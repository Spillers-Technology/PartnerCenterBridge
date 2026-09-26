using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph.Workflows;
using PartnerCenterBridge.PartnerCenter;
using static PartnerCenterBridge.Core.Operations.EvidenceRenderer;

namespace PartnerCenterBridge.Graph.Operations;

/// <summary>
/// Policy-driven offboarding: plan -> apply -> verify. Ordering is deliberate: sign-in is blocked and
/// sessions revoked first, the mailbox is converted to shared before anything that can remove a
/// license (license removal and group cleanup), and license/group removal only runs once the
/// conversion has been verified -- removing the license from an unconverted mailbox starts its
/// deletion clock. Policy options PCB cannot execute appear as ineligible items with the reason.
/// </summary>
public class OffboardingOperation : IOffboardingService
{
    public const string OperationId = "offboarding";
    public const string OperationName = "Offboarding";

    // Plan item actions, in execution order.
    public const string BlockSignIn = "BlockSignIn";
    public const string RevokeSessions = "RevokeSessions";
    public const string ConvertToShared = "ConvertToShared";
    public const string SetForwarding = "SetForwarding";
    public const string HideFromGal = "HideFromGal";
    public const string GrantManagerAccess = "GrantManagerAccess";
    public const string RemoveMember = "RemoveMember";
    public const string RemoveLicense = "RemoveLicense";
    public const string RetireDevice = "RetireDevice";
    public const string FollowUp = "FollowUpReminder";

    private readonly TenantGraphRest _graph;
    private readonly IExchangeOnlineService _exchange;
    private readonly IExchangeCapability _exchangeCapability;
    private readonly TimeProvider _time;

    public OffboardingOperation(
        ITokenProvider tokens, IHttpClientFactory httpFactory, IOptions<IntuneOptions> options,
        IExchangeOnlineService exchange, IExchangeCapability exchangeCapability)
        : this(new TenantGraphRest(tokens, httpFactory, options), exchange, exchangeCapability, TimeProvider.System) { }

    internal OffboardingOperation(TenantGraphRest graph, IExchangeOnlineService exchange, IExchangeCapability exchangeCapability, TimeProvider time)
    {
        _graph = graph;
        _exchange = exchange;
        _exchangeCapability = exchangeCapability;
        _time = time;
    }

    private sealed record UserState(
        string Id, string DisplayName, string? Upn, bool? AccountEnabled, bool Synced,
        List<(string SkuId, bool Direct)> Licenses);

    private sealed class PlanState
    {
        public required OperationPlan Plan { get; init; }
        public required UserState User { get; init; }
        /// <summary>Ineligible items the policy asked for: not doing them counts against success.</summary>
        public HashSet<string> RequiredIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ConversionRequested { get; set; }
    }

    public async Task<OperationPlan> PlanAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default) =>
        (await BuildPlanAsync(tenant, userId, policy, ct)).Plan;

    private async Task<PlanState> BuildPlanAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new OperationInputException("userId is required.");
        var graph = await _graph.CreateAsync(tenant, ct);
        var user = await ReadUserAsync(graph, userId.Trim(), ct);

        var plan = new OperationPlan
        {
            OperationId = OperationId,
            OperationName = OperationName,
            TenantId = tenant.Id.ToString(),
            Target = new OperationTarget("user", user.Id, user.DisplayName),
            Limitations =
            {
                "Mailbox content, OneDrive files and Teams/group ownership are not transferred by this operation.",
                "Directory role assignments are listed but never removed automatically."
            }
        };
        var state = new PlanState { Plan = plan, User = user };
        var items = plan.Items;

        plan.Preflight.Add(new("User", FindingStatus.Ok, user.Upn is null ? user.DisplayName : $"{user.DisplayName} ({user.Upn})"));
        plan.Preflight.Add(new("Sign-in", FindingStatus.Info, user.AccountEnabled == false ? "already blocked" : "enabled"));
        if (user.Synced)
        {
            plan.Preflight.Add(new("Directory sync", FindingStatus.Warning,
                "Account is synced from on-premises AD (hybrid). Graph cannot disable it; disable it in on-premises AD or the next sync can re-enable it."));
            plan.Warnings.Add("Hybrid account: this user is synced from on-premises AD. Disable the account in on-premises AD as well; a cloud-only block would not hold.");
        }
        else
            plan.Preflight.Add(new("Directory sync", FindingStatus.Ok, "Cloud-only account."));

        // 1. Sign-in.
        if (policy.BlockSignIn)
        {
            var item = NewItem("block-sign-in", BlockSignIn, "user", user.Id, user.DisplayName, "SignIn", destructive: false);
            if (user.Synced)
            {
                item.Eligible = false;
                item.Reason = "Account is synced from on-premises AD; Graph cannot change accountEnabled on a synced account. Disable it in on-premises AD.";
                state.RequiredIds.Add(item.Id);
            }
            else if (user.AccountEnabled == false)
                item.Reason = "Already blocked; re-applied and verified.";
            items.Add(item);
        }
        if (policy.RevokeSessions)
            items.Add(NewItem("revoke-sessions", RevokeSessions, "user", user.Id, user.DisplayName, "SignIn", destructive: false));

        // 2. Mailbox (Exchange Online) -- strictly before anything that can remove a license.
        var needsExchange = policy.ConvertMailboxToShared || !string.IsNullOrWhiteSpace(policy.ForwardTo)
                            || policy.HideFromGal || policy.ManagerAccess != ManagerAccessMode.None;
        var exchange = needsExchange ? _exchangeCapability.Check() : new ExchangeCapabilityStatus(true, null);
        if (needsExchange)
            plan.Preflight.Add(exchange.Available
                ? new("Exchange Online", FindingStatus.Ok, "Configured")
                : new("Exchange Online", FindingStatus.Warning, exchange.MissingDependency));

        var mailboxName = user.Upn ?? user.DisplayName;
        PlanItem? convert = null;
        if (policy.ConvertMailboxToShared)
        {
            state.ConversionRequested = true;
            convert = NewItem("convert-mailbox", ConvertToShared, "mailbox", user.Upn ?? user.Id, mailboxName, "Mailbox", destructive: false);
            if (!exchange.Available)
            {
                convert.Eligible = false;
                convert.Reason = exchange.MissingDependency;
                state.RequiredIds.Add(convert.Id);
            }
            items.Add(convert);
        }
        if (!string.IsNullOrWhiteSpace(policy.ForwardTo))
        {
            var fwd = NewItem("set-forwarding", SetForwarding, "mailbox", user.Upn ?? user.Id, policy.ForwardTo.Trim(), "Mailbox", destructive: false);
            if (!policy.ConvertMailboxToShared)
            {
                fwd.Eligible = false;
                fwd.Reason = "PCB sets forwarding together with shared-mailbox conversion only; enable convertMailboxToShared to forward.";
                state.RequiredIds.Add(fwd.Id);
            }
            else if (!exchange.Available)
            {
                fwd.Eligible = false;
                fwd.Reason = exchange.MissingDependency;
                state.RequiredIds.Add(fwd.Id);
            }
            items.Add(fwd);
        }
        if (policy.HideFromGal)
        {
            items.Add(NewItem("hide-from-gal", HideFromGal, "mailbox", user.Upn ?? user.Id, mailboxName, "Mailbox", false,
                eligible: false,
                reason: "PCB has no Exchange operation for hiding a mailbox from the address list yet; set HiddenFromAddressListsEnabled in the Exchange admin center."));
            state.RequiredIds.Add("hide-from-gal");
        }
        if (policy.ManagerAccess == ManagerAccessMode.FullAccess)
        {
            items.Add(NewItem("manager-access", GrantManagerAccess, "mailbox", user.Upn ?? user.Id, mailboxName, "Mailbox", false,
                eligible: false,
                reason: "PCB has no Exchange operation for granting mailbox permissions yet; grant the manager Full Access in the Exchange admin center."));
            state.RequiredIds.Add("manager-access");
        }

        // Anything that can remove a license waits for a verified conversion when one was requested.
        string? gate = null;
        if (convert is not null)
            gate = convert.Eligible
                ? "Runs only after the mailbox conversion to shared is verified."
                : $"Mailbox conversion to shared was requested but cannot run ({convert.Reason}); kept so the mailbox is not put on the deletion path.";

        // 3. Group cleanup.
        if (policy.GroupCleanup != GroupCleanupMode.None)
        {
            try
            {
                var memberships = await GroupClassifier.DirectMembershipsAsync(graph, user.Id, ct);
                foreach (var m in memberships.Where(m => m.IsGroup || m.Category == MembershipCategory.DirectoryRole)
                             .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var isRole = m.Category == MembershipCategory.DirectoryRole;
                    var item = NewItem($"{(isRole ? "role" : "group")}:{m.Id}", RemoveMember, isRole ? "directoryRole" : "group",
                        m.Id, m.DisplayName, m.Category, destructive: !isRole);
                    var removable = m.Modifiable
                                    || (policy.GroupCleanup == GroupCleanupMode.RemoveAll && m.Category == MembershipCategory.RoleAssignable);
                    if (isRole)
                    {
                        item.Eligible = false;
                        item.Reason = "Directory role; not removed by offboarding. Review the user's role assignments.";
                    }
                    else if (!removable)
                    {
                        item.Eligible = false;
                        item.Reason = m.Category == MembershipCategory.RoleAssignable
                            ? "Role-assignable group; left for review under the RemoveAssignable policy."
                            : m.Reason;
                    }
                    else if (gate is not null)
                    {
                        item.Eligible = convert!.Eligible;
                        item.Reason = gate;
                        if (!item.Eligible) state.RequiredIds.Add(item.Id);
                    }
                    items.Add(item);
                }
                if (memberships.Count == 0)
                    plan.Preflight.Add(new("Group memberships", FindingStatus.Info, "No direct memberships."));
            }
            catch (Exception ex) when (ex is GraphRequestException)
            {
                items.Add(NewItem("groups", RemoveMember, "group", "", "Group memberships", "Group", true, eligible: false,
                    reason: GraphErrors.IsForbidden(ex)
                        ? "Could not read group memberships: the PCB app registration lacks GroupMember.Read.All in this tenant (Graph 403)."
                        : $"Could not read group memberships: {GraphErrors.Describe(ex)}"));
                state.RequiredIds.Add("groups");
            }
        }

        // 4. Licenses.
        if (policy.RemoveLicenses)
        {
            var names = await LicenseNamesAsync(graph, user.Id, ct);
            foreach (var (skuId, direct) in user.Licenses)
            {
                var item = NewItem($"license:{skuId}", RemoveLicense, "license", skuId,
                    names.TryGetValue(skuId, out var n) ? n : skuId, "License", destructive: true);
                if (!direct)
                {
                    item.Eligible = false;
                    item.Reason = "Assigned through group-based licensing; it goes away only when the licensing group membership is removed, and is re-checked after the run.";
                }
                else if (gate is not null)
                {
                    item.Eligible = convert!.Eligible;
                    item.Reason = gate;
                    if (!item.Eligible) state.RequiredIds.Add(item.Id);
                }
                items.Add(item);
            }
            if (user.Licenses.Count == 0)
                plan.Preflight.Add(new("Licenses", FindingStatus.Info, "No licenses assigned."));
        }

        // 5. Devices.
        if (policy.WipeDevices == DeviceWipeMode.Retire)
        {
            try
            {
                var devices = await graph.GetAllAsync(
                    $"/users/{Uri.EscapeDataString(user.Id)}/managedDevices?$select=id,deviceName,operatingSystem,managementState", ct);
                foreach (var d in devices)
                {
                    var id = Str(d, "id") ?? "";
                    var name = Str(d, "deviceName") ?? id;
                    var os = Str(d, "operatingSystem");
                    items.Add(NewItem($"device:{id}", RetireDevice, "managedDevice", id,
                        os is null ? name : $"{name} ({os})", "Device", destructive: true));
                }
                if (devices.Count == 0)
                    plan.Preflight.Add(new("Managed devices", FindingStatus.Info, "No Intune-managed devices."));
            }
            catch (Exception ex) when (ex is GraphRequestException)
            {
                items.Add(NewItem("devices", RetireDevice, "managedDevice", "", "Managed devices", "Device", true, eligible: false,
                    reason: GraphErrors.IsForbidden(ex)
                        ? "The PCB app registration lacks DeviceManagementManagedDevices.Read.All / PrivilegedOperations.All in this tenant (Graph 403); devices were not retired."
                        : $"Could not read managed devices: {GraphErrors.Describe(ex)}"));
                state.RequiredIds.Add("devices");
            }
        }

        // 6. Follow-up (recorded only).
        if (policy.FollowUpDays > 0)
        {
            var due = _time.GetUtcNow().AddDays(policy.FollowUpDays).ToString("yyyy-MM-dd");
            items.Add(NewItem("follow-up", FollowUp, "user", user.Id, user.DisplayName, "FollowUp", false, eligible: false,
                reason: $"Recorded in evidence only; PCB does not schedule reminders. Review and delete the account on or after {due}."));
            plan.Warnings.Add($"Follow-up: review and delete {user.DisplayName} on or after {due} ({policy.FollowUpDays} days). Not scheduled by PCB.");
        }

        return state;
    }

    public async Task<OperationEvidence> ApplyAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default)
    {
        var state = await BuildPlanAsync(tenant, userId, policy, ct);
        var plan = state.Plan;
        var user = state.User;
        var graph = await _graph.CreateAsync(tenant, ct);
        var startedAt = _time.GetUtcNow();

        var e = new OperationEvidence
        {
            OperationId = OperationId,
            OperationName = OperationName,
            Target = plan.Target,
            Preflight = plan.Preflight,
            Plan = plan.Items,
            Warnings = plan.Warnings.ToList(),
            Limitations = plan.Limitations.ToList()
        };
        var changes = new Dictionary<string, ChangeResult>(StringComparer.OrdinalIgnoreCase);
        // Items whose request has been sent and whose answer has not arrived yet.
        var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ChangeResult Record(PlanItem item, bool attempted, bool ok, string? detail)
        {
            if (!inFlight.Remove(item.Id) || !changes.TryGetValue(item.Id, out var c))
            {
                c = new ChangeResult { PlanItemId = item.Id, Action = item.Action, ObjectName = item.ObjectName };
                changes[item.Id] = c;
                e.Changes.Add(c);
            }
            c.Attempted = attempted;
            c.Succeeded = ok;
            c.Detail = detail;
            if (attempted && !ok) e.Failures.Add($"{Label(item)}: {detail}");
            return c;
        }
        // Recorded as attempted before the request is sent: if the run is interrupted while it is in
        // flight, Microsoft may already have applied it, and the evidence must say so.
        void Sending(PlanItem item)
        {
            var c = new ChangeResult
            {
                PlanItemId = item.Id, Action = item.Action, ObjectName = item.ObjectName,
                Attempted = true, Succeeded = false, Detail = ChangeResult.InterruptedInFlight
            };
            changes[item.Id] = c;
            e.Changes.Add(c);
            inFlight.Add(item.Id);
        }
        async Task Run(PlanItem item, Func<Task<string>> action)
        {
            Sending(item);
            try { Record(item, true, true, await action()); }
            catch (GraphRequestException ex)
            {
                Record(item, true, false, GraphErrors.IsForbidden(ex)
                    ? $"Insufficient privileges ({GraphErrors.Describe(ex)})"
                    : GraphErrors.Describe(ex));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record(item, true, false, ChangeResult.NoResponse(ex)); // no answer: it may still have been applied
            }
        }
        var uid = Uri.EscapeDataString(user.Id);

        // Verification results, keyed by plan-item id (never by display name: two groups can share one).
        var verified = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(PlanItem item, bool passed, string detail)
        {
            verified[item.Id] = passed;
            e.Verification.Add(new VerificationCheck(Label(item), passed, detail, item.Id));
        }
        void Pending(PlanItem item, string detail)
        {
            verified[item.Id] = false;
            pending.Add(item.Id);
            e.Verification.Add(VerificationCheck.NotVerifiable(Label(item), detail, item.Id));
        }
        bool Attempted(PlanItem? i) => i is not null && changes.TryGetValue(i.Id, out var c) && c.Attempted;

        foreach (var item in plan.Items.Where(i => !i.Eligible))
            Record(item, false, false, "Not performed: " + item.Reason);

        var block = plan.Items.FirstOrDefault(i => i.Action == BlockSignIn && i.Eligible);
        var revoke = plan.Items.FirstOrDefault(i => i.Action == RevokeSessions && i.Eligible);
        var convert = plan.Items.FirstOrDefault(i => i.Action == ConvertToShared && i.Eligible);
        var forward = plan.Items.FirstOrDefault(i => i.Action == SetForwarding && i.Eligible);
        var licenseItems = plan.Items.Where(i => i.Action == RemoveLicense && i.Eligible).ToList();
        // Every SKU the policy intends to remove, including group-inherited ones PCB cannot remove directly.
        var requestedLicenses = plan.Items.Where(i => i.Action == RemoveLicense && i.ObjectId.Length > 0).ToList();
        var conversionVerified = false;
        MailboxInfo? mailboxAfter = null;
        string? mailboxVerifyError = null;
        // The sign-in cutoff read just before the revoke: the revoke is proven only by it moving forward.
        SessionCutoff? cutoffBefore = null;

        try
        {
            // 1. Sign-in.
            if (block is not null)
                await Run(block, async () => { await graph.PatchAsync($"/users/{uid}", new { accountEnabled = false }, ct); return "accountEnabled=false"; });
            if (revoke is not null)
            {
                cutoffBefore = await WorkflowVerify.ReadSessionCutoffAsync(graph, user.Id, ct);
                await Run(revoke, async () => { await graph.PostAsync($"/users/{uid}/revokeSignInSessions", new { }, ct); return "revoked"; });
            }

            // 2. Mailbox conversion (+ forwarding in the same Exchange call), then verify it before
            // anything that can remove a license.
            if (convert is not null)
            {
                Sending(convert);
                if (forward is not null) Sending(forward);
                try
                {
                    var exo = await _exchange.ConvertToSharedAsync(tenant, convert.ObjectId,
                        forward is null ? null : policy.ForwardTo!.Trim(), deliverToMailboxAndForward: false, ct);
                    var convStep = exo.Steps.FirstOrDefault(s => s.Name.Contains("shared", StringComparison.OrdinalIgnoreCase));
                    var failedStep = exo.Steps.FirstOrDefault(s => !s.Success);
                    Record(convert, true, convStep?.Success == true,
                        convStep?.Success == true ? "Set-Mailbox -Type Shared" : failedStep?.Detail ?? "Exchange did not report a conversion step.");
                    if (forward is not null)
                    {
                        var fwdStep = exo.Steps.FirstOrDefault(s => s.Name.Contains("forward", StringComparison.OrdinalIgnoreCase));
                        Record(forward, true, fwdStep?.Success == true,
                            fwdStep?.Success == true ? $"ForwardingSmtpAddress={policy.ForwardTo!.Trim()}" : failedStep?.Detail ?? "Exchange did not report a forwarding step.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Record(convert, true, false, ex.Message);
                    if (forward is not null) Record(forward, true, false, ex.Message);
                }

                try
                {
                    mailboxAfter = await _exchange.GetMailboxAsync(tenant, convert.ObjectId, ct);
                    if (mailboxAfter is null) mailboxVerifyError = "Exchange returned no mailbox on re-read.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    mailboxVerifyError = ex.Message;
                }
                conversionVerified = string.Equals(mailboxAfter?.RecipientTypeDetails, "SharedMailbox", StringComparison.OrdinalIgnoreCase);
            }
            var gateOpen = !state.ConversionRequested || conversionVerified;
            const string gateReason = "Skipped: mailbox conversion to shared was not verified; removing licenses now could put the mailbox on the deletion path.";

            // 3. Groups.
            foreach (var item in plan.Items.Where(i => i.Action == RemoveMember && i.Eligible))
            {
                if (!gateOpen) { Record(item, false, false, gateReason); continue; }
                await Run(item, async () =>
                {
                    try
                    {
                        await graph.DeleteAsync($"/groups/{Uri.EscapeDataString(item.ObjectId)}/members/{uid}/$ref", ct);
                        return "removed";
                    }
                    catch (Exception ex) when (GraphErrors.IsNotFound(ex)) { return "already not a member"; }
                });
            }

            // 4. Licenses (one assignLicense call for every eligible SKU).
            if (licenseItems.Count > 0)
            {
                if (!gateOpen)
                    foreach (var item in licenseItems) Record(item, false, false, gateReason);
                else
                {
                    foreach (var item in licenseItems) Sending(item);
                    try
                    {
                        await graph.PostAsync($"/users/{uid}/assignLicense", new
                        {
                            addLicenses = Array.Empty<object>(),
                            removeLicenses = licenseItems.Select(i => i.ObjectId).ToArray()
                        }, ct);
                        foreach (var item in licenseItems) Record(item, true, true, "removed");
                    }
                    catch (GraphRequestException ex)
                    {
                        foreach (var item in licenseItems) Record(item, true, false, GraphErrors.Describe(ex));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        foreach (var item in licenseItems) Record(item, true, false, ChangeResult.NoResponse(ex));
                    }
                }
            }

            // 5. Devices.
            foreach (var item in plan.Items.Where(i => i.Action == RetireDevice && i.Eligible))
                await Run(item, async () =>
                {
                    await graph.PostAsync($"/deviceManagement/managedDevices/{Uri.EscapeDataString(item.ObjectId)}/retire", new { }, ct);
                    return "retire issued";
                });

            // --- Verification: re-read everything that was attempted, plus every requested license. ---
            var needUser = Attempted(block) || Attempted(revoke) || requestedLicenses.Count > 0;
            JsonElement? userAfter = null;
            string? userVerifyError = null;
            if (needUser)
            {
                try
                {
                    using var doc = await graph.GetAsync($"/users/{uid}?$select=id,accountEnabled,signInSessionsValidFromDateTime,assignedLicenses,licenseAssignmentStates", ct);
                    userAfter = doc.RootElement.Clone();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { userVerifyError = GraphErrors.Describe(ex); }
            }
            if (Attempted(block))
            {
                if (userAfter is { } u)
                {
                    var disabled = u.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind == JsonValueKind.False;
                    Check(block!, disabled, disabled ? "accountEnabled is false on re-read." : "Account is still enabled on re-read.");
                }
                else Check(block!, false, $"Could not re-read the user: {userVerifyError}");
            }
            if (Attempted(revoke))
            {
                if (userAfter is { } u)
                {
                    var (ok, detail) = WorkflowVerify.EvaluateCutoff(
                        cutoffBefore ?? new SessionCutoff(false, null, "not read"), WorkflowVerify.ParseCutoff(u), startedAt);
                    Check(revoke!, ok, detail);
                }
                else Check(revoke!, false, $"Could not re-read the user: {userVerifyError}");
            }
            if (Attempted(convert))
                Check(convert!, conversionVerified, conversionVerified
                    ? "Mailbox is a SharedMailbox on re-read."
                    : mailboxVerifyError ?? $"Mailbox type on re-read is {mailboxAfter?.RecipientTypeDetails ?? "unknown"}.");
            if (Attempted(forward))
            {
                // Exact address (Exchange may prefix "smtp:"), and the requested delivery mode: forward
                // only, no copy kept in the mailbox.
                var fwd = mailboxAfter?.ForwardingSmtpAddress ?? "";
                var addressOk = string.Equals(BareSmtp(fwd), policy.ForwardTo!.Trim(), StringComparison.OrdinalIgnoreCase);
                var deliverOk = mailboxAfter is { DeliverToMailboxAndForward: false };
                var ok = addressOk && deliverOk;
                Check(forward!, ok,
                    ok ? $"Forwarding to {BareSmtp(fwd)} (no copy kept in the mailbox) on re-read."
                    : mailboxAfter is null ? mailboxVerifyError ?? "Mailbox could not be re-read."
                    : !addressOk ? $"Forwarding on re-read is '{fwd}', not {policy.ForwardTo!.Trim()}."
                    : "DeliverToMailboxAndForward is true on re-read; forward-only was requested.");
            }

            var groupItems = plan.Items.Where(i => i.Action == RemoveMember && Attempted(i)).ToList();
            if (groupItems.Count > 0)
            {
                HashSet<string>? after = null;
                string? err = null;
                try
                {
                    after = (await GroupClassifier.DirectMembershipsAsync(graph, user.Id, ct)).Select(m => m.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { err = GraphErrors.Describe(ex); }
                foreach (var item in groupItems)
                {
                    if (after is null) { Check(item, false, $"Could not re-read memberships: {err}"); continue; }
                    var gone = !after.Contains(item.ObjectId);
                    Check(item, gone, gone ? "No longer a member on re-read." : "Still a member on re-read.");
                }
            }

            // Every requested SKU's final state, including group-inherited ones: a license still assigned
            // after offboarding is unfinished work whichever way it was assigned.
            var remainingInherited = new List<PlanItem>();
            foreach (var item in requestedLicenses)
            {
                var inherited = !item.Eligible && IsGroupBased(item);
                if (!Attempted(item) && !inherited) continue; // gated/skipped direct SKUs are already reported as not done
                if (userAfter is not { } u) { Check(item, false, $"Could not re-read licenses: {userVerifyError}"); continue; }
                var still = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
                            && al.EnumerateArray().Any(l => string.Equals(Str(l, "skuId"), item.ObjectId, StringComparison.OrdinalIgnoreCase));
                if (!inherited)
                    Check(item, !still, still ? "License still assigned on re-read." : "License no longer assigned on re-read.");
                else if (still)
                {
                    remainingInherited.Add(item);
                    Check(item, false, "Still assigned through group-based licensing on re-read. It is removed only by removing the user from the licensing group (or changing that group's licenses); group license changes can also take a few minutes to process.");
                }
                else
                    Check(item, true, "No longer assigned on re-read (removed with the licensing group membership).");
            }
            if (remainingInherited.Count > 0)
                e.Warnings.Add($"{Count(remainingInherited.Count, "license")} still assigned through group-based licensing ({string.Join(", ", remainingInherited.Select(i => i.ObjectName))}). " +
                               "Group-based licenses cannot be removed directly: remove the user from the licensing group, or change the group's license assignment.");

            foreach (var item in plan.Items.Where(i => i.Action == RetireDevice && Attempted(i)))
            {
                try
                {
                    using var doc = await graph.GetAsync(
                        $"/deviceManagement/managedDevices/{Uri.EscapeDataString(item.ObjectId)}?$select=id,managementState", ct);
                    var ms = Str(doc.RootElement, "managementState") ?? "";
                    // Graph's managementState enum has no "retired" value
                    // (https://learn.microsoft.com/en-us/graph/api/resources/intune-devices-managementstate):
                    // a completed retire shows up as the managed device record disappearing (404,
                    // below). While the record exists, the retire is at best requested.
                    switch (ms.ToLowerInvariant())
                    {
                        case "retirepending" or "retireissued":
                            Pending(item, $"Retire requested; completion not yet confirmed (managementState={ms}). The device completes it at its next check-in.");
                            break;
                        case "retirefailed" or "retirecanceled":
                            Check(item, false, $"Retire did not complete: managementState={ms}.");
                            break;
                        default:
                            Check(item, false, $"managementState on re-read is '{ms}'; the retire is not reflected (a completed retire removes the device record).");
                            break;
                    }
                }
                catch (Exception ex) when (GraphErrors.IsNotFound(ex)) { Check(item, true, "The managed device record is gone on re-read (Graph 404): the retire completed."); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Check(item, false, $"Could not re-read device: {GraphErrors.Describe(ex)}"); }
            }

        }
        catch (Exception ex)
        {
            // Cancelled (or an unexpected error) mid-apply or mid-verification: keep the evidence of
            // what already changed and of what was already verified.
            foreach (var item in plan.Items.Where(i => i.Eligible && !changes.ContainsKey(i.Id)))
                Record(item, false, false, "Interrupted: the run stopped before this step's request was sent; nothing was changed for it.");
            foreach (var id in inFlight)
                e.Failures.Add($"{Label(plan.Items.First(i => i.Id == id))}: {ChangeResult.InterruptedInFlight}");
            foreach (var item in plan.Items.Where(i => (Attempted(i) || (i.Action == RemoveLicense && IsGroupBased(i))) && !verified.ContainsKey(i.Id)))
                Check(item, false, "Not verified: the run was interrupted before the verification re-read.");
            e.Failures.Add($"Interrupted: {(ex is OperationCanceledException ? "the request was cancelled" : ex.Message)}.");
            Finish();
            e.TicketNotes += " The run was interrupted before it finished; re-run the plan to see the current state.";
            throw new OperationInterruptedException(e, ex);
        }

        Finish();
        return e;

        // --- Outcome and notes ---
        void Finish()
        {
            var outcomes = new List<ItemOutcome>();
            foreach (var item in plan.Items)
            {
                var c = changes.GetValueOrDefault(item.Id);
                if (c is { Attempted: true })
                    outcomes.Add(new ItemOutcome(true, c.Succeeded, verified.GetValueOrDefault(item.Id), Unverifiable: pending.Contains(item.Id)));
                else if (state.RequiredIds.Contains(item.Id) || (item.Eligible && c is { Attempted: false })
                         || (verified.TryGetValue(item.Id, out var ok) && !ok))
                    outcomes.Add(new ItemOutcome(false, false, false, RequiredButNotDone: true));
            }
            e.Outcome = OutcomeRules.Derive(outcomes);
            e.TicketNotes = BuildNotes(state, e, changes, verified, pending);
        }
    }

    private static bool IsGroupBased(PlanItem item) =>
        item.Reason?.StartsWith("Assigned through group-based", StringComparison.Ordinal) == true;

    /// <summary>Exchange reports forwarding as "smtp:user@domain" (or bare); strip only that prefix.</summary>
    private static string BareSmtp(string address)
    {
        var trimmed = address.Trim();
        return trimmed.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? trimmed[5..] : trimmed;
    }

    /// <summary>Legacy-shaped steps for the existing terminate response, one per plan item.</summary>
    public static List<ProvisioningStep> ToSteps(OperationEvidence e)
    {
        // Checks are matched to items by plan-item id; rows written before checks carried one fall
        // back to the label.
        var linked = e.Verification.Any(v => v.PlanItemId is not null);
        VerificationCheck? CheckFor(PlanItem item) => linked
            ? e.Verification.FirstOrDefault(v => v.PlanItemId == item.Id)
            : e.Verification.FirstOrDefault(v => v.Name == Label(item));

        var steps = new List<ProvisioningStep>();
        foreach (var item in e.Plan)
        {
            var c = e.Changes.FirstOrDefault(x => x.PlanItemId == item.Id);
            var check = CheckFor(item);
            if (c is { Attempted: true })
            {
                var ok = c.Succeeded && check?.Passed == true;
                steps.Add(new(Label(item), ok,
                    ok ? $"{c.Detail}; verified"
                    : c.Succeeded && check?.Unverifiable == true ? $"{c.Detail}; not yet confirmed: {check.Detail}"
                    : c.Succeeded ? $"{c.Detail}; not verified: {check?.Detail}" : c.Detail));
            }
            else if (IsGroupBased(item) && check is not null)
            {
                // Inherited license: what matters is whether it is gone at the end.
                steps.Add(new(Label(item), check.Passed, (check.Passed ? "Removed with the licensing group: " : "Still assigned: ") + check.Detail));
            }
            else
            {
                // Items the user cannot act on (dynamic groups, group-based licenses, roles, follow-up)
                // are informational; blocked policy actions are failures.
                var informational = !item.Eligible
                    && (item.Category is MembershipCategory.Dynamic or MembershipCategory.OnPremSynced
                            or MembershipCategory.MailEnabledSecurity or MembershipCategory.Distribution
                            or MembershipCategory.DirectoryRole or MembershipCategory.RoleAssignable
                            or MembershipCategory.Other or "FollowUp"
                        || IsGroupBased(item));
                var why = (c?.Detail ?? item.Reason ?? "").Replace("Not performed: ", "");
                steps.Add(new(Label(item), informational, (informational ? "Left in place: " : "Not performed: ") + why));
            }
        }
        return steps;
    }

    public static string Label(PlanItem item) => item.Action switch
    {
        BlockSignIn => "Block sign-in",
        RevokeSessions => "Revoke sessions",
        ConvertToShared => "Convert mailbox to shared",
        SetForwarding => $"Forward mail to {item.ObjectName}",
        HideFromGal => "Hide from address list",
        GrantManagerAccess => "Grant manager Full Access",
        RemoveMember => $"Remove from {item.ObjectName}",
        RemoveLicense => $"Remove license {item.ObjectName}",
        RetireDevice => $"Retire {item.ObjectName}",
        FollowUp => "Follow-up reminder",
        _ => item.Action
    };

    private static string BuildNotes(PlanState state, OperationEvidence e,
        Dictionary<string, ChangeResult> changes, Dictionary<string, bool> verified, HashSet<string> pending)
    {
        var sb = new StringBuilder();
        var user = state.User;
        sb.Append($"Offboarded {user.DisplayName}{(user.Upn is null ? "" : $" ({user.Upn})")}. ");

        string Status(PlanItem item)
        {
            var c = changes.GetValueOrDefault(item.Id);
            if (c is null || !c.Attempted)
                return IsGroupBased(item) && verified.TryGetValue(item.Id, out var gone) ? (gone ? "group-removed" : "remaining") : "skipped";
            if (!c.Succeeded) return c.Detail == ChangeResult.InterruptedInFlight ? "unknown" : "failed";
            if (pending.Contains(item.Id)) return "pending";
            return verified.GetValueOrDefault(item.Id) ? "verified" : "unverified";
        }

        foreach (var item in e.Plan.Where(i => i.Action is BlockSignIn or RevokeSessions or ConvertToShared or SetForwarding or HideFromGal or GrantManagerAccess))
        {
            var label = Label(item);
            sb.Append(Status(item) switch
            {
                "verified" => $"{label}: done and verified. ",
                "unverified" => $"{label}: reported done but NOT confirmed on re-read. ",
                "failed" => $"{label}: failed ({changes[item.Id].Detail?.TrimEnd('.')}). ",
                "unknown" => $"{label}: request sent, but the run was interrupted before Microsoft answered; it may or may not have been applied. ",
                _ => $"{label}: not performed - {(item.Reason ?? changes.GetValueOrDefault(item.Id)?.Detail)?.TrimEnd('.')}. "
            });
        }

        void Summarize(string action, string noun, string verb)
        {
            var items = e.Plan.Where(i => i.Action == action && i.ObjectId.Length > 0).ToList();
            var blocked = e.Plan.FirstOrDefault(i => i.Action == action && i.ObjectId.Length == 0);
            if (blocked is not null) { sb.Append($"{char.ToUpperInvariant(noun[0])}{noun[1..]}s: not processed - {blocked.Reason?.TrimEnd('.')}. "); return; }
            if (items.Count == 0) return;
            var byStatus = items.GroupBy(Status).ToDictionary(g => g.Key, g => g.ToList());
            var parts = new List<string>();
            if (byStatus.TryGetValue("verified", out var v)) parts.Add($"{verb} {Count(v.Count, noun)} (verified)");
            if (byStatus.TryGetValue("unverified", out var uv)) parts.Add($"{Count(uv.Count, noun)} reported {verb.ToLowerInvariant()} but not confirmed");
            if (byStatus.TryGetValue("failed", out var f)) parts.Add($"{Count(f.Count, noun)} failed ({string.Join(", ", f.Select(i => i.ObjectName))})");
            if (byStatus.TryGetValue("unknown", out var un))
                parts.Add($"{Count(un.Count, noun)} sent but interrupted before Microsoft answered, so may or may not have been {verb.ToLowerInvariant()} ({string.Join(", ", un.Select(i => i.ObjectName))})");
            if (byStatus.TryGetValue("pending", out var p))
                parts.Add($"{(action == RetireDevice ? "retire " : "")}requested for {Count(p.Count, noun)}; completion not yet confirmed ({string.Join(", ", p.Select(i => i.ObjectName))})");
            if (byStatus.TryGetValue("group-removed", out var gr)) parts.Add($"{Count(gr.Count, noun)} removed with the licensing group membership (verified)");
            if (byStatus.TryGetValue("remaining", out var rem)) parts.Add($"{Count(rem.Count, noun)} still assigned through group-based licensing ({string.Join(", ", rem.Select(i => i.ObjectName))})");
            if (byStatus.TryGetValue("skipped", out var s))
            {
                var reasons = s.GroupBy(i => (changes.GetValueOrDefault(i.Id)?.Detail ?? i.Reason ?? "").Replace("Not performed: ", "").TrimEnd('.'));
                parts.AddRange(reasons.Select(r => $"{Count(r.Count(), noun)} left in place: {r.Key}"));
            }
            sb.Append(string.Join("; ", parts)).Append(". ");
        }
        Summarize(RemoveMember, "group membership", "Removed");
        Summarize(RemoveLicense, "license", "Removed");
        Summarize(RetireDevice, "device", "Retired");

        foreach (var w in e.Warnings) sb.Append(w.TrimEnd('.')).Append(". ");
        sb.Append($"Outcome: {Describe(e.Outcome)}.");
        return sb.ToString().Trim();
    }

    // --- helpers ---

    private static PlanItem NewItem(string id, string action, string objectType, string objectId, string objectName,
        string category, bool destructive, bool eligible = true, string? reason = null) => new()
    {
        Id = id, Action = action, ObjectType = objectType, ObjectId = objectId, ObjectName = objectName,
        Category = category, Destructive = destructive, Eligible = eligible, Reason = reason
    };

    private static async Task<UserState> ReadUserAsync(GraphRestClient graph, string userId, CancellationToken ct)
    {
        JsonElement u;
        try
        {
            using var doc = await graph.GetAsync(
                $"/users/{Uri.EscapeDataString(userId)}?$select=id,displayName,userPrincipalName,accountEnabled,onPremisesSyncEnabled,assignedLicenses,licenseAssignmentStates", ct);
            u = doc.RootElement.Clone();
        }
        catch (Exception ex) when (GraphErrors.IsNotFound(ex))
        {
            throw new OperationInputException($"User '{userId}' was not found in this tenant.");
        }

        var skus = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Select(l => Str(l, "skuId")).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var states = u.TryGetProperty("licenseAssignmentStates", out var ls) && ls.ValueKind == JsonValueKind.Array
            ? ls.EnumerateArray().ToList() : new List<JsonElement>();
        var licenses = skus.Select(sku =>
        {
            var forSku = states.Where(s => string.Equals(Str(s, "skuId"), sku, StringComparison.OrdinalIgnoreCase)).ToList();
            // No state info -> assume a direct assignment (the removal will then report honestly).
            var direct = forSku.Count == 0 || forSku.Any(s => string.IsNullOrEmpty(Str(s, "assignedByGroup")));
            return (sku, direct);
        }).ToList();

        var id = Str(u, "id") ?? userId;
        var upn = Str(u, "userPrincipalName");
        bool? enabled = u.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind is JsonValueKind.True or JsonValueKind.False ? ae.GetBoolean() : null;
        var synced = u.TryGetProperty("onPremisesSyncEnabled", out var sy) && sy.ValueKind == JsonValueKind.True;
        return new UserState(id, Str(u, "displayName") ?? upn ?? id, upn, enabled, synced, licenses);
    }

    private static async Task<Dictionary<string, string>> LicenseNamesAsync(GraphRestClient graph, string userId, CancellationToken ct)
    {
        try
        {
            return (await graph.GetAllAsync($"/users/{Uri.EscapeDataString(userId)}/licenseDetails?$select=skuId,skuPartNumber", ct))
                .Where(l => Str(l, "skuId") is not null)
                .GroupBy(l => Str(l, "skuId")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Str(g.First(), "skuPartNumber") ?? g.Key, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is GraphRequestException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // names are cosmetic
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

using System.Text;
using System.Text.Json;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using static PartnerCenterBridge.Core.Operations.EvidenceRenderer;

namespace PartnerCenterBridge.Graph.Operations;

/// <summary>
/// Access Parity: give a target user the group memberships a source user has and they lack.
/// Additive only -- the only write this class can issue is <c>POST groups/{id}/members/$ref</c>
/// for the target; nothing is ever removed. Only cloud Security and Microsoft 365 groups are
/// eligible; every other membership is listed with the reason it is not copied.
/// </summary>
internal sealed class AccessParityOperation : IPlannedOperation
{
    public const string OperationId = "access-parity";

    /// <summary>Always stated: what this comparison does not cover.</summary>
    public static readonly IReadOnlyList<string> StandardLimitations =
    [
        "Only direct group memberships and directory roles are compared; access inherited through nested groups follows from the direct groups.",
        "SharePoint direct (non-group) site and file permissions are not compared.",
        "App role assignments (enterprise application access granted directly to the user) are not compared.",
        "Exchange mailbox and calendar permissions (Full Access, Send As, delegate access) are not compared.",
        "Teams private and shared channel memberships are not compared (they are not group memberships)."
    ];

    private readonly TenantGraphRest _graph;

    public AccessParityOperation(TenantGraphRest graph) => _graph = graph;

    public string Id => OperationId;
    public string Name => "Access parity";
    public string Description => "Give a user the missing group memberships another user has. Additive only: nothing is removed.";
    public string Category => "Identity";
    public IReadOnlyList<WorkflowInput> Inputs =>
    [
        new("sourceUserId", "Source user (UPN or id) - copy access from", "colleague@contoso.com"),
        new("targetUserId", "Target user (UPN or id) - give access to", "newhire@contoso.com")
    ];

    private sealed class Attempt(PlanItem item, ChangeResult change, bool alreadyExisted)
    {
        public PlanItem Item { get; } = item;
        public ChangeResult Change { get; } = change;
        public bool AlreadyExisted { get; } = alreadyExisted;
        public bool Verified { get; set; }
    }

    private sealed record UserRef(string Id, string DisplayName, string? Upn, bool? AccountEnabled);

    private sealed class PlanState
    {
        public required OperationPlan Plan { get; init; }
        public required UserRef Source { get; init; }
        public required UserRef Target { get; init; }
        public required HashSet<string> TargetMembershipIds { get; init; }
    }

    public async Task<OperationPlan> PlanAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
        (await BuildPlanAsync(tenant, inputs, ct)).Plan;

    private async Task<PlanState> BuildPlanAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        var sourceInput = Input(inputs, "sourceUserId");
        var targetInput = Input(inputs, "targetUserId");
        if (string.Equals(sourceInput, targetInput, StringComparison.OrdinalIgnoreCase))
            throw new OperationInputException("Source and target must be different users.");

        var graph = await _graph.CreateAsync(tenant, ct);
        var source = await ResolveUserAsync(graph, sourceInput, "Source", ct);
        var target = await ResolveUserAsync(graph, targetInput, "Target", ct);
        if (string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            throw new OperationInputException("Source and target resolve to the same user.");

        var sourceMemberships = await GroupClassifier.DirectMembershipsAsync(graph, source.Id, ct);
        var targetMemberships = await GroupClassifier.DirectMembershipsAsync(graph, target.Id, ct);
        var targetIds = targetMemberships.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plan = new OperationPlan
        {
            OperationId = Id,
            OperationName = Name,
            TenantId = tenant.Id.ToString(),
            Target = new OperationTarget("user", target.Id, target.DisplayName),
            Limitations = StandardLimitations.ToList()
        };
        plan.Preflight.Add(new("Source user", FindingStatus.Ok, Describe(source)));
        plan.Preflight.Add(new("Target user", target.AccountEnabled == false ? FindingStatus.Warning : FindingStatus.Ok,
            Describe(target) + (target.AccountEnabled == false ? " - sign-in is blocked" : "")));
        plan.Preflight.Add(new("Source direct memberships", FindingStatus.Info, Count(sourceMemberships.Count, "membership")));
        plan.Preflight.Add(new("Target direct memberships", FindingStatus.Info, Count(targetMemberships.Count, "membership")));
        if (target.AccountEnabled == false)
            plan.Warnings.Add($"{target.DisplayName} currently has sign-in blocked; added memberships take effect once the account is enabled.");

        var skippedOther = 0;
        foreach (var m in sourceMemberships.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (m.Category == MembershipCategory.Other && !m.IsGroup) { skippedOther++; continue; }
            var already = targetIds.Contains(m.Id);
            var isRole = m.Category == MembershipCategory.DirectoryRole;
            plan.Items.Add(new PlanItem
            {
                Id = $"{(isRole ? "role" : "group")}:{m.Id}",
                Action = isRole ? "AssignRole" : "AddMember",
                ObjectType = isRole ? "directoryRole" : "group",
                ObjectId = m.Id,
                ObjectName = m.DisplayName,
                Destructive = false,
                Category = already ? MembershipCategory.AlreadyMember : m.Category,
                Eligible = !already && m.Modifiable,
                Reason = already ? "Target is already a direct member." : m.Reason
            });
        }
        if (skippedOther > 0)
            plan.Warnings.Add($"{Count(skippedOther, "non-group membership")} (for example administrative units) not compared.");

        var eligible = plan.Items.Count(i => i.Eligible);
        plan.Preflight.Add(new("Missing eligible memberships",
            eligible > 0 ? FindingStatus.Warning : FindingStatus.Ok,
            eligible > 0 ? $"{Count(eligible, "group")} can be added" : "Target already has every eligible membership the source has."));

        return new PlanState { Plan = plan, Source = source, Target = target, TargetMembershipIds = targetIds };
    }

    public async Task<OperationEvidence> ApplyAsync(
        Tenant tenant, IReadOnlyDictionary<string, string> inputs, IReadOnlyCollection<string> selectedItemIds,
        CancellationToken ct = default)
    {
        // Re-plan server-side: the client's plan may be stale, and only still-eligible items run.
        var state = await BuildPlanAsync(tenant, inputs, ct);
        var plan = state.Plan;
        var graph = await _graph.CreateAsync(tenant, ct);

        var e = new OperationEvidence
        {
            OperationId = Id,
            OperationName = Name,
            Target = plan.Target,
            Preflight = plan.Preflight,
            Plan = plan.Items,
            Warnings = plan.Warnings.ToList(),
            Limitations = plan.Limitations.ToList()
        };

        var attempts = new List<Attempt>();
        foreach (var id in selectedItemIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = plan.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                e.Changes.Add(new ChangeResult
                {
                    PlanItemId = id, Action = "AddMember", ObjectName = id, Attempted = false, Succeeded = false,
                    Detail = "Not in the current plan (the source may no longer be a member); skipped."
                });
                e.Warnings.Add($"Selected item {id} is no longer in the plan and was skipped.");
                continue;
            }
            if (item.Category == MembershipCategory.AlreadyMember)
            {
                e.Changes.Add(new ChangeResult
                {
                    PlanItemId = item.Id, Action = item.Action, ObjectName = item.ObjectName,
                    Attempted = false, Succeeded = true, Detail = "Already a member; no change needed."
                });
                continue;
            }
            if (!item.Eligible)
            {
                e.Changes.Add(new ChangeResult
                {
                    PlanItemId = item.Id, Action = item.Action, ObjectName = item.ObjectName,
                    Attempted = false, Succeeded = false, Detail = "Skipped: " + item.Reason
                });
                continue;
            }

            var change = new ChangeResult
            {
                PlanItemId = item.Id, Action = item.Action, ObjectName = item.ObjectName, Attempted = true
            };
            var alreadyExisted = false;
            try
            {
                await graph.PostAsync($"/groups/{Uri.EscapeDataString(item.ObjectId)}/members/$ref",
                    new Dictionary<string, object?> { ["@odata.id"] = $"{graph.BaseUrl}/directoryObjects/{state.Target.Id}" }, ct);
                change.Succeeded = true;
                change.Detail = "Member added.";
            }
            catch (Exception ex) when (GraphErrors.IsAlreadyExists(ex))
            {
                alreadyExisted = true;
                change.Succeeded = true;
                change.Detail = "Graph reported the membership already exists; no change needed.";
            }
            catch (Exception ex) when (GraphErrors.IsForbidden(ex))
            {
                change.Succeeded = false;
                change.Detail = "Insufficient privileges to add members to this group (Graph 403).";
                e.Failures.Add($"{item.ObjectName}: insufficient privileges to add members (Graph 403)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                change.Succeeded = false;
                change.Detail = GraphErrors.Describe(ex);
                e.Failures.Add($"{item.ObjectName}: {GraphErrors.Describe(ex)}");
            }
            e.Changes.Add(change);
            attempts.Add(new Attempt(item, change, alreadyExisted));
        }

        // Verify by re-reading the target's memberships from Graph, never by trusting the POST.
        HashSet<string>? after = null;
        string? verifyError = null;
        if (attempts.Count > 0)
        {
            try
            {
                after = (await GroupClassifier.DirectMembershipsAsync(graph, state.Target.Id, ct))
                    .Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                verifyError = GraphErrors.Describe(ex);
                e.Failures.Add($"Verification re-read of target memberships failed: {verifyError}");
            }
        }

        var outcomes = new List<ItemOutcome>();
        foreach (var attempt in attempts)
        {
            var present = after?.Contains(attempt.Item.ObjectId) == true;
            attempt.Verified = present;
            string detail = after is null
                ? $"Could not re-read memberships: {verifyError}"
                : present ? "Target is a direct member on re-read."
                : attempt.Change.Succeeded ? "Add was reported, but the target is not a member on re-read."
                : "Target is not a member (the add failed).";
            e.Verification.Add(new VerificationCheck($"Membership: {attempt.Item.ObjectName}", present, detail));
            outcomes.Add(new ItemOutcome(Attempted: true, ReportedOk: attempt.Change.Succeeded, Verified: present));
        }

        bool? preserved = null;
        if (after is not null)
        {
            var lost = state.TargetMembershipIds.Where(id => !after.Contains(id)).ToList();
            preserved = lost.Count == 0;
            e.Verification.Add(new VerificationCheck("Existing target memberships preserved", lost.Count == 0,
                lost.Count == 0
                    ? $"All {Count(state.TargetMembershipIds.Count, "prior membership")} still present."
                    : $"{Count(lost.Count, "prior membership")} no longer present; PCB issued no removals, so this was changed elsewhere during the run."));
            if (lost.Count > 0)
                e.Warnings.Add("Some of the target's prior memberships disappeared during the run. PCB never removes memberships; check for a concurrent change.");
        }

        e.Outcome = OutcomeRules.Derive(outcomes);
        e.TicketNotes = BuildNotes(state, e, attempts, preserved, verifyError);
        return e;
    }

    private static string BuildNotes(
        PlanState state, OperationEvidence e,
        List<Attempt> attempts, bool? preserved, string? verifyError)
    {
        var sb = new StringBuilder();
        var plan = state.Plan;
        sb.Append($"Compared {state.Target.DisplayName}'s group memberships against source user {state.Source.DisplayName}. ");

        var added = attempts.Where(a => a.Change.Succeeded && !a.AlreadyExisted).ToList();
        var failed = attempts.Where(a => !a.Change.Succeeded).ToList();
        var alreadyPresent = attempts.Count(a => a.AlreadyExisted)
                             + e.Changes.Count(c => !c.Attempted && c.Succeeded);

        if (added.Count > 0)
            sb.Append($"Added {Count(added.Count, "missing eligible group membership")}. ");
        else if (failed.Count == 0)
            sb.Append("No group memberships were added. ");
        if (alreadyPresent > 0)
            sb.Append($"{Count(alreadyPresent, "selected group")} {(alreadyPresent == 1 ? "was" : "were")} already present and needed no change. ");
        if (failed.Count > 0)
            sb.Append($"{Count(failed.Count, "membership")} could not be added: ")
              .Append(string.Join("; ", failed.Select(f => $"{f.Item.ObjectName} ({ShortReason(f.Change.Detail)})")))
              .Append(". ");

        var selectedIds = e.Changes.Select(c => c.PlanItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notSelected = plan.Items.Count(i => i.Eligible && !selectedIds.Contains(i.Id));
        if (notSelected > 0)
            sb.Append($"{Count(notSelected, "eligible group")} {(notSelected == 1 ? "was" : "were")} not selected and left unchanged. ");

        AppendSkipped(sb, plan, MembershipCategory.Dynamic, "dynamic group", "because membership is rule-managed");
        var exchange = plan.Items.Count(i => i.Category is MembershipCategory.MailEnabledSecurity or MembershipCategory.Distribution);
        if (exchange > 0)
            sb.Append($"{Count(exchange, "mail-enabled security group or distribution list", "mail-enabled security groups or distribution lists")} {(exchange == 1 ? "was" : "were")} skipped because they are managed in Exchange Online. ");
        AppendSkipped(sb, plan, MembershipCategory.RoleAssignable, "role-assignable group", "because they grant directory role privileges");
        AppendSkipped(sb, plan, MembershipCategory.OnPremSynced, "on-premises synced group", "because membership is managed in on-premises AD");
        var roles = plan.Items.Count(i => i.Category == MembershipCategory.DirectoryRole);
        if (roles > 0)
            sb.Append($"{Count(roles, "directory role")} held by the source {(roles == 1 ? "was" : "were")} not copied. ");

        if (preserved == true || (preserved is null && attempts.Count == 0))
            sb.Append("Existing target memberships were preserved. ");
        else if (preserved == false)
            sb.Append("Some prior target memberships disappeared during the run (PCB issued no removals). ");

        if (attempts.Count > 0)
        {
            var reportedOk = attempts.Where(a => a.Change.Succeeded).ToList();
            var verified = reportedOk.Count(a => a.Verified);
            var unconfirmed = reportedOk.Count - verified;
            if (verifyError is not null)
                sb.Append($"Post-change verification could not be completed ({verifyError}), so the additions are unconfirmed. ");
            else if (unconfirmed > 0)
                sb.Append($"Post-change verification could not confirm {unconfirmed} of {Count(reportedOk.Count, "reported addition")}. ");
            else if (reportedOk.Count > 0)
                sb.Append($"Post-change verification confirmed the {Count(verified, "addition")}. ");
        }
        return sb.ToString().Trim();
    }

    private static void AppendSkipped(StringBuilder sb, OperationPlan plan, string category, string noun, string because)
    {
        var n = plan.Items.Count(i => i.Category == category);
        if (n > 0) sb.Append($"{Count(n, noun)} {(n == 1 ? "was" : "were")} identified and skipped {because}. ");
    }

    private static string ShortReason(string? detail) =>
        detail is null ? "unknown error"
        : detail.StartsWith("Insufficient privileges", StringComparison.Ordinal) ? "insufficient privileges"
        : detail.TrimEnd('.');

    // --- IWorkflow surface: diagnose = plan as findings, remediate = apply every eligible item ---

    public async Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var result = new DiagnosisResult();
        OperationPlan plan;
        try { plan = await PlanAsync(tenant, inputs, ct); }
        catch (Exception ex) when (ex is OperationInputException or GraphRequestException)
        {
            result.Findings.Add(new("Plan", FindingStatus.Blocker, ex is GraphRequestException ? GraphErrors.Describe(ex) : ex.Message));
            return result;
        }
        result.Findings.AddRange(plan.Preflight);
        foreach (var group in plan.Items.Where(i => !i.Eligible).GroupBy(i => i.Category))
            result.Findings.Add(new($"Not copied: {group.Key}", FindingStatus.Info,
                string.Join(", ", group.Select(i => i.ObjectName))));
        return result;
    }

    public async Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var plan = await PlanAsync(tenant, inputs, ct);
        var evidence = await ApplyAsync(tenant, inputs, plan.Items.Where(i => i.Eligible).Select(i => i.Id).ToList(), ct);
        var run = new WorkflowRunResult { Evidence = evidence };
        // Verification checks are emitted in the same order as the attempted changes.
        var attempted = evidence.Changes.Where(c => c.Attempted).ToList();
        for (var i = 0; i < attempted.Count; i++)
        {
            var c = attempted[i];
            var verified = i < evidence.Verification.Count && evidence.Verification[i].Passed;
            run.Steps.Add(new ProvisioningStep($"Add to {c.ObjectName}", c.Succeeded && verified, c.Detail));
        }
        if (run.Steps.Count == 0)
            run.Steps.Add(new ProvisioningStep("Add memberships", true, "No eligible memberships were missing."));
        run.PostState = new DiagnosisResult
        {
            Findings = evidence.Verification
                .Select(v => new Finding(v.Name, v.Passed ? FindingStatus.Ok : FindingStatus.Blocker, v.Detail)).ToList()
        };
        return run;
    }

    // --- helpers ---

    private static string Input(IReadOnlyDictionary<string, string> inputs, string key) =>
        inputs.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim()
            : throw new OperationInputException($"Missing required input '{key}'.");

    private static async Task<UserRef> ResolveUserAsync(GraphRestClient graph, string idOrUpn, string role, CancellationToken ct)
    {
        try
        {
            using var doc = await graph.GetAsync(
                $"/users/{Uri.EscapeDataString(idOrUpn)}?$select=id,displayName,userPrincipalName,accountEnabled", ct);
            var r = doc.RootElement;
            var id = r.GetProperty("id").GetString()!;
            var upn = r.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null;
            var name = r.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : upn ?? id;
            bool? enabled = r.TryGetProperty("accountEnabled", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? a.GetBoolean() : null;
            return new UserRef(id, name, upn, enabled);
        }
        catch (Exception ex) when (GraphErrors.IsNotFound(ex))
        {
            throw new OperationInputException($"{role} user '{idOrUpn}' was not found in this tenant.");
        }
    }

    private static string Describe(UserRef u) => u.Upn is null ? u.DisplayName : $"{u.DisplayName} ({u.Upn})";
}

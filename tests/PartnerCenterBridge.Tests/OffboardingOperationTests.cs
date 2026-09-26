using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Operations;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class OffboardingOperationTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ScriptedExchange _exchange = new();
    public void Dispose() => _server.Stop();

    private OffboardingOperation Op(bool exchangeAvailable = true) => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })),
        _exchange, new FixedExchangeCapability(exchangeAvailable), TimeProvider.System);

    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };

    private static object Group(string id, string name, string[]? groupTypes = null, bool security = true, bool mail = false) =>
        new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.group", ["id"] = id, ["displayName"] = name,
            ["groupTypes"] = groupTypes ?? Array.Empty<string>(), ["securityEnabled"] = security, ["mailEnabled"] = mail
        };

    // When the fake Graph accepted a revokeSignInSessions POST (null: never). The user's
    // signInSessionsValidFromDateTime is a stale value until then, like the real one: an
    // ineffective or missing revoke leaves it where it was.
    private DateTimeOffset? _revokedAt;
    private static readonly DateTimeOffset StaleCutoff = DateTimeOffset.UtcNow.AddMinutes(-4);

    /// <summary>User reads: the first (plan) returns <paramref name="before"/>, later ones (verification) <paramref name="after"/>.</summary>
    private void StubUser(object before, object? after = null)
    {
        var reads = 0;
        _server.Given(Request.Create().WithPath("/users/u1").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ =>
            {
                var node = System.Text.Json.JsonSerializer.SerializeToNode(Interlocked.Increment(ref reads) == 1 ? before : after ?? before)!.AsObject();
                if (node.ContainsKey("signInSessionsValidFromDateTime"))
                    node["signInSessionsValidFromDateTime"] = (_revokedAt ?? StaleCutoff).ToString("o");
                return node.ToJsonString();
            }));
        _server.Given(Request.Create().WithPath("/users/u1/licenseDetails").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = new[] { new { skuId = "sku-e3", skuPartNumber = "ENTERPRISEPACK" } } }));
    }

    private static object CloudUser(bool enabled = true, bool synced = false, string[]? skus = null) => new
    {
        id = "u1", displayName = "Leaver", userPrincipalName = "leaver@contoso.com", accountEnabled = enabled,
        onPremisesSyncEnabled = synced ? (bool?)true : null,
        assignedLicenses = (skus ?? ["sku-e3"]).Select(s => new { skuId = s }).ToArray(),
        licenseAssignmentStates = (skus ?? ["sku-e3"]).Select(s => new { skuId = s, assignedByGroup = (string?)null, state = "Active" }).ToArray(),
        signInSessionsValidFromDateTime = DateTimeOffset.UtcNow.ToString("o")
    };

    private void StubMemberOf(object[] before, object[] after)
    {
        var reads = 0;
        _server.Given(Request.Create().WithPath("/users/u1/memberOf").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ =>
                System.Text.Json.JsonSerializer.Serialize(new { value = Interlocked.Increment(ref reads) == 1 ? before : after })));
    }

    private void StubWrites(bool revokeEffective = true)
    {
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch()).RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ =>
            {
                if (revokeEffective) _revokedAt = DateTimeOffset.UtcNow;
                return "{\"value\":true}";
            }));
        _server.Given(Request.Create().WithPath("/users/u1/assignLicense").UsingPost()).RespondWith(Response.Create().WithBodyAsJson(new { id = "u1" }));
        _server.Given(Request.Create().WithPath("/groups/*/members/u1/$ref").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));
    }

    private IEnumerable<string> Calls(string method) =>
        _server.LogEntries.Where(l => l.RequestMessage.Method == method).Select(l => l.RequestMessage.Path);

    [Fact]
    public void Default_policy_reproduces_the_original_offboarding_behavior()
    {
        var p = new OffboardingPolicy();
        Assert.True(p.BlockSignIn);
        Assert.True(p.RevokeSessions);
        Assert.True(p.RemoveLicenses);
        Assert.Equal(GroupCleanupMode.RemoveAll, p.GroupCleanup);
        Assert.False(p.ConvertMailboxToShared);
        Assert.False(p.HideFromGal);
        Assert.Null(p.ForwardTo);
        Assert.Equal(ManagerAccessMode.None, p.ManagerAccess);
        Assert.Equal(DeviceWipeMode.None, p.WipeDevices);
        Assert.Equal(0, p.FollowUpDays);
        Assert.Empty(p.Validate());
        Assert.NotEmpty(new OffboardingPolicy { ForwardTo = "not an address" }.Validate());
        Assert.NotEmpty(new OffboardingPolicy { FollowUpDays = -1 }.Validate());
    }

    [Fact]
    public async Task Plan_orders_mailbox_conversion_before_license_removal_and_flags_destructive_items()
    {
        StubUser(CloudUser());
        StubMemberOf([Group("g1", "Finance")], []);

        var plan = await Op().PlanAsync(Tenant(), "u1",
            new OffboardingPolicy { ConvertMailboxToShared = true, ForwardTo = "manager@contoso.com", FollowUpDays = 30 });

        var order = plan.Items.Select(i => i.Id).ToList();
        int At(string id) => order.IndexOf(id);
        Assert.True(At("block-sign-in") < At("convert-mailbox"));
        Assert.True(At("convert-mailbox") < At("set-forwarding"));
        Assert.True(At("convert-mailbox") < At("group:g1"));
        Assert.True(At("convert-mailbox") < At("license:sku-e3"));
        var license = plan.Items.Single(i => i.Id == "license:sku-e3");
        Assert.True(license.Destructive);
        Assert.True(license.Eligible);
        Assert.Equal("ENTERPRISEPACK", license.ObjectName);
        Assert.Contains("after the mailbox conversion to shared is verified", license.Reason);
        Assert.True(plan.Items.Single(i => i.Id == "group:g1").Destructive);
        Assert.False(plan.Items.Single(i => i.Id == "block-sign-in").Destructive);
        var followUp = plan.Items.Single(i => i.Id == "follow-up");
        Assert.False(followUp.Eligible);
        Assert.Contains("does not schedule", followUp.Reason);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Follow-up"));
        Assert.All(_server.LogEntries, l => Assert.Equal("GET", l.RequestMessage.Method));
    }

    [Fact]
    public async Task Synced_account_gets_a_guardrail_and_block_sign_in_is_ineligible()
    {
        StubUser(CloudUser(synced: true));
        StubMemberOf([], []);
        StubWrites();

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy { RemoveLicenses = false, GroupCleanup = GroupCleanupMode.None });

        var block = e.Plan.Single(i => i.Id == "block-sign-in");
        Assert.False(block.Eligible);
        Assert.Contains("synced from on-premises AD", block.Reason);
        Assert.Contains(e.Warnings, w => w.Contains("on-premises AD"));
        Assert.Empty(Calls("PATCH"));
        // Revoke still ran and verified, but the requested block did not happen: not a success.
        Assert.Equal(Outcome.PartiallySucceeded, e.Outcome);
        Assert.Contains("Block sign-in: not performed", e.TicketNotes);
    }

    [Fact]
    public async Task Unverified_conversion_skips_license_removal_with_reason()
    {
        StubUser(CloudUser(), CloudUser(enabled: false));
        StubMemberOf([], []);
        StubWrites();
        _exchange.MailboxAfter = new MailboxInfo("leaver@contoso.com", "Leaver", "UserMailbox", null, false); // still a user mailbox

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy { ConvertMailboxToShared = true });

        Assert.Contains(_exchange.Calls, c => c.StartsWith("convert:leaver@contoso.com"));
        Assert.Empty(Calls("POST").Where(p => p.EndsWith("/assignLicense")));
        var license = e.Changes.Single(c => c.PlanItemId == "license:sku-e3");
        Assert.False(license.Attempted);
        Assert.Contains("conversion to shared was not verified", license.Detail);
        Assert.Equal(Outcome.VerificationFailed, e.Outcome); // conversion reported ok, re-read disagrees
        Assert.Contains("Convert mailbox to shared: reported done but NOT confirmed", e.TicketNotes);
    }

    [Fact]
    public async Task Verified_conversion_runs_before_license_removal()
    {
        StubUser(CloudUser(), CloudUser(enabled: false, skus: []));
        StubMemberOf([], []);
        StubWrites();
        _exchange.MailboxAfter = new MailboxInfo("leaver@contoso.com", "Leaver", "SharedMailbox", null, false);

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy { ConvertMailboxToShared = true });

        Assert.Equal(Outcome.Succeeded, e.Outcome);
        var ids = e.Changes.Where(c => c.Attempted).Select(c => c.PlanItemId).ToList();
        Assert.True(ids.IndexOf("convert-mailbox") < ids.IndexOf("license:sku-e3"));
        Assert.Single(Calls("POST").Where(p => p.EndsWith("/assignLicense")));
        Assert.All(e.Verification, v => Assert.True(v.Passed, v.Name + ": " + v.Detail));
    }

    [Fact]
    public async Task Exchange_unavailable_blocks_conversion_and_keeps_licenses()
    {
        StubUser(CloudUser(), CloudUser(enabled: false));
        StubMemberOf([], []);
        StubWrites();

        var e = await Op(exchangeAvailable: false).ApplyAsync(Tenant(), "u1", new OffboardingPolicy { ConvertMailboxToShared = true });

        Assert.False(e.Plan.Single(i => i.Id == "convert-mailbox").Eligible);
        var license = e.Plan.Single(i => i.Id == "license:sku-e3");
        Assert.False(license.Eligible);
        Assert.Contains("cannot run", license.Reason);
        Assert.Empty(_exchange.Calls);
        Assert.Empty(Calls("POST").Where(p => p.EndsWith("/assignLicense")));
        Assert.Equal(Outcome.PartiallySucceeded, e.Outcome);
    }

    [Fact]
    public async Task Default_policy_blocks_revokes_removes_groups_and_licenses_with_verification()
    {
        StubUser(CloudUser(), CloudUser(enabled: false, skus: []));
        StubMemberOf(
            before: [Group("g1", "Finance"), Group("dyn", "Dyn", groupTypes: ["DynamicMembership"]), Group("dl", "DL", security: false, mail: true)],
            after: [Group("dyn", "Dyn", groupTypes: ["DynamicMembership"]), Group("dl", "DL", security: false, mail: true)]);
        StubWrites();

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy());

        Assert.Equal(Outcome.Succeeded, e.Outcome);
        Assert.Equal(new[] { "/groups/g1/members/u1/$ref" }, Calls("DELETE").ToArray());
        Assert.Single(Calls("PATCH"));
        var steps = OffboardingOperation.ToSteps(e);
        Assert.All(steps, s => Assert.True(s.Success, s.Name + ": " + s.Detail));
        Assert.Contains(steps, s => s.Name == "Remove from Dyn" && s.Detail!.StartsWith("Left in place"));
        Assert.Contains("Removed 1 group membership (verified)", e.TicketNotes);
        Assert.Empty(_exchange.Calls);
    }

    // --- Device retire: explicit managementState values (finding 2) ---

    private static OffboardingPolicy DevicesOnly() => new()
    {
        BlockSignIn = false, RevokeSessions = false, RemoveLicenses = false, GroupCleanup = GroupCleanupMode.None,
        WipeDevices = DeviceWipeMode.Retire
    };

    private void StubDevice(string managementState)
    {
        _server.Given(Request.Create().WithPath("/users/u1/managedDevices").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = new[] { new { id = "d1", deviceName = "LAPTOP-1", operatingSystem = "Windows", managementState = "managed" } } }));
        _server.Given(Request.Create().WithPath("/deviceManagement/managedDevices/d1/retire").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/deviceManagement/managedDevices/d1").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { id = "d1", managementState }));
    }

    [Theory]
    [InlineData("retireFailed")]
    [InlineData("retireCanceled")]
    public async Task Failed_or_canceled_retire_fails_verification(string state)
    {
        StubUser(CloudUser());
        StubDevice(state);

        var e = await Op().ApplyAsync(Tenant(), "u1", DevicesOnly());

        Assert.Equal(Outcome.VerificationFailed, e.Outcome);
        Assert.False(e.Verification.Single(v => v.Name.StartsWith("Retire")).Passed);
        Assert.DoesNotContain("(verified)", e.TicketNotes);
    }

    [Theory]
    [InlineData("retirePending")]
    [InlineData("retireIssued")]
    public async Task Pending_retire_is_issued_not_completed(string state)
    {
        StubUser(CloudUser());
        StubDevice(state);

        var e = await Op().ApplyAsync(Tenant(), "u1", DevicesOnly());

        Assert.Equal(Outcome.CompletedUnverified, e.Outcome);
        var check = e.Verification.Single(v => v.PlanItemId == "device:d1");
        Assert.False(check.Passed);
        Assert.True(check.Unverifiable);
        Assert.Contains("retire issued for 1 device but not yet completed", e.TicketNotes);
        Assert.False(OffboardingOperation.ToSteps(e).Single(s => s.Name.StartsWith("Retire")).Success);
    }

    [Fact]
    public async Task Completed_retire_is_verified()
    {
        StubUser(CloudUser());
        StubDevice("retired");

        var e = await Op().ApplyAsync(Tenant(), "u1", DevicesOnly());

        Assert.Equal(Outcome.Succeeded, e.Outcome);
        Assert.Contains("Retired 1 device (verified)", e.TicketNotes);
    }

    [Fact]
    public async Task Acknowledged_revoke_that_leaves_the_cutoff_unchanged_is_not_verified()
    {
        StubUser(CloudUser(), CloudUser(enabled: false, skus: []));
        StubMemberOf([], []);
        // Graph acknowledges the revoke, but the cutoff stays at an earlier (four-minute-old) value.
        StubWrites(revokeEffective: false);

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy());

        var check = e.Verification.Single(v => v.PlanItemId == "revoke-sessions");
        Assert.False(check.Passed);
        Assert.Contains("did not move forward", check.Detail);
        Assert.Equal(Outcome.VerificationFailed, e.Outcome);
    }

    // --- Group-inherited licenses (finding 5) ---

    private static object InheritedUser(bool licensed, bool enabled = true) => new
    {
        id = "u1", displayName = "Leaver", userPrincipalName = "leaver@contoso.com", accountEnabled = enabled,
        assignedLicenses = licensed ? new object[] { new { skuId = "sku-e3" } } : Array.Empty<object>(),
        licenseAssignmentStates = licensed ? new object[] { new { skuId = "sku-e3", assignedByGroup = "lic", state = "Active" } } : Array.Empty<object>(),
        signInSessionsValidFromDateTime = DateTimeOffset.UtcNow.ToString("o")
    };

    [Fact]
    public async Task Inherited_license_still_assigned_is_not_success()
    {
        StubUser(InheritedUser(true));
        StubWrites();

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy { BlockSignIn = false, GroupCleanup = GroupCleanupMode.None });

        Assert.Equal(Outcome.PartiallySucceeded, e.Outcome); // sessions revoked and verified; the license remains
        Assert.False(e.Verification.Single(v => v.PlanItemId == "license:sku-e3").Passed);
        Assert.Contains(e.Warnings, w => w.Contains("group-based licensing") && w.Contains("licensing group"));
        Assert.Contains("still assigned through group-based licensing", e.TicketNotes);
        Assert.False(OffboardingOperation.ToSteps(e).Single(s => s.Name == "Remove license ENTERPRISEPACK").Success);
    }

    [Fact]
    public async Task Inherited_license_gone_after_group_cleanup_is_verified()
    {
        StubUser(InheritedUser(true), InheritedUser(false, enabled: false));
        StubMemberOf([Group("lic", "Licensing")], []);
        StubWrites();

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy());

        Assert.Equal(Outcome.Succeeded, e.Outcome);
        Assert.True(e.Verification.Single(v => v.PlanItemId == "license:sku-e3").Passed);
    }

    // --- Forwarding verification (finding 6) ---

    [Theory]
    [InlineData("SMTP:notmanager@contoso.com", false, Outcome.VerificationFailed)]
    [InlineData("smtp:manager@contoso.com.evil.example", false, Outcome.VerificationFailed)]
    [InlineData("smtp:manager@contoso.com", true, Outcome.VerificationFailed)]   // copy kept: not what was asked
    [InlineData("SMTP:Manager@Contoso.com", false, Outcome.Succeeded)]
    [InlineData("manager@contoso.com", false, Outcome.Succeeded)]
    public async Task Forwarding_verification_matches_the_exact_address_and_delivery_mode(string forwarding, bool deliverAndForward, Outcome expected)
    {
        StubUser(CloudUser(), CloudUser(enabled: false, skus: []));
        StubMemberOf([], []);
        StubWrites();
        _exchange.ConvertResult = new ExoResult { Steps = { new("Connect", true, "contoso"), new("Convert to shared", true, "u"), new("Set forwarding", true, "manager@contoso.com") } };
        _exchange.MailboxAfter = new MailboxInfo("leaver@contoso.com", "Leaver", "SharedMailbox", forwarding, deliverAndForward);

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy { ConvertMailboxToShared = true, ForwardTo = "manager@contoso.com" });

        Assert.Equal(expected, e.Outcome);
        Assert.Equal(expected == Outcome.Succeeded, e.Verification.Single(v => v.Name.StartsWith("Forward mail")).Passed);
    }

    // --- Verification tied to plan-item ids, not names (finding 15) ---

    [Fact]
    public async Task Duplicate_group_names_get_their_own_verification()
    {
        StubUser(CloudUser(), CloudUser(enabled: false, skus: []));
        StubMemberOf([Group("g1", "Finance"), Group("g2", "Finance")], [Group("g2", "Finance")]); // g2 removal did not take
        StubWrites();

        var e = await Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy());

        Assert.Equal(Outcome.VerificationFailed, e.Outcome);
        var finance = OffboardingOperation.ToSteps(e).Where(s => s.Name == "Remove from Finance").ToList();
        Assert.Equal(2, finance.Count);
        Assert.Single(finance, s => s.Success);
        Assert.Single(finance, s => !s.Success && s.Detail!.Contains("Still a member"));
    }

    // --- Interrupted apply keeps partial evidence (finding 8) ---

    [Fact]
    public async Task Cancellation_mid_apply_keeps_completed_changes()
    {
        StubUser(CloudUser());
        StubMemberOf([Group("g1", "Finance")], []);
        using var cts = new CancellationTokenSource();
        // Blocking sign-in answers and starts the cancellation clock; the revoke is sent next and
        // hangs, so the run is cancelled while it waits for Graph's answer.
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(204).WithBody(_ => { cts.CancelAfter(TimeSpan.FromSeconds(2)); return ""; }));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = true }).WithDelay(TimeSpan.FromSeconds(30)));

        var ex = await Assert.ThrowsAsync<OperationInterruptedException>(
            () => Op().ApplyAsync(Tenant(), "u1", new OffboardingPolicy(), cts.Token));

        var e = ex.Partial;
        Assert.True(e.Changes.Single(c => c.PlanItemId == "block-sign-in") is { Attempted: true, Succeeded: true });
        // The revoke was sent: it may or may not have been applied.
        var revoke = e.Changes.Single(c => c.PlanItemId == "revoke-sessions");
        Assert.True(revoke.Attempted);
        Assert.False(revoke.Succeeded);
        Assert.Equal(ChangeResult.InterruptedInFlight, revoke.Detail);
        Assert.Contains("Revoke sessions: request sent, but the run was interrupted", e.TicketNotes);
        // The group removal was never sent.
        var group = e.Changes.Single(c => c.PlanItemId == "group:g1");
        Assert.False(group.Attempted);
        Assert.StartsWith("Interrupted", group.Detail);
        Assert.Empty(Calls("DELETE"));
        Assert.All(e.Verification.Where(v => v.PlanItemId is "block-sign-in" or "revoke-sessions"), v => Assert.False(v.Passed));
        Assert.NotEqual(Outcome.Succeeded, e.Outcome);
        Assert.Contains("interrupted", e.TicketNotes);
    }

    [Fact]
    public async Task Cancellation_during_the_verification_reread_keeps_the_applied_changes()
    {
        // User reads: the plan and the pre-revoke cutoff answer; the verification re-read hangs.
        var user = System.Text.Json.JsonSerializer.Serialize(CloudUser());
        _server.Given(Request.Create().WithPath("/users/u1").UsingGet()).InScenario("user").WillSetStateTo("planned")
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(user));
        _server.Given(Request.Create().WithPath("/users/u1").UsingGet()).InScenario("user").WhenStateIs("planned").WillSetStateTo("applied")
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(user));
        _server.Given(Request.Create().WithPath("/users/u1").UsingGet()).InScenario("user").WhenStateIs("applied")
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(user).WithDelay(TimeSpan.FromSeconds(30)));
        _server.Given(Request.Create().WithPath("/users/u1/licenseDetails").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = Array.Empty<object>() }));
        using var cts = new CancellationTokenSource();
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch()).RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody(_ => { cts.CancelAfter(TimeSpan.FromSeconds(2)); return "{\"value\":true}"; }));

        var ex = await Assert.ThrowsAsync<OperationInterruptedException>(() => Op().ApplyAsync(Tenant(), "u1",
            new OffboardingPolicy { RemoveLicenses = false, GroupCleanup = GroupCleanupMode.None }, cts.Token));

        var e = ex.Partial;
        Assert.True(e.Changes.Single(c => c.PlanItemId == "block-sign-in") is { Attempted: true, Succeeded: true });
        Assert.True(e.Changes.Single(c => c.PlanItemId == "revoke-sessions") is { Attempted: true, Succeeded: true });
        foreach (var id in new[] { "block-sign-in", "revoke-sessions" })
        {
            var check = e.Verification.Single(v => v.PlanItemId == id);
            Assert.False(check.Passed);
            Assert.Contains("interrupted", check.Detail);
        }
        Assert.NotEqual(Outcome.Succeeded, e.Outcome);
        Assert.Contains("interrupted", e.TicketNotes);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }
}

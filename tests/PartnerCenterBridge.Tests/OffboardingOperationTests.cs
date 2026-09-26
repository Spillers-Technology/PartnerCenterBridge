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

    /// <summary>User reads: the first (plan) returns <paramref name="before"/>, later ones (verification) <paramref name="after"/>.</summary>
    private void StubUser(object before, object? after = null)
    {
        var reads = 0;
        _server.Given(Request.Create().WithPath("/users/u1").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ =>
                System.Text.Json.JsonSerializer.Serialize(Interlocked.Increment(ref reads) == 1 ? before : after ?? before)));
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

    private void StubWrites()
    {
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch()).RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost()).RespondWith(Response.Create().WithBodyAsJson(new { value = true }));
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
}

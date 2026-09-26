using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Operations;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class AccessParityOperationTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private AccessParityOperation Op() => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })));

    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };
    private static Dictionary<string, string> In() => new() { ["sourceUserId"] = "alice@contoso.com", ["targetUserId"] = "bob@contoso.com" };

    private static object Group(string id, string name, string[]? groupTypes = null, bool security = true, bool mail = false,
        bool roleAssignable = false, bool? synced = null, string? rule = null) => new Dictionary<string, object?>
    {
        ["@odata.type"] = "#microsoft.graph.group",
        ["id"] = id, ["displayName"] = name, ["groupTypes"] = groupTypes ?? Array.Empty<string>(),
        ["securityEnabled"] = security, ["mailEnabled"] = mail, ["isAssignableToRole"] = roleAssignable,
        ["onPremisesSyncEnabled"] = synced, ["membershipRule"] = rule
    };

    private static object Role(string id, string name) => new Dictionary<string, object?>
    {
        ["@odata.type"] = "#microsoft.graph.directoryRole", ["id"] = id, ["displayName"] = name
    };

    private void StubUsers()
    {
        _server.Given(Request.Create().WithPath("/users/alice@contoso.com").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { id = "src", displayName = "Alice Source", userPrincipalName = "alice@contoso.com", accountEnabled = true }));
        _server.Given(Request.Create().WithPath("/users/bob@contoso.com").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { id = "tgt", displayName = "Bob Target", userPrincipalName = "bob@contoso.com", accountEnabled = true }));
    }

    private void StubSource(params object[] memberships) =>
        _server.Given(Request.Create().WithPath("/users/src/memberOf").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = memberships }));

    /// <summary>Target memberOf: the first read (plan) returns <paramref name="before"/>, later reads (verification) <paramref name="after"/>.</summary>
    private void StubTarget(object[] before, object[] after)
    {
        var reads = 0;
        _server.Given(Request.Create().WithPath("/users/tgt/memberOf").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ =>
                System.Text.Json.JsonSerializer.Serialize(new { value = Interlocked.Increment(ref reads) == 1 ? before : after })));
    }

    private void StubAdd(string groupId, int status = 204, string body = "") =>
        _server.Given(Request.Create().WithPath($"/groups/{groupId}/members/$ref").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    private IEnumerable<string> Methods() => _server.LogEntries.Select(e => e.RequestMessage.Method.ToUpperInvariant());

    [Fact]
    public async Task Plan_classifies_every_group_category_and_only_cloud_security_and_m365_are_eligible()
    {
        StubUsers();
        StubSource(
            Group("sec", "Sales Security"),
            Group("m365", "Sales Team", groupTypes: ["Unified"], mail: true),
            Group("mes", "Mail Security", mail: true),
            Group("dl", "All Staff DL", security: false, mail: true),
            Group("dyn", "Dynamic Sales", groupTypes: ["DynamicMembership"], rule: "user.department -eq \"Sales\""),
            Group("role", "Helpdesk Admins", roleAssignable: true),
            Group("sync", "OnPrem Finance", synced: true),
            Role("dr", "Global Reader"),
            Group("have", "Already Shared"));
        StubTarget([Group("have", "Already Shared"), Group("own", "Target Only")], []);

        var plan = await Op().PlanAsync(Tenant(), In());

        string Cat(string name) => plan.Items.Single(i => i.ObjectName == name).Category;
        Assert.Equal("Security", Cat("Sales Security"));
        Assert.Equal("Microsoft365", Cat("Sales Team"));
        Assert.Equal("MailEnabledSecurity", Cat("Mail Security"));
        Assert.Equal("Distribution", Cat("All Staff DL"));
        Assert.Equal("Dynamic", Cat("Dynamic Sales"));
        Assert.Equal("RoleAssignable", Cat("Helpdesk Admins"));
        Assert.Equal("OnPremSynced", Cat("OnPrem Finance"));
        Assert.Equal("DirectoryRole", Cat("Global Reader"));
        Assert.Equal("AlreadyMember", Cat("Already Shared"));

        Assert.Equal(new[] { "Sales Security", "Sales Team" },
            plan.Items.Where(i => i.Eligible).Select(i => i.ObjectName).OrderBy(n => n).ToArray());
        Assert.All(plan.Items.Where(i => !i.Eligible), i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
        Assert.Equal("Managed in Exchange Online; not modified by this operation", plan.Items.Single(i => i.ObjectName == "Mail Security").Reason);
        Assert.Equal("Managed in Exchange Online; not modified by this operation", plan.Items.Single(i => i.ObjectName == "All Staff DL").Reason);
        Assert.DoesNotContain(plan.Items, i => i.ObjectName == "Target Only"); // target-only memberships are never touched
        Assert.All(plan.Items, i => Assert.False(i.Destructive));
        Assert.Equal("user", plan.Target.Kind);
        Assert.Equal("tgt", plan.Target.Id);
        Assert.Contains(plan.Limitations, l => l.Contains("SharePoint"));
        Assert.Contains(plan.Limitations, l => l.Contains("App role assignments"));
        Assert.Contains(plan.Limitations, l => l.Contains("mailbox and calendar"));
        Assert.Contains(plan.Limitations, l => l.Contains("private"));
        Assert.All(Methods(), m => Assert.Equal("GET", m));
    }

    [Fact]
    public async Task Plan_follows_nextLink_paging_for_memberships()
    {
        StubUsers();
        _server.Given(Request.Create().WithPath("/users/src/memberOf").WithParam("$skiptoken", "p2").UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithBodyAsJson(new { value = new[] { Group("g2", "Page Two") } }));
        _server.Given(Request.Create().WithPath("/users/src/memberOf").UsingGet()).AtPriority(10)
            .RespondWith(Response.Create().WithBodyAsJson(new Dictionary<string, object>
            {
                ["value"] = new[] { Group("g1", "Page One") },
                ["@odata.nextLink"] = $"{_server.Url}/users/src/memberOf?$skiptoken=p2"
            }));
        StubTarget([], []);

        var plan = await Op().PlanAsync(Tenant(), In());

        Assert.Equal(new[] { "Page One", "Page Two" }, plan.Items.Select(i => i.ObjectName).ToArray());
    }

    [Fact]
    public async Task Source_equal_to_target_is_rejected_before_any_Graph_call()
    {
        await Assert.ThrowsAsync<OperationInputException>(() => Op().PlanAsync(Tenant(),
            new Dictionary<string, string> { ["sourceUserId"] = "Bob@contoso.com", ["targetUserId"] = "bob@contoso.com" }));
        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task Apply_is_additive_only_and_writes_the_expected_ticket_notes()
    {
        StubUsers();
        StubSource(
            Group("g1", "Finance"), Group("g2", "Sales Team", groupTypes: ["Unified"], mail: true),
            Group("d1", "Dyn One", groupTypes: ["DynamicMembership"]), Group("d2", "Dyn Two", groupTypes: ["DynamicMembership"]),
            Group("dl", "Staff DL", security: false, mail: true));
        StubTarget(
            before: [Group("keep", "Target Only")],
            after: [Group("keep", "Target Only"), Group("g1", "Finance"), Group("g2", "Sales Team", groupTypes: ["Unified"], mail: true)]);
        StubAdd("g1");
        StubAdd("g2");

        // Selecting an ineligible item must not cause a write for it.
        var e = await Op().ApplyAsync(Tenant(), In(), ["group:g1", "group:g2", "group:d1", "group:dl"]);

        Assert.Equal(Outcome.Succeeded, e.Outcome);
        Assert.DoesNotContain("DELETE", Methods());
        Assert.DoesNotContain("PATCH", Methods());
        Assert.DoesNotContain("PUT", Methods());
        var posts = _server.LogEntries.Where(l => l.RequestMessage.Method == "POST").Select(l => l.RequestMessage.Path).OrderBy(p => p).ToList();
        Assert.Equal(new[] { "/groups/g1/members/$ref", "/groups/g2/members/$ref" }, posts);
        Assert.Contains("/directoryObjects/tgt", _server.LogEntries.First(l => l.RequestMessage.Method == "POST").RequestMessage.Body);
        Assert.False(e.Changes.Single(c => c.PlanItemId == "group:d1").Attempted);
        Assert.Contains(e.Verification, v => v.Name == "Existing target memberships preserved" && v.Passed);

        Assert.Equal(
            "Compared Bob Target's group memberships against source user Alice Source. " +
            "Added 2 missing eligible group memberships. " +
            "2 dynamic groups were identified and skipped because membership is rule-managed. " +
            "1 mail-enabled security group or distribution list was skipped because they are managed in Exchange Online. " +
            "Existing target memberships were preserved. " +
            "Post-change verification confirmed the 2 additions.",
            e.TicketNotes);
    }

    [Fact]
    public async Task Already_member_is_idempotent_not_a_failure()
    {
        StubUsers();
        StubSource(Group("g1", "Finance"), Group("g2", "Ops"));
        // g2 became a member between plan and apply: Graph answers 400 "already exist".
        StubTarget(before: [], after: [Group("g1", "Finance"), Group("g2", "Ops")]);
        StubAdd("g1");
        StubAdd("g2", 400, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"One or more added object references already exist for the following modified properties: 'members'.\"}}");

        var e = await Op().ApplyAsync(Tenant(), In(), ["group:g1", "group:g2"]);

        Assert.Equal(Outcome.Succeeded, e.Outcome);
        Assert.Empty(e.Failures);
        Assert.True(e.Changes.Single(c => c.PlanItemId == "group:g2").Succeeded);
        Assert.Contains("already present", e.TicketNotes);
    }

    [Fact]
    public async Task Selecting_only_existing_memberships_is_NoChangeNeeded()
    {
        StubUsers();
        StubSource(Group("g1", "Finance"));
        StubTarget(before: [Group("g1", "Finance")], after: [Group("g1", "Finance")]);

        var e = await Op().ApplyAsync(Tenant(), In(), ["group:g1"]);

        Assert.Equal(Outcome.NoChangeNeeded, e.Outcome);
        Assert.DoesNotContain("POST", Methods());
        Assert.Empty(e.Verification.Where(v => v.Name.StartsWith("Membership")));
    }

    [Fact]
    public async Task Forbidden_add_is_partial_success_with_honest_notes()
    {
        StubUsers();
        StubSource(Group("g1", "Finance"), Group("g2", "Payroll"));
        StubTarget(before: [], after: [Group("g1", "Finance")]);
        StubAdd("g1");
        StubAdd("g2", 403, "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}");

        var e = await Op().ApplyAsync(Tenant(), In(), ["group:g1", "group:g2"]);

        Assert.Equal(Outcome.PartiallySucceeded, e.Outcome);
        var failed = e.Changes.Single(c => c.PlanItemId == "group:g2");
        Assert.True(failed.Attempted);
        Assert.False(failed.Succeeded);
        Assert.Contains("insufficient privileges", e.Failures.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Added 1 missing eligible group membership.", e.TicketNotes);
        Assert.Contains("1 membership could not be added: Payroll (insufficient privileges).", e.TicketNotes);
        Assert.Contains("Post-change verification confirmed the 1 addition.", e.TicketNotes);
    }

    [Fact]
    public async Task Reported_success_that_verification_does_not_confirm_is_VerificationFailed()
    {
        StubUsers();
        StubSource(Group("g1", "Finance"));
        StubTarget(before: [], after: []); // POST says 204, re-read says no
        StubAdd("g1");

        var e = await Op().ApplyAsync(Tenant(), In(), ["group:g1"]);

        Assert.Equal(Outcome.VerificationFailed, e.Outcome);
        Assert.False(e.Verification.Single(v => v.Name == "Membership: Finance").Passed);
        Assert.Contains("could not confirm 1 of 1 reported addition", e.TicketNotes);
        Assert.DoesNotContain("confirmed the", e.TicketNotes);
    }

    [Fact]
    public async Task Remediate_applies_every_eligible_item_and_carries_native_evidence()
    {
        StubUsers();
        StubSource(Group("g1", "Finance"), Group("dyn", "Dyn", groupTypes: ["DynamicMembership"]));
        // Remediate plans once itself, then apply re-plans: target is read twice before verification.
        var reads = 0;
        _server.Given(Request.Create().WithPath("/users/tgt/memberOf").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ =>
                System.Text.Json.JsonSerializer.Serialize(new { value = Interlocked.Increment(ref reads) <= 2 ? Array.Empty<object>() : new[] { Group("g1", "Finance") } })));
        StubAdd("g1");

        var run = await Op().RemediateAsync(Tenant(), In());

        Assert.True(run.Succeeded);
        Assert.NotNull(run.Evidence);
        Assert.Equal(Outcome.Succeeded, run.Evidence!.Outcome);
        Assert.Single(_server.LogEntries.Where(l => l.RequestMessage.Method == "POST"));
    }
}

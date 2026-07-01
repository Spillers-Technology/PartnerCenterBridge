using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Workflows;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class CompromisedAccountLockdownWorkflowTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private CompromisedAccountLockdownWorkflow Workflow() => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })));

    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };
    private static Dictionary<string, string> In() => new() { ["userUpn"] = "user1" };

    private void StubUser(bool enabled = true)
    {
        _server.Given(Request.Create().WithPath("/users/user1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = "u1", accountEnabled = enabled, displayName = "User One" }));
        _server.Given(Request.Create().WithPath("/users/u1/authentication/methods").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new object[] { new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.passwordAuthenticationMethod", ["id"] = "pwd" } }
            }));
    }

    private void StubRules(params object[] rules) =>
        _server.Given(Request.Create().WithPath("/users/u1/mailFolders/inbox/messageRules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = rules }));

    private static object ForwardingRule(string id = "r1", bool enabled = true) => new
    {
        id,
        displayName = "Sneaky forward",
        isEnabled = enabled,
        actions = new { forwardTo = new object[] { new { emailAddress = new { address = "evil@attacker.example" } } } }
    };

    private static object BenignRule() => new
    {
        id = "r2",
        displayName = "Move newsletters",
        isEnabled = true,
        actions = new { moveToFolder = "folder-id" }
    };

    [Fact]
    public async Task Diagnose_flags_enabled_account_and_forwarding_rule()
    {
        StubUser(enabled: true);
        StubRules(ForwardingRule(), BenignRule());

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Sign-in" && f.Status == FindingStatus.Warning);
        Assert.Contains(d.Findings, f => f.Name == "Inbox rule: Sneaky forward"
            && f.Status == FindingStatus.Warning && f.Detail!.Contains("evil@attacker.example"));
        Assert.DoesNotContain(d.Findings, f => f.Name.Contains("Move newsletters"));
        Assert.False(d.Healthy);
    }

    [Fact]
    public async Task Diagnose_is_healthy_when_blocked_and_clean()
    {
        StubUser(enabled: false);
        StubRules(BenignRule());

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Sign-in" && f.Status == FindingStatus.Ok);
        Assert.Contains(d.Findings, f => f.Name == "Inbox rules" && f.Status == FindingStatus.Ok);
        Assert.True(d.Healthy);
    }

    [Fact]
    public async Task Remediate_blocks_revokes_and_disables_risky_rules()
    {
        StubUser(enabled: true);
        StubRules(ForwardingRule(), BenignRule());
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = true }));
        _server.Given(Request.Create().WithPath("/users/u1/mailFolders/inbox/messageRules/r1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "r1", isEnabled = false }));

        var run = await Workflow().RemediateAsync(Tenant(), In());

        Assert.True(run.Succeeded, string.Join("; ", run.Steps.Select(s => $"{s.Name}:{s.Detail}")));
        Assert.Contains(run.Steps, s => s.Name == "Block sign-in" && s.Success);
        Assert.Contains(run.Steps, s => s.Name == "Revoke sign-in sessions" && s.Success);
        Assert.Contains(run.Steps, s => s.Name == "Disable inbox rule: Sneaky forward" && s.Success);

        var log = _server.LogEntries.Select(e => $"{e.RequestMessage.Method} {e.RequestMessage.Path}").ToList();
        Assert.Contains("PATCH /users/u1", log);
        Assert.Contains("PATCH /users/u1/mailFolders/inbox/messageRules/r1", log);
        // The benign rule must not be touched.
        Assert.DoesNotContain(log, l => l.Contains("messageRules/r2"));
    }
}

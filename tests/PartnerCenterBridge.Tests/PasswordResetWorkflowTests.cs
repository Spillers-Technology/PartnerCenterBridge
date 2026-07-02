using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Workflows;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class PasswordResetWorkflowTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private PasswordResetWorkflow Workflow() => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })));

    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };
    private static Dictionary<string, string> In() => new() { ["userUpn"] = "user1" };

    private void StubUser(bool? synced)
    {
        _server.Given(Request.Create().WithPath("/users/user1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = "u1",
                accountEnabled = true,
                lastPasswordChangeDateTime = "2026-01-15T00:00:00Z",
                onPremisesSyncEnabled = synced
            }));
    }

    [Fact]
    public async Task Diagnose_blocks_synced_accounts()
    {
        StubUser(synced: true);

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Directory sync" && f.Status == FindingStatus.Blocker);
        Assert.False(d.Healthy);
    }

    [Fact]
    public async Task Diagnose_passes_cloud_only_accounts()
    {
        StubUser(synced: null);

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Directory sync" && f.Status == FindingStatus.Ok);
        Assert.True(d.Healthy);
    }

    [Fact]
    public async Task Remediate_sets_password_revokes_and_returns_it_only_ephemerally()
    {
        StubUser(synced: null);
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(204));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = true }));

        var run = await Workflow().RemediateAsync(Tenant(), In());

        Assert.True(run.Succeeded, string.Join("; ", run.Steps.Select(s => $"{s.Name}:{s.Detail}")));
        var password = Assert.Contains("Temporary password", run.Ephemeral as IDictionary<string, string>);
        Assert.Equal(16, password.Length);

        // The password must reach Graph but never appear in the recorded steps.
        // Parse the body rather than substring-match: the JSON encoder escapes chars like '+'.
        var patchBody = _server.LogEntries
            .Single(e => e.RequestMessage.Method == "PATCH").RequestMessage.Body!;
        using var doc = System.Text.Json.JsonDocument.Parse(patchBody);
        var profile = doc.RootElement.GetProperty("passwordProfile");
        Assert.Equal(password, profile.GetProperty("password").GetString());
        Assert.True(profile.GetProperty("forceChangePasswordNextSignIn").GetBoolean());
        Assert.All(run.Steps, s => Assert.DoesNotContain(password, s.Detail ?? ""));
    }

    [Fact]
    public async Task Failed_password_set_returns_no_ephemeral_secret()
    {
        StubUser(synced: null);
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("denied"));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = true }));

        var run = await Workflow().RemediateAsync(Tenant(), In());

        Assert.False(run.Succeeded);
        Assert.Empty(run.Ephemeral);
    }

    [Fact]
    public void Generated_passwords_hit_all_character_classes()
    {
        for (var i = 0; i < 50; i++)
        {
            var p = PasswordResetWorkflow.GeneratePassword();
            Assert.Equal(16, p.Length);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
        }
    }
}

using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Workflows;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class MfaResetWorkflowTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private MfaResetWorkflow Workflow() => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })));

    private static Tenant Tenant() => new() { TenantId = "t", DisplayName = "Contoso" };
    private static Dictionary<string, string> In() => new() { ["userUpn"] = "user1" };

    private void StubUserAndMethods(params (string type, string id)[] methods)
    {
        _server.Given(Request.Create().WithPath("/users/user1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "u1" }));
        _server.Given(Request.Create().WithPath("/users/u1/authentication/methods").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = methods.Select(m => new Dictionary<string, object> { ["@odata.type"] = m.type, ["id"] = m.id }).ToArray()
            }));
    }

    [Fact]
    public async Task Diagnose_reports_strong_method_present()
    {
        StubUserAndMethods(
            ("#microsoft.graph.passwordAuthenticationMethod", "pwd"),
            ("#microsoft.graph.microsoftAuthenticatorAuthenticationMethod", "auth1"));

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Strong MFA" && f.Status == FindingStatus.Ok);
    }

    [Fact]
    public async Task Diagnose_warns_when_only_password()
    {
        StubUserAndMethods(("#microsoft.graph.passwordAuthenticationMethod", "pwd"));

        var d = await Workflow().DiagnoseAsync(Tenant(), In());

        Assert.Contains(d.Findings, f => f.Name == "Strong MFA" && f.Status == FindingStatus.Warning);
    }

    [Fact]
    public async Task Remediate_revokes_sessions_and_removes_deletable_methods()
    {
        StubUserAndMethods(
            ("#microsoft.graph.passwordAuthenticationMethod", "pwd"),
            ("#microsoft.graph.microsoftAuthenticatorAuthenticationMethod", "auth1"));
        _server.Given(Request.Create().WithPath("/users/u1/revokeSignInSessions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = true }));
        _server.Given(Request.Create().WithPath("/users/u1/authentication/microsoftAuthenticatorMethods/auth1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var run = await Workflow().RemediateAsync(Tenant(), In());

        Assert.True(run.Steps.All(s => s.Success), string.Join("; ", run.Steps.Select(s => $"{s.Name}:{s.Detail}")));
        Assert.Contains(run.Steps, s => s.Name == "Revoke sign-in sessions" && s.Success);
        Assert.Contains(run.Steps, s => s.Name.Contains("microsoftAuthenticator"));

        var log = _server.LogEntries.Select(e => $"{e.RequestMessage.Method} {e.RequestMessage.Path}").ToList();
        Assert.Contains("POST /users/u1/revokeSignInSessions", log);
        Assert.Contains("DELETE /users/u1/authentication/microsoftAuthenticatorMethods/auth1", log);
        // The password method must never be deleted.
        Assert.DoesNotContain(log, l => l.Contains("passwordMethods"));
    }
}

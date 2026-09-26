using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Workflows;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Classic workflow runs are verified against the desired state of each change, not against the
/// generic post-run diagnosis (finding 7).
/// </summary>
public class WorkflowDesiredStateVerificationTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private IWorkflow Workflow(string id)
    {
        var services = new ServiceCollection();
        services.AddSingleton<PartnerCenterBridge.PartnerCenter.ITokenProvider, FakeTokenProvider>();
        services.AddSingleton<IHttpClientFactory, SingleHttpClientFactory>();
        services.AddSingleton(Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! }));
        services.AddGraphWorkflows();
        return services.BuildServiceProvider().GetServices<IWorkflow>().Single(w => w.Id == id);
    }

    private static async Task<WorkflowRun> RemediateAsync(TestDb db, IWorkflow workflow)
    {
        var tenant = await OpsTest.AddTenantAsync(db);
        var controller = OpsTest.WithHttp(new WorkflowsController(new WorkflowCatalog([workflow]), db.Context, new NullRunNotifier(), new RoleAccess(TenantRole.Operator)));
        var result = await controller.Remediate(workflow.Id, new WorkflowRunRequest(tenant.Id, new() { ["userUpn"] = "user1" }), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        return await db.Context.WorkflowRuns.SingleAsync();
    }

    private void StubJson(string path, string method, Func<int, object> body)
    {
        var calls = 0;
        _server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody(_ => System.Text.Json.JsonSerializer.Serialize(body(Interlocked.Increment(ref calls)))));
    }

    private static Dictionary<string, object> Method(string type, string id) =>
        new() { ["@odata.type"] = type, ["id"] = id };

    [Fact]
    public async Task Successful_mfa_reset_is_succeeded_even_though_the_user_now_has_no_strong_mfa()
    {
        StubJson("/users/user1", "GET", _ => new { id = "u1" });
        StubJson("/users/u1", "GET", _ => new { id = "u1", signInSessionsValidFromDateTime = DateTimeOffset.UtcNow.ToString("o") });
        StubJson("/users/u1/revokeSignInSessions", "POST", _ => new { value = true });
        _server.Given(Request.Create().WithPath("/users/u1/authentication/microsoftAuthenticatorMethods/auth1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));
        // First read (what to remove) has the authenticator; every later read (verification,
        // post-run diagnosis) has only the password -- the intended end state.
        StubJson("/users/u1/authentication/methods", "GET", n => new
        {
            value = n == 1
                ? new[] { Method("#microsoft.graph.passwordAuthenticationMethod", "pwd"), Method("#microsoft.graph.microsoftAuthenticatorAuthenticationMethod", "auth1") }
                : new[] { Method("#microsoft.graph.passwordAuthenticationMethod", "pwd") }
        });

        using var db = new TestDb();
        var run = await RemediateAsync(db, Workflow("mfa-reset"));

        // The post-run diagnosis warns "no strong MFA" (that is the point of the reset)...
        Assert.Contains(run.Findings, f => f.Name == "Strong MFA" && f.Status == FindingStatus.Warning);
        // ...but the verification is that the removed method is gone and sessions were revoked.
        Assert.Equal(Outcome.Succeeded, run.Outcome);
        Assert.All(run.Evidence!.Verification, v => Assert.True(v.Passed, v.Name + ": " + v.Detail));
        Assert.Contains(run.Evidence.Verification, v => v.Name == "microsoftAuthenticator method removed" && v.PlanItemId == "step-2");
        Assert.Contains(run.Evidence.Verification, v => v.Name == "Sessions revoked" && v.PlanItemId == "step-1");
    }

    [Fact]
    public async Task Mfa_method_still_registered_after_removal_fails_verification()
    {
        StubJson("/users/user1", "GET", _ => new { id = "u1" });
        StubJson("/users/u1", "GET", _ => new { id = "u1", signInSessionsValidFromDateTime = DateTimeOffset.UtcNow.ToString("o") });
        StubJson("/users/u1/revokeSignInSessions", "POST", _ => new { value = true });
        _server.Given(Request.Create().WithPath("/users/u1/authentication/phoneMethods/ph1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));
        StubJson("/users/u1/authentication/methods", "GET", _ => new { value = new[] { Method("#microsoft.graph.phoneAuthenticationMethod", "ph1") } });

        using var db = new TestDb();
        var run = await RemediateAsync(db, Workflow("mfa-reset"));

        Assert.Equal(Outcome.VerificationFailed, run.Outcome);
        Assert.Contains(run.Evidence!.Verification, v => !v.Passed && v.Detail == "Method is still registered on re-read.");
    }

    [Fact]
    public async Task Password_reset_is_completed_unverified_never_all_changes_verified()
    {
        StubJson("/users/user1", "GET", _ => new { id = "u1", accountEnabled = true, onPremisesSyncEnabled = (bool?)null });
        StubJson("/users/u1", "GET", _ => new
        {
            id = "u1", signInSessionsValidFromDateTime = DateTimeOffset.UtcNow.ToString("o"),
            passwordProfile = new { forceChangePasswordNextSignIn = true }
        });
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch()).RespondWith(Response.Create().WithStatusCode(204));
        StubJson("/users/u1/revokeSignInSessions", "POST", _ => new { value = true });

        using var db = new TestDb();
        var run = await RemediateAsync(db, Workflow("password-reset"));

        Assert.Equal(Outcome.CompletedUnverified, run.Outcome);
        var checks = run.Evidence!.Verification;
        Assert.Contains(checks, v => v.Name == "Temporary password set" && v.Unverifiable && !v.Passed);
        Assert.Contains(checks, v => v.Name == "Must change password at next sign-in" && v.Passed);
        Assert.Contains(checks, v => v.Name == "Sessions revoked" && v.Passed);
        // The cloud-only diagnosis is not presented as verification of either change.
        Assert.DoesNotContain(checks, v => v.Name == "Directory sync");
        Assert.DoesNotContain("all changes verified", run.Evidence.TicketNotes);
        Assert.Contains("could not be independently verified", run.Evidence.TicketNotes);
        Assert.Contains("| Temporary password set | not verifiable |", EvidenceRenderer.Markdown(run.Evidence));
    }

    [Fact]
    public void Outcome_rules_completed_unverified_only_when_nothing_failed()
    {
        var verified = new ItemOutcome(true, true, true);
        var unverifiable = new ItemOutcome(true, true, false, Unverifiable: true);
        Assert.Equal(Outcome.CompletedUnverified, OutcomeRules.Derive([verified, unverifiable]));
        Assert.Equal(Outcome.Succeeded, OutcomeRules.Derive([verified]));
        Assert.Equal(Outcome.PartiallySucceeded, OutcomeRules.Derive([unverifiable, new ItemOutcome(true, false, false)]));
        Assert.Equal(Outcome.VerificationFailed, OutcomeRules.Derive([unverifiable, new ItemOutcome(true, true, false)]));
        Assert.False(OutcomeRules.IsSuccess(Outcome.CompletedUnverified));
    }
}

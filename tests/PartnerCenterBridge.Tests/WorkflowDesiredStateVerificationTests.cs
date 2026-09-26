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

    // The fake user's signInSessionsValidFromDateTime: a stale cutoff (from four minutes before the
    // run, i.e. an earlier revocation) until a revoke POST that takes effect moves it to "now".
    private DateTimeOffset? _revokedAt;
    private static readonly DateTimeOffset StaleCutoff = DateTimeOffset.UtcNow.AddMinutes(-4);
    private string Cutoff() => (_revokedAt ?? StaleCutoff).ToString("o");

    /// <summary>revokeSignInSessions: Graph acknowledges it; only an effective one moves the cutoff.</summary>
    private void StubRevoke(bool effective = true) =>
        StubJson("/users/u1/revokeSignInSessions", "POST", _ =>
        {
            if (effective) _revokedAt = DateTimeOffset.UtcNow;
            return new { value = true };
        });

    private static Dictionary<string, object> Method(string type, string id) =>
        new() { ["@odata.type"] = type, ["id"] = id };

    [Fact]
    public async Task Successful_mfa_reset_is_succeeded_even_though_the_user_now_has_no_strong_mfa()
    {
        StubJson("/users/user1", "GET", _ => new { id = "u1" });
        StubJson("/users/u1", "GET", _ => new { id = "u1", signInSessionsValidFromDateTime = Cutoff() });
        StubRevoke();
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
        StubJson("/users/u1", "GET", _ => new { id = "u1", signInSessionsValidFromDateTime = Cutoff() });
        StubRevoke();
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
            id = "u1", signInSessionsValidFromDateTime = Cutoff(),
            passwordProfile = new { forceChangePasswordNextSignIn = true }
        });
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch()).RespondWith(Response.Create().WithStatusCode(204));
        StubRevoke();

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

    [Theory]
    [InlineData("mfa-reset")]
    [InlineData("password-reset")]
    [InlineData("compromised-lockdown")]
    public async Task Revoke_that_leaves_a_recent_stale_cutoff_is_not_verified(string workflowId)
    {
        // Graph acknowledges the revoke, but the cutoff stays at an earlier revocation from four
        // minutes before this run: that proves nothing about this run's revoke.
        StubJson("/users/user1", "GET", _ => new { id = "u1", accountEnabled = true, onPremisesSyncEnabled = (bool?)null });
        StubJson("/users/u1", "GET", _ => new
        {
            id = "u1", accountEnabled = false, signInSessionsValidFromDateTime = Cutoff(),
            passwordProfile = new { forceChangePasswordNextSignIn = true }
        });
        _server.Given(Request.Create().WithPath("/users/u1").UsingPatch()).RespondWith(Response.Create().WithStatusCode(204));
        StubRevoke(effective: false);
        StubJson("/users/u1/authentication/methods", "GET", _ => new { value = Array.Empty<object>() });
        StubJson("/users/u1/mailFolders/inbox/messageRules", "GET", _ => new { value = Array.Empty<object>() });

        using var db = new TestDb();
        var run = await RemediateAsync(db, Workflow(workflowId));

        var check = Assert.Single(run.Evidence!.Verification, v => v.Name == "Sessions revoked");
        Assert.False(check.Passed);
        Assert.Contains("did not move forward", check.Detail);
        Assert.NotEqual(Outcome.Succeeded, run.Outcome);
        Assert.NotEqual(Outcome.CompletedUnverified, run.Outcome);
    }

    [Fact]
    public void Session_cutoff_must_move_forward_and_cover_the_run()
    {
        var start = DateTimeOffset.UtcNow;
        var before = new SessionCutoff(true, start.AddMinutes(-4), null);
        Assert.False(WorkflowVerify.EvaluateCutoff(before, start.AddMinutes(-4), start).Passed);  // unchanged
        Assert.True(WorkflowVerify.EvaluateCutoff(before, start.AddSeconds(1), start).Passed);
        Assert.True(WorkflowVerify.EvaluateCutoff(before, start.AddSeconds(-30), start).Passed); // PCB clock slightly ahead
        // Moved, but not to this run: an older value surfacing.
        var older = new SessionCutoff(true, start.AddDays(-2), null);
        Assert.False(WorkflowVerify.EvaluateCutoff(older, start.AddDays(-1), start).Passed);
        // Never set before: only the run-start bound applies.
        Assert.True(WorkflowVerify.EvaluateCutoff(new SessionCutoff(true, null, null), start, start).Passed);
        // Unknown before or after: not confirmed.
        Assert.False(WorkflowVerify.EvaluateCutoff(new SessionCutoff(false, null, "Graph 500"), start.AddSeconds(1), start).Passed);
        Assert.False(WorkflowVerify.EvaluateCutoff(before, null, start).Passed);
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

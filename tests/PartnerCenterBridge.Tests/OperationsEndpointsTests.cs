using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.Operations;
using PartnerCenterBridge.PartnerCenter;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

public class OperationsEndpointsTests
{
    private static OperationsController Ops(TestDb db, TenantRole? role, FakePlannedOperation op) =>
        OpsTest.WithHttp(new OperationsController(new WorkflowCatalog([op]), db.Context, new RoleAccess(role), OpsTest.Recorder(db)));

    [Fact]
    public async Task Viewer_can_plan_but_cannot_apply_access_parity()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var op = new FakePlannedOperation();

        var plan = await Ops(db, TenantRole.Viewer, op).PlanAccessParity(tenant.Id, new("alice", "bob"), CancellationToken.None);
        var apply = await Ops(db, TenantRole.Viewer, op).ApplyAccessParity(tenant.Id, new("alice", "bob", ["group:g1"]), CancellationToken.None);

        Assert.IsType<OkObjectResult>(plan.Result);
        Assert.IsType<ForbidResult>(apply.Result);
        Assert.Equal(1, op.PlanCalls);
        Assert.Equal(0, op.ApplyCalls);
        var planRun = await db.Context.WorkflowRuns.SingleAsync();
        Assert.Equal(WorkflowRunKind.Plan, planRun.Kind);
        Assert.Equal(Outcome.Planned, planRun.Outcome);
        Assert.Equal("tgt-id", planRun.TargetId);
    }

    [Fact]
    public async Task No_grant_cannot_plan_apply_view_person_or_read_evidence()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var run = new WorkflowRun { TenantId = tenant.Id, WorkflowId = "wf", WorkflowName = "wf" };
        db.Context.WorkflowRuns.Add(run);
        await db.Context.SaveChangesAsync();
        var op = new FakePlannedOperation();
        var none = new RoleAccess(null);
        using var services = new ServiceCollection().BuildServiceProvider();

        var plan = await Ops(db, null, op).PlanAccessParity(tenant.Id, new("alice", "bob"), CancellationToken.None);
        var apply = await Ops(db, null, op).ApplyAccessParity(tenant.Id, new("alice", "bob", ["group:g1"]), CancellationToken.None);
        var person = await new PeopleController(db.Context, services.GetRequiredService<IServiceScopeFactory>(), none)
            .Get(tenant.Id, "bob", CancellationToken.None);
        var evidence = await OpsTest.WithHttp(new WorkflowsController(new WorkflowCatalog([]), db.Context, new NullRunNotifier(), none))
            .Evidence(run.Id, null, CancellationToken.None);
        var terminatePlan = await new ProvisioningController(db.Context, null!, new CountingOffboardingService(), none, OpsTest.Recorder(db))
            .TerminatePlan(new TerminateApiRequest(tenant.Id, new TerminationOptions { UserId = "bob" }), CancellationToken.None);

        Assert.IsType<ForbidResult>(plan.Result);
        Assert.IsType<ForbidResult>(apply.Result);
        Assert.IsType<ForbidResult>(person.Result);
        Assert.IsType<ForbidResult>(evidence);
        Assert.IsType<ForbidResult>(terminatePlan.Result);
        Assert.Equal(0, op.PlanCalls + op.ApplyCalls);
        Assert.Equal(1, await db.Context.WorkflowRuns.CountAsync()); // nothing recorded for refused calls
    }

    [Fact]
    public async Task Viewer_cannot_terminate_but_can_plan_offboarding()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var offboarding = new CountingOffboardingService();
        ProvisioningController Controller() => new(db.Context, null!, offboarding, new RoleAccess(TenantRole.Viewer), OpsTest.Recorder(db));
        var req = new TerminateApiRequest(tenant.Id, new TerminationOptions { UserId = "bob" });

        Assert.IsType<ForbidResult>((await Controller().Terminate(req, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await Controller().TerminatePlan(req, CancellationToken.None)).Result);
        Assert.Equal(1, offboarding.Calls);
    }

    [Fact]
    public async Task Source_equal_to_target_is_a_400()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var op = new FakePlannedOperation();

        var plan = await Ops(db, TenantRole.Operator, op).PlanAccessParity(tenant.Id, new("Bob@x.com", "bob@x.com"), CancellationToken.None);
        var apply = await Ops(db, TenantRole.Operator, op).ApplyAccessParity(tenant.Id, new("bob@x.com", "bob@x.com", []), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(plan.Result);
        Assert.IsType<BadRequestObjectResult>(apply.Result);
        Assert.Equal(0, op.PlanCalls + op.ApplyCalls);
    }

    [Fact]
    public async Task Apply_persists_evidence_that_exports_as_json_and_markdown_and_filters_by_target()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var op = new FakePlannedOperation();

        var apply = await Ops(db, TenantRole.Operator, op).ApplyAccessParity(tenant.Id, new("alice", "bob", ["group:g1"]), CancellationToken.None);

        var evidence = Assert.IsType<OperationEvidence>(Assert.IsType<OkObjectResult>(apply.Result).Value);
        Assert.Equal(["group:g1"], op.LastSelection);
        var run = await db.Context.WorkflowRuns.SingleAsync();
        Assert.Equal(WorkflowRunKind.Apply, run.Kind);
        Assert.Equal(Outcome.Succeeded, run.Outcome);
        Assert.True(run.Succeeded);
        Assert.Equal("tgt-id", run.TargetId);
        Assert.Equal(run.Id.ToString(), evidence.RunId);
        Assert.Equal(tenant.TenantId, evidence.Tenant.TenantId);
        Assert.Equal("test actor", evidence.Operator);

        // Round-trips through the jsonb column.
        using var fresh = db.CreateContext();
        var stored = await fresh.WorkflowRuns.SingleAsync();
        Assert.Equal(Outcome.Succeeded, stored.Evidence!.Outcome);
        Assert.Equal("Membership: Finance", stored.Evidence.Verification.Single().Name);

        var workflows = OpsTest.WithHttp(new WorkflowsController(new WorkflowCatalog([]), fresh, new NullRunNotifier(), new RoleAccess(TenantRole.Viewer)));
        var json = Assert.IsType<OkObjectResult>(await workflows.Evidence(run.Id, null, CancellationToken.None));
        Assert.Equal(evidence.TicketNotes, Assert.IsType<OperationEvidence>(json.Value).TicketNotes);

        var md = Assert.IsType<FileContentResult>(await workflows.Evidence(run.Id, "markdown", CancellationToken.None));
        Assert.StartsWith("text/markdown", md.ContentType);
        Assert.EndsWith(".md", md.FileDownloadName);
        var text = Encoding.UTF8.GetString(md.FileContents);
        Assert.Contains("# Access parity", text);
        Assert.Contains("## Ticket notes", text);
        Assert.Contains(evidence.TicketNotes, text);
        Assert.Contains("| Membership: Finance | passed |", text);

        var byTarget = await workflows.Runs(tenant.Id, null, 0, "TGT-ID", CancellationToken.None);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<WorkflowRunDto>>(Assert.IsType<OkObjectResult>(byTarget.Result).Value);
        Assert.Equal(Outcome.Succeeded, Assert.Single(rows).Outcome);
        var other = await workflows.Runs(tenant.Id, null, 0, "someone-else", CancellationToken.None);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<WorkflowRunDto>>(Assert.IsType<OkObjectResult>(other.Result).Value));
    }

    [Fact]
    public async Task Existing_workflow_runs_now_carry_evidence_without_secrets()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var workflow = new SecretWorkflow();
        var controller = OpsTest.WithHttp(new WorkflowsController(new WorkflowCatalog([workflow]), db.Context, new NullRunNotifier(), new RoleAccess(TenantRole.Operator)));

        var remediate = await controller.Remediate(workflow.Id, new WorkflowRunRequest(tenant.Id, new() { ["userUpn"] = "User@Contoso.com" }), CancellationToken.None);
        var diagnose = await controller.Diagnose(workflow.Id, new WorkflowRunRequest(tenant.Id, new() { ["userUpn"] = "User@Contoso.com" }), CancellationToken.None);

        Assert.IsType<OkObjectResult>(remediate.Result);
        Assert.IsType<OkObjectResult>(diagnose.Result);
        var runs = await db.Context.WorkflowRuns.OrderBy(r => r.Kind).ToListAsync();
        var diag = runs.Single(r => r.Kind == WorkflowRunKind.Diagnose);
        var rem = runs.Single(r => r.Kind == WorkflowRunKind.Remediate);
        Assert.Equal("user@contoso.com", rem.TargetId);
        Assert.Equal(Outcome.Succeeded, rem.Outcome);
        Assert.Equal("Set temporary password", rem.Evidence!.Changes.Single().Action);
        Assert.Equal("Directory sync", rem.Evidence.Verification.Single().Name);
        Assert.Contains("1 of 1 step reported success", rem.Evidence.TicketNotes);
        Assert.Equal(Outcome.Planned, diag.Outcome);
        Assert.Equal("Directory sync", diag.Evidence!.Preflight.Single(f => f.Status == FindingStatus.Warning).Name);

        var serialized = System.Text.Json.JsonSerializer.Serialize(rem.Evidence) + EvidenceRenderer.Markdown(rem.Evidence);
        Assert.DoesNotContain(SecretWorkflow.Secret, serialized);
    }

    [Fact]
    public async Task Legacy_rows_without_evidence_are_adapted_on_read()
    {
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var run = new WorkflowRun
        {
            TenantId = tenant.Id, WorkflowId = "license-repair", WorkflowName = "License assignment repair",
            Kind = WorkflowRunKind.Remediate, Operator = "op", Inputs = new() { ["userUpn"] = "a@b.com" },
            Steps = [new("Set usage location", true, "US")],
            Findings = [new("Usage location", FindingStatus.Blocker, "Not set")],
            Healthy = false, Succeeded = true
        };
        db.Context.WorkflowRuns.Add(run);
        await db.Context.SaveChangesAsync();
        var controller = OpsTest.WithHttp(new WorkflowsController(new WorkflowCatalog([]), db.Context, new NullRunNotifier(), new RoleAccess(TenantRole.Viewer)));

        var e = Assert.IsType<OperationEvidence>(Assert.IsType<OkObjectResult>(await controller.Evidence(run.Id, null, CancellationToken.None)).Value);

        // Steps reported ok but the re-diagnosis still shows a blocker: never "Succeeded".
        Assert.Equal(Outcome.VerificationFailed, e.Outcome);
        Assert.Equal("op", e.Operator);
    }

    [Fact]
    public async Task Contract_offboarding_policy_defaults_validates_and_saves()
    {
        using var db = new TestDb();
        var contract = new Contract { Name = "Gold" };
        db.Context.Contracts.Add(contract);
        await db.Context.SaveChangesAsync();
        var admin = new FakeTenantAccessService(isSystemAdmin: true);
        var controller = new ContractsController(db.Context, admin, admin);

        var initial = Assert.IsType<OffboardingPolicy>(Assert.IsType<OkObjectResult>((await controller.GetOffboardingPolicy(contract.Id, CancellationToken.None)).Result).Value);
        Assert.Equal(GroupCleanupMode.RemoveAll, initial.GroupCleanup);

        var bad = await controller.PutOffboardingPolicy(contract.Id, new OffboardingPolicy { ForwardTo = "nope" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(bad.Result);

        await controller.PutOffboardingPolicy(contract.Id, new OffboardingPolicy { ConvertMailboxToShared = true, FollowUpDays = 30 }, CancellationToken.None);
        await controller.PutOffboardingPolicy(contract.Id, new OffboardingPolicy { ConvertMailboxToShared = true, FollowUpDays = 45, WipeDevices = DeviceWipeMode.Retire }, CancellationToken.None);
        using var fresh = db.CreateContext();
        var saved = (await fresh.Contracts.SingleAsync()).OffboardingPolicy!;
        Assert.True(saved.ConvertMailboxToShared);
        Assert.Equal(45, saved.FollowUpDays);
        Assert.Equal(DeviceWipeMode.Retire, saved.WipeDevices);

        var viewer = new ContractsController(db.Context, new RoleAccess(null), new FakeTenantAccessService(isSystemAdmin: false));
        Assert.IsType<ForbidResult>((await viewer.PutOffboardingPolicy(contract.Id, new OffboardingPolicy(), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Contract_offboarding_policy_read_needs_a_tenant_grant_even_for_catalog_managers()
    {
        using var db = new TestDb();
        var contract = new Contract { Name = "Gold", OffboardingPolicy = new OffboardingPolicy { ForwardTo = "boss@contoso.com" } };
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso", Contract = contract };
        db.Context.AddRange(contract, tenant);
        await db.Context.SaveChangesAsync();

        // Instance Administrator/CatalogManager with no tenant grants: cannot read the policy...
        var catalogManager = new ContractsController(db.Context, new RoleAccess(null), new FakeTenantAccessService(isSystemAdmin: true));
        Assert.IsType<ForbidResult>((await catalogManager.GetOffboardingPolicy(contract.Id, CancellationToken.None)).Result);
        // ...but can still replace it, and the PUT response carries what was saved.
        var put = await catalogManager.PutOffboardingPolicy(contract.Id, new OffboardingPolicy { FollowUpDays = 7 }, CancellationToken.None);
        Assert.Equal(7, Assert.IsType<OffboardingPolicy>(Assert.IsType<OkObjectResult>(put.Result).Value).FollowUpDays);

        // A Viewer on a tenant under the contract reads it without any instance role.
        var viewer = new ContractsController(db.Context, new RoleAccess(TenantRole.Viewer), new FakeTenantAccessService(isSystemAdmin: false));
        Assert.IsType<OkObjectResult>((await viewer.GetOffboardingPolicy(contract.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Terminate_merges_contract_policy_with_request_overrides_and_records_the_run()
    {
        using var db = new TestDb();
        var contract = new Contract { Name = "Gold", OffboardingPolicy = new OffboardingPolicy { ConvertMailboxToShared = true, FollowUpDays = 14 } };
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso", Contract = contract };
        db.Context.AddRange(contract, tenant);
        await db.Context.SaveChangesAsync();
        var offboarding = new RecordingOffboarding();
        var controller = new ProvisioningController(db.Context, null!, offboarding, new RoleAccess(TenantRole.Operator), OpsTest.Recorder(db));

        var result = await controller.Terminate(new TerminateApiRequest(tenant.Id,
            new TerminationOptions { UserId = "u1", RemoveFromGroups = false }), CancellationToken.None);

        var dto = Assert.IsType<TerminateResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(offboarding.Policy!.ConvertMailboxToShared); // from the contract (omitted in the request)
        Assert.Equal(14, offboarding.Policy.FollowUpDays);
        Assert.Equal(GroupCleanupMode.None, offboarding.Policy.GroupCleanup); // request override
        Assert.True(dto.Succeeded);
        Assert.Equal("u1", dto.UserId);
        Assert.NotNull(dto.Evidence);
        var run = await db.Context.WorkflowRuns.SingleAsync();
        Assert.Equal("offboarding", run.WorkflowId);
        Assert.Equal(WorkflowRunKind.Apply, run.Kind);
        Assert.Equal("u1", run.TargetId);
        Assert.Equal("true", run.Inputs["convertMailboxToShared"]);
    }

    private sealed class RecordingOffboarding : IOffboardingService
    {
        public OffboardingPolicy? Policy { get; private set; }
        public Task<OperationPlan> PlanAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<OperationEvidence> ApplyAsync(Tenant tenant, string userId, OffboardingPolicy policy, CancellationToken ct = default)
        {
            Policy = policy;
            return Task.FromResult(new OperationEvidence
            {
                OperationId = "offboarding", OperationName = "Offboarding", Target = new("user", "u1", "Leaver"),
                Outcome = Outcome.Succeeded,
                Plan = { new PlanItem { Id = "revoke-sessions", Action = OffboardingOperation.RevokeSessions, Eligible = true } },
                Changes = { new ChangeResult { PlanItemId = "revoke-sessions", Action = OffboardingOperation.RevokeSessions, Attempted = true, Succeeded = true } },
                Verification = { new VerificationCheck("Revoke sessions", true) }
            });
        }
    }

    /// <summary>Password-reset-like workflow: returns a show-once secret that must never reach evidence.</summary>
    private sealed class SecretWorkflow : IWorkflow
    {
        public const string Secret = "Sup3r-Secret-Temp!";
        public string Id => "secret-wf";
        public string Name => "Secret workflow";
        public string Description => "";
        public string Category => "Identity";
        public IReadOnlyList<WorkflowInput> Inputs => [new("userUpn", "User")];
        public Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
            Task.FromResult(new DiagnosisResult { Findings = [new("Directory sync", FindingStatus.Warning, "check")] });
        public Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunResult
            {
                Steps = [new("Set temporary password", true, "must change at next sign-in")],
                PostState = new DiagnosisResult { Findings = [new("Directory sync", FindingStatus.Ok, "Cloud-only account.")] },
                Ephemeral = { ["Temporary password"] = Secret }
            });
    }
}

public class PersonWorkspaceTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private ServiceProvider Services(IExchangeCapability capability, IExchangeOnlineService exchange)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITokenProvider, FakeTokenProvider>();
        services.AddSingleton<IHttpClientFactory, SingleHttpClientFactory>();
        services.AddSingleton(Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! }));
        services.AddScoped<IPersonDirectoryReader, PersonDirectoryReader>();
        services.AddSingleton(capability);
        services.AddSingleton(exchange);
        return services.BuildServiceProvider();
    }

    private void StubHappyProfile()
    {
        _server.Given(Request.Create().WithPath("/users/bob@contoso.com").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new
            {
                id = "BOB-ID", displayName = "Bob", userPrincipalName = "bob@contoso.com", mail = "bob@contoso.com",
                accountEnabled = true, jobTitle = "Analyst", department = "Finance", createdDateTime = "2024-01-01T00:00:00Z",
                signInActivity = new { lastSignInDateTime = "2026-09-20T10:00:00Z" }
            }));
        _server.Given(Request.Create().WithPath("/users/BOB-ID/licenseDetails").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = new[] { new { skuId = "s1", skuPartNumber = "SPE_E3" } } }));
        _server.Given(Request.Create().WithPath("/users/BOB-ID/authentication/methods").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = new object[]
            {
                new Dictionary<string, string> { ["@odata.type"] = "#microsoft.graph.passwordAuthenticationMethod", ["id"] = "p" },
                new Dictionary<string, string> { ["@odata.type"] = "#microsoft.graph.microsoftAuthenticatorAuthenticationMethod", ["id"] = "m" }
            } }));
    }

    [Fact]
    public async Task Sections_degrade_independently_and_recent_runs_match_by_target()
    {
        StubHappyProfile();
        const string denied = "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges\"}}";
        _server.Given(Request.Create().WithPath("/users/BOB-ID/memberOf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody(denied));
        _server.Given(Request.Create().WithPath("/users/BOB-ID/managedDevices").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody(denied));

        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        db.Context.WorkflowRuns.AddRange(
            new WorkflowRun { TenantId = tenant.Id, WorkflowId = "mfa-reset", WorkflowName = "MFA", TargetId = "bob@contoso.com" },
            new WorkflowRun { TenantId = tenant.Id, WorkflowId = "access-parity", WorkflowName = "AP", TargetId = "bob-id" },
            new WorkflowRun { TenantId = tenant.Id, WorkflowId = "mfa-reset", WorkflowName = "MFA", TargetId = "someone@contoso.com" });
        await db.Context.SaveChangesAsync();
        var exchange = new ScriptedExchange();
        using var sp = Services(new FixedExchangeCapability(false), exchange);
        var controller = new PeopleController(db.Context, sp.GetRequiredService<IServiceScopeFactory>(), new RoleAccess(TenantRole.Viewer));

        var result = await controller.Get(tenant.Id, "bob@contoso.com", CancellationToken.None);

        var dto = Assert.IsType<PersonWorkspaceDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(SectionStatus.Ok, dto.Profile.Status);
        Assert.Equal("2026-09-20T10:00:00Z", dto.Profile.Data!.LastSignIn);
        Assert.Equal("SPE_E3", dto.Licenses.Data!.Single().SkuPartNumber);
        Assert.Equal(new[] { "microsoftAuthenticator", "password" }, dto.AuthMethods.Data);
        Assert.Equal(SectionStatus.Unavailable, dto.Groups.Status);
        Assert.Contains("lacks GroupMember.Read.All", dto.Groups.Reason);
        Assert.Equal(SectionStatus.Unavailable, dto.Devices.Status);
        Assert.Contains("DeviceManagementManagedDevices.Read.All", dto.Devices.Reason);
        Assert.Equal(SectionStatus.Unavailable, dto.Mailbox.Status);
        Assert.Contains("Exchange:AppId", dto.Mailbox.Reason);
        Assert.Empty(exchange.Calls); // never attempted without configuration
        Assert.Equal(2, dto.RecentRuns.Data!.Count);
        Assert.Equal("BOB-ID", dto.UserId);
    }

    [Fact]
    public async Task Configured_exchange_uses_the_read_only_mailbox_lookup_by_upn()
    {
        StubHappyProfile();
        using var db = new TestDb();
        var tenant = await OpsTest.AddTenantAsync(db);
        var exchange = new ScriptedExchange { MailboxAfter = new MailboxInfo("bob@contoso.com", "Bob", "UserMailbox", null, false) };
        using var sp = Services(new FixedExchangeCapability(true), exchange);
        var controller = new PeopleController(db.Context, sp.GetRequiredService<IServiceScopeFactory>(), new RoleAccess(TenantRole.Viewer));

        var dto = Assert.IsType<PersonWorkspaceDto>(Assert.IsType<OkObjectResult>((await controller.Get(tenant.Id, "bob@contoso.com", CancellationToken.None)).Result).Value);

        Assert.Equal(SectionStatus.Ok, dto.Mailbox.Status);
        Assert.Equal("UserMailbox", dto.Mailbox.Data!.RecipientTypeDetails);
        Assert.Equal(new[] { "get:bob@contoso.com" }, exchange.Calls);
    }

    [Fact]
    public void Exchange_capability_names_the_missing_dependency()
    {
        static ExchangeCapabilityStatus Check(PartnerCenterBridge.Exchange.ExchangeOptions o, params string[] existing) =>
            new PartnerCenterBridge.Exchange.ExchangeCapability(Options.Create(o), p => existing.Contains(p), () => "/bin").Check();

        Assert.Contains("Exchange:AppId", Check(new()).MissingDependency);
        Assert.Contains("CertificatePath", Check(new() { AppId = "a" }).MissingDependency);
        Assert.Contains("not found", Check(new() { AppId = "a", CertificatePath = "/c.pfx" }).MissingDependency);
        Assert.Contains("pwsh", Check(new() { AppId = "a", CertificatePath = "/c.pfx", PwshPath = "/opt/pwsh" }, "/c.pfx").MissingDependency);
        Assert.True(Check(new() { AppId = "a", CertificatePath = "/c.pfx", PwshPath = "/opt/pwsh" }, "/c.pfx", "/opt/pwsh").Available);
    }
}

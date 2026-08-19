using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Mcp;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Tests;

public class WorkflowToolsTests
{
    [Fact]
    public async Task RemediateWorkflow_rejects_a_caller_without_Operator_access_before_running_or_staging()
    {
        using var db = new TestDb();
        var tenant = await AddTenantAsync(db, McpApprovalMode.ClientTrust);
        var first = new FakeWorkflow("first");
        var target = new FakeWorkflow("target");
        var tools = CreateTools(db, new TestTenantAccessService(hasRole: false), first, target);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.RemediateWorkflow(target.Id, tenant.Id, new(), CancellationToken.None));

        Assert.Equal(0, target.RemediateCallCount);
        Assert.Empty(db.Context.PendingActions);
    }

    [Fact]
    public async Task RemediateWorkflow_in_ClientTrust_runs_the_matching_workflow_with_the_exact_inputs_without_staging()
    {
        using var db = new TestDb();
        var tenant = await AddTenantAsync(db, McpApprovalMode.ClientTrust);
        var first = new FakeWorkflow("first");
        var target = new FakeWorkflow("target");
        var tools = CreateTools(db, new TestTenantAccessService(), first, target);
        var inputs = new Dictionary<string, string> { ["user"] = "operator@contoso.com", ["action"] = "repair" };

        var result = await tools.RemediateWorkflow(target.Id, tenant.Id, inputs, CancellationToken.None);

        Assert.Contains("Succeeded=True", result);
        Assert.Equal(0, first.RemediateCallCount);
        Assert.Equal(1, target.RemediateCallCount);
        Assert.Equal(inputs.OrderBy(pair => pair.Key), target.LastRemediateInputs!.OrderBy(pair => pair.Key));
        Assert.Empty(db.Context.PendingActions);
    }

    [Fact]
    public async Task RemediateWorkflow_in_Queue_mode_stages_a_pending_remediation_without_executing_it()
    {
        using var db = new TestDb();
        var tenant = await AddTenantAsync(db, McpApprovalMode.Queue);
        var first = new FakeWorkflow("first");
        var target = new FakeWorkflow("target");
        var tools = CreateTools(db, new TestTenantAccessService(), first, target);

        await tools.RemediateWorkflow(target.Id, tenant.Id, new() { ["item"] = "license" }, CancellationToken.None);

        var action = await db.Context.PendingActions.SingleAsync();
        Assert.Equal(0, target.RemediateCallCount);
        Assert.Equal(1, target.DiagnoseCallCount);
        Assert.Equal(tenant.Id, action.TenantId);
        Assert.Equal("workflow.remediate", action.ActionType);
        Assert.Equal(PendingActionStatus.Pending, action.Status);
        Assert.Contains("item=license", action.PreviewSummary);
    }

    [Fact]
    public async Task RemediateWorkflow_in_Queue_mode_selects_the_matching_workflow_and_stages_its_exact_inputs()
    {
        using var db = new TestDb();
        var tenant = await AddTenantAsync(db, McpApprovalMode.Queue);
        var first = new FakeWorkflow("first");
        var target = new FakeWorkflow("target");
        var tools = CreateTools(db, new TestTenantAccessService(), first, target);
        var inputs = new Dictionary<string, string> { ["scope"] = "all", ["requestId"] = "42" };

        await tools.RemediateWorkflow(target.Id, tenant.Id, inputs, CancellationToken.None);

        var action = await db.Context.PendingActions.SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<WorkflowRemediatePayload>(action.PayloadJson)!;
        Assert.Equal(0, first.DiagnoseCallCount);
        Assert.Equal(1, target.DiagnoseCallCount);
        Assert.Equal(target.Id, payload.WorkflowId);
        Assert.Equal(inputs.OrderBy(pair => pair.Key), payload.Inputs.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task RemediateWorkflow_rejects_password_reset_in_Queue_mode_without_staging()
    {
        using var db = new TestDb();
        var tenant = await AddTenantAsync(db, McpApprovalMode.Queue);
        var passwordReset = new FakeWorkflow("password-reset");
        var other = new FakeWorkflow("other");
        var tools = CreateTools(db, new TestTenantAccessService(), other, passwordReset);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.RemediateWorkflow(passwordReset.Id, tenant.Id, new(), CancellationToken.None));

        Assert.Contains("one-time value", exception.Message);
        Assert.Empty(db.Context.PendingActions);
    }

    [Fact]
    public async Task RemediateWorkflow_in_ClientTrust_returns_ephemeral_values_directly()
    {
        using var db = new TestDb();
        var tenant = await AddTenantAsync(db, McpApprovalMode.ClientTrust);
        var ordinary = new FakeWorkflow("ordinary");
        var secret = new FakeWorkflow("secret", new Dictionary<string, string> { ["Temporary password"] = "show-once-value" });
        var tools = CreateTools(db, new TestTenantAccessService(), ordinary, secret);

        var result = await tools.RemediateWorkflow(secret.Id, tenant.Id, new(), CancellationToken.None);

        Assert.Contains("Temporary password=show-once-value", result);
    }

    private static async Task<Tenant> AddTenantAsync(TestDb db, McpApprovalMode mode)
    {
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso", McpApprovalMode = mode };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        return tenant;
    }

    private static WorkflowTools CreateTools(TestDb db, TestTenantAccessService access, params IWorkflow[] workflows)
    {
        var actor = new FakeCurrentActor();
        return new WorkflowTools(
            new WorkflowCatalog(workflows),
            db.Context,
            new NoOpRunNotifier(),
            access,
            actor,
            new PendingActionService(db.Context, actor));
    }

    private sealed class FakeWorkflow : IWorkflow
    {
        private readonly Dictionary<string, string> _ephemeral;

        public FakeWorkflow(string id, Dictionary<string, string>? ephemeral = null)
        {
            Id = id;
            _ephemeral = ephemeral ?? new();
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => "test workflow";
        public string Category => "Test";
        public IReadOnlyList<WorkflowInput> Inputs => Array.Empty<WorkflowInput>();
        public int DiagnoseCallCount { get; private set; }
        public int RemediateCallCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastRemediateInputs { get; private set; }

        public Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
        {
            DiagnoseCallCount++;
            return Task.FromResult(new DiagnosisResult());
        }

        public Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
        {
            RemediateCallCount++;
            LastRemediateInputs = new Dictionary<string, string>(inputs);
            return Task.FromResult(new WorkflowRunResult
            {
                Steps = new List<ProvisioningStep> { new("remediated", true) },
                Ephemeral = new(_ephemeral)
            });
        }
    }

    private sealed class TestTenantAccessService : ITenantAccessService
    {
        private readonly bool _hasRole;

        public TestTenantAccessService(bool hasRole = true, Guid? currentUserId = null)
        {
            _hasRole = hasRole;
            CurrentUserId = currentUserId ?? Guid.NewGuid();
        }

        public bool IsSystemAdmin => false;
        public Guid? CurrentUserId { get; }
        public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct) => Task.FromResult(_hasRole);
    }

    private sealed class NoOpRunNotifier : IRunNotifier
    {
        public Task NotifyAsync(WorkflowRun run, CancellationToken ct = default) => Task.CompletedTask;
    }
}

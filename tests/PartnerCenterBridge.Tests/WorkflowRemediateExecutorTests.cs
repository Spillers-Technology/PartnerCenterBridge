using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Mcp;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Tests;

public class WorkflowRemediateExecutorTests
{
    // DiagnosisResult/WorkflowRunResult are plain classes with computed Healthy/Succeeded
    // properties (derived from Findings/Steps respectively), not positional-constructor records --
    // and IWorkflow's own methods take IReadOnlyDictionary<string, string>, not Dictionary, so an
    // implementer's signature must match that exactly to satisfy the interface.
    private class FakeWorkflow : IWorkflow
    {
        private readonly bool _succeeds;

        public FakeWorkflow(string id, bool succeeds = true)
        {
            Id = id;
            _succeeds = succeeds;
        }

        public string Id { get; }
        public string Name => "Fake";
        public string Description => "test";
        public string Category => "Test";
        public IReadOnlyList<WorkflowInput> Inputs => Array.Empty<WorkflowInput>();
        public int RemediateCallCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastRemediateInputs { get; private set; }
        public Task<DiagnosisResult> DiagnoseAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default) =>
            Task.FromResult(new DiagnosisResult());
        public Task<WorkflowRunResult> RemediateAsync(Tenant tenant, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
        {
            RemediateCallCount++;
            LastRemediateInputs = new Dictionary<string, string>(inputs);
            return Task.FromResult(new WorkflowRunResult
            {
                Steps = new List<ProvisioningStep> { new("fake step", _succeeds) },
                PostState = new DiagnosisResult
                {
                    Findings = new List<Finding>
                    {
                        new("post state", _succeeds ? FindingStatus.Ok : FindingStatus.Blocker)
                    }
                }
            });
        }
    }

    [Fact]
    public async Task ExecuteAsync_deserializes_the_payload_and_runs_the_matching_workflow()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();

        var workflow = new FakeWorkflow("fake.workflow");
        var catalog = new WorkflowCatalog(new[] { workflow });
        var executor = new WorkflowRemediateExecutor(catalog, db.Context, new NoOpRunNotifier(), new FakeCurrentActor());

        var action = new PendingAction
        {
            TenantId = tenant.Id,
            ActionType = "workflow.remediate",
            RequestedByUserId = Guid.NewGuid(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new WorkflowRemediatePayload("fake.workflow", new())),
            PreviewSummary = "runs fake workflow"
        };

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal(1, workflow.RemediateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_runs_the_payload_workflow_with_its_exact_inputs()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();

        var first = new FakeWorkflow("first.workflow");
        var target = new FakeWorkflow("target.workflow");
        var executor = new WorkflowRemediateExecutor(
            new WorkflowCatalog(new[] { first, target }), db.Context, new NoOpRunNotifier(), new FakeCurrentActor());
        var inputs = new Dictionary<string, string> { ["scope"] = "all", ["requestId"] = "42" };
        var action = new PendingAction
        {
            TenantId = tenant.Id,
            ActionType = "workflow.remediate",
            RequestedByUserId = Guid.NewGuid(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new WorkflowRemediatePayload(target.Id, inputs)),
            PreviewSummary = "runs target workflow"
        };

        await executor.ExecuteAsync(action, CancellationToken.None);

        Assert.Equal(0, first.RemediateCallCount);
        Assert.Equal(1, target.RemediateCallCount);
        Assert.Equal(inputs.OrderBy(pair => pair.Key), target.LastRemediateInputs!.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Unsuccessful_result_is_persisted_and_leaves_approved_action_retryable_with_error()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t1", DisplayName = "Contoso" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var actor = new FakeCurrentActor();
        var pending = new PendingActionService(db.Context, actor);
        var workflow = new FakeWorkflow("failing.workflow", succeeds: false);
        var notifier = new RecordingRunNotifier();
        var executor = new WorkflowRemediateExecutor(
            new WorkflowCatalog(new[] { workflow }), db.Context, notifier, actor);
        var action = await pending.StageAsync(
            tenant.Id,
            executor.ActionType,
            Guid.NewGuid(),
            new WorkflowRemediatePayload(workflow.Id, new() { ["userUpn"] = "user@contoso.com" }),
            "preview",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pending.ApproveAsync(action.Id, Guid.NewGuid(),
                approved => executor.ExecuteAsync(approved, CancellationToken.None), CancellationToken.None));

        await using var verificationContext = db.CreateContext();
        var persistedAction = await verificationContext.PendingActions.FindAsync(action.Id);
        var run = await verificationContext.WorkflowRuns.SingleAsync();
        Assert.Contains("unsuccessfully", exception.Message);
        Assert.Equal(PendingActionStatus.Approved, persistedAction!.Status);
        Assert.Contains("unsuccessfully", persistedAction.ExecutionError);
        Assert.False(run.Succeeded);
        Assert.Equal(WorkflowRunKind.Remediate, run.Kind);
        Assert.False(run.Healthy);
        Assert.Contains(run.Steps, step => !step.Success);
        Assert.Contains(run.Findings, finding => finding.Status == FindingStatus.Blocker);
        Assert.Equal(run.Id, notifier.NotifiedRunId);
    }

    private sealed class NoOpRunNotifier : IRunNotifier
    {
        public Task NotifyAsync(WorkflowRun run, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRunNotifier : IRunNotifier
    {
        public Guid? NotifiedRunId { get; private set; }

        public Task NotifyAsync(WorkflowRun run, CancellationToken ct = default)
        {
            NotifiedRunId = run.Id;
            return Task.CompletedTask;
        }
    }
}

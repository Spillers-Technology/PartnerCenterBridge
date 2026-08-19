using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class PendingActionsControllerTests
{
    private class NoopExecutor : IPendingActionExecutor
    {
        public string ActionType => "test.action";
        public bool Ran { get; private set; }
        public Task ExecuteAsync(PendingAction action, CancellationToken ct)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Approve_runs_the_matching_executor()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var tenantId = Guid.NewGuid();
        db.Context.Tenants.Add(new Tenant { Id = tenantId, TenantId = "t", DisplayName = "Test Tenant" });
        await db.Context.SaveChangesAsync();
        var staged = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executor = new NoopExecutor();

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), new[] { executor });
        var result = await controller.Approve(staged.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True(executor.Ran);
    }

    [Fact]
    public async Task Approve_without_a_registered_executor_returns_a_server_error_not_a_silent_success()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var tenantId = Guid.NewGuid();
        db.Context.Tenants.Add(new Tenant { Id = tenantId, TenantId = "t", DisplayName = "Test Tenant" });
        await db.Context.SaveChangesAsync();
        var staged = await pending.StageAsync(tenantId, "unknown.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), Array.Empty<IPendingActionExecutor>());
        var result = await controller.Approve(staged.Id, CancellationToken.None);

        Assert.IsType<ObjectResult>(result);
    }

    [Fact]
    public async Task Reject_never_calls_the_executor()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var tenantId = Guid.NewGuid();
        db.Context.Tenants.Add(new Tenant { Id = tenantId, TenantId = "t", DisplayName = "Test Tenant" });
        await db.Context.SaveChangesAsync();
        var staged = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executor = new NoopExecutor();

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), new[] { executor });
        await controller.Reject(staged.Id, CancellationToken.None);

        Assert.False(executor.Ran);
    }

    [Fact]
    public async Task Retry_runs_the_matching_executor_for_a_failed_action()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var tenantId = Guid.NewGuid();
        db.Context.Tenants.Add(new Tenant { Id = tenantId, TenantId = "t", DisplayName = "Test Tenant" });
        await db.Context.SaveChangesAsync();
        var staged = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pending.ApproveAsync(staged.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));
        var executor = new NoopExecutor();

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), new[] { executor });
        var result = await controller.Retry(staged.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True(executor.Ran);
    }

    [Fact]
    public async Task Retry_of_a_non_retryable_action_returns_conflict_without_calling_the_executor()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var tenantId = Guid.NewGuid();
        db.Context.Tenants.Add(new Tenant { Id = tenantId, TenantId = "t", DisplayName = "Test Tenant" });
        await db.Context.SaveChangesAsync();
        var staged = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executor = new NoopExecutor();

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), new[] { executor });
        var result = await controller.Retry(staged.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.False(executor.Ran);
    }

    [Fact]
    public async Task List_includes_pending_and_failed_retryable_actions_with_their_error()
    {
        using var db = new TestDb();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var tenantId = Guid.NewGuid();
        db.Context.Tenants.Add(new Tenant { Id = tenantId, TenantId = "t", DisplayName = "Test Tenant" });
        await db.Context.SaveChangesAsync();
        var pendingAction = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "pending", CancellationToken.None);
        var failedAction = await pending.StageAsync(tenantId, "test.action", Guid.NewGuid(), new { }, "failed", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pending.ApproveAsync(failedAction.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var controller = new PendingActionsController(db.Context, pending, new FakeTenantAccessService(isSystemAdmin: true), Array.Empty<IPendingActionExecutor>());
        var result = await controller.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<PendingActionsController.PendingActionDto>>(ok.Value);
        Assert.Contains(items, item => item.Id == pendingAction.Id && item.ExecutionError is null);
        Assert.Contains(items, item => item.Id == failedAction.Id && item.ExecutionError == "boom");
    }
}

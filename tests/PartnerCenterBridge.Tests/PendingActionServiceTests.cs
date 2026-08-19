using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class PendingActionServiceTests
{
    [Fact]
    public async Task Stage_creates_a_pending_row()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = new PendingActionService(db.Context);
        var userId = Guid.NewGuid();

        var action = await svc.StageAsync(tenant.Id, "test.action", userId, new { note = "x" }, "does a thing", CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Pending, persisted!.Status);
        Assert.Equal(tenant.Id, persisted.TenantId);
        Assert.Contains("\"note\"", persisted.PayloadJson);
    }

    [Fact]
    public async Task Approve_runs_the_executor_and_marks_Executed()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var executed = false;

        var result = await svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => { executed = true; return Task.CompletedTask; }, CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.True(executed);
        Assert.Equal(PendingActionStatus.Executed, result.Status);
        Assert.NotNull(result.ExecutedAt);
        Assert.Equal(PendingActionStatus.Executed, persisted!.Status);
        Assert.NotNull(persisted.ExecutedAt);
    }

    [Fact]
    public async Task Approve_records_the_error_and_rethrows_when_execution_fails()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal("boom", exception.Message);
        Assert.Equal("boom", persisted!.ExecutionError);
    }

    [Fact]
    public async Task Reject_marks_Rejected_and_never_executes()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var result = await svc.RejectAsync(action.Id, Guid.NewGuid(), CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Rejected, result.Status);
        Assert.Equal(PendingActionStatus.Rejected, persisted!.Status);
    }

    [Fact]
    public async Task Approve_a_second_time_throws_instead_of_re_executing()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        await svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None));

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Executed, persisted!.Status);
    }

    [Fact]
    public async Task GetAsync_lazily_expires_a_stale_pending_row()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = new PendingActionService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        action.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.Context.SaveChangesAsync();

        var reloaded = await svc.GetAsync(action.Id, CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Expired, reloaded!.Status);
        Assert.Equal(PendingActionStatus.Expired, persisted!.Status);
    }

    [Fact]
    public async Task Concurrent_approvals_claim_once_and_execute_once()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var action = await new PendingActionService(db.Context).StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        using var firstContext = db.CreateContext();
        using var secondContext = db.CreateContext();
        var firstService = new PendingActionService(firstContext);
        var secondService = new PendingActionService(secondContext);
        var firstExecutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;

        async Task Execute(PendingAction _)
        {
            if (Interlocked.Increment(ref executionCount) == 1)
            {
                firstExecutionStarted.TrySetResult();
                await releaseFirstExecution.Task;
            }
        }

        var firstApproval = firstService.ApproveAsync(
            action.Id, Guid.NewGuid(), Execute, CancellationToken.None);
        await firstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                secondService.ApproveAsync(action.Id, Guid.NewGuid(), Execute, CancellationToken.None));
        }
        finally
        {
            releaseFirstExecution.TrySetResult();
        }

        await firstApproval;

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(1, executionCount);
        Assert.Equal(PendingActionStatus.Executed, persisted!.Status);
    }
}

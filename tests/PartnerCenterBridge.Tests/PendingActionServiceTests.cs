using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

public class PendingActionServiceTests
{
    private static readonly FakeCurrentActor Actor = new();

    private static PendingActionService CreateService(BridgeDbContext db) => new(db, Actor);

    [Fact]
    public async Task Stage_creates_a_pending_row()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
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
        var svc = CreateService(db.Context);
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
        await AssertAuditDetailsAsync(db, action.Id, "approved", "executed");
    }

    [Fact]
    public async Task Approve_persists_final_audit_when_request_is_cancelled_after_claim()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var executed = false;

        var result = await svc.ApproveAsync(action.Id, Guid.NewGuid(), _ =>
        {
            executed = true;
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);

        Assert.True(executed);
        Assert.Equal(PendingActionStatus.Executed, result.Status);
        await AssertAuditDetailsAsync(db, action.Id, "approved", "executed");
    }

    [Fact]
    public async Task Approve_records_the_error_and_rethrows_when_execution_fails()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal("boom", exception.Message);
        Assert.Equal("boom", persisted!.ExecutionError);
        await AssertAuditDetailsAsync(db, action.Id, "approved", "execution failed: boom");
    }

    [Fact]
    public async Task Reject_marks_Rejected_and_never_executes()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        var result = await svc.RejectAsync(action.Id, Guid.NewGuid(), CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Rejected, result.Status);
        Assert.Equal(PendingActionStatus.Rejected, persisted!.Status);
        await AssertAuditDetailsAsync(db, action.Id, "rejected");
    }

    [Fact]
    public async Task Approve_a_second_time_throws_instead_of_re_executing()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
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
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        action.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.Context.SaveChangesAsync();

        var reloaded = await svc.GetAsync(action.Id, CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Expired, reloaded!.Status);
        Assert.Equal(PendingActionStatus.Expired, persisted!.Status);
        await AssertAuditDetailsAsync(db, action.Id, "expired");
    }

    [Fact]
    public async Task Concurrent_approvals_claim_once_and_execute_once()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var action = await CreateService(db.Context).StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        using var firstContext = db.CreateContext();
        using var secondContext = db.CreateContext();
        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);
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

    [Fact]
    public async Task Concurrent_approval_and_rejection_claim_once()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var action = await CreateService(db.Context).StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        using var approveContext = db.CreateContext();
        using var rejectContext = db.CreateContext();
        var approveService = CreateService(approveContext);
        var rejectService = CreateService(rejectContext);
        var approvalExecutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApprovalExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;

        async Task Execute(PendingAction _)
        {
            Interlocked.Increment(ref executionCount);
            approvalExecutionStarted.TrySetResult();
            await releaseApprovalExecution.Task;
        }

        var approval = approveService.ApproveAsync(
            action.Id, Guid.NewGuid(), Execute, CancellationToken.None);
        await approvalExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                rejectService.RejectAsync(action.Id, Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            releaseApprovalExecution.TrySetResult();
        }

        await approval;

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(1, executionCount);
        Assert.Equal(PendingActionStatus.Executed, persisted!.Status);
    }

    /// <summary>
    /// Approval rejects an action whose expiry has already passed and does not execute it.
    /// </summary>
    [Fact]
    public async Task Approve_of_an_already_expired_pending_action_fails_without_executing()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var action = await CreateService(db.Context).StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        action.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.Context.SaveChangesAsync();
        using var approveContext = db.CreateContext();
        var approveService = CreateService(approveContext);
        var executionCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => approveService.ApproveAsync(
            action.Id, Guid.NewGuid(), _ =>
            {
                Interlocked.Increment(ref executionCount);
                return Task.CompletedTask;
            }, CancellationToken.None));

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(0, executionCount);
        Assert.Equal(PendingActionStatus.Expired, persisted!.Status);
    }

    [Fact]
    public async Task GetAsync_does_not_expire_an_already_executed_action()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);

        await svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None);
        using (var expiryContext = db.CreateContext())
        {
            var terminalAction = await expiryContext.PendingActions.FindAsync(action.Id);
            terminalAction!.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
            await expiryContext.SaveChangesAsync();
        }

        var reloaded = await svc.GetAsync(action.Id, CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(PendingActionStatus.Executed, reloaded!.Status);
        Assert.Equal(PendingActionStatus.Executed, persisted!.Status);
    }

    [Fact]
    public async Task Retry_runs_the_executor_and_marks_Executed()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));
        var executed = false;

        var result = await svc.RetryAsync(action.Id, _ =>
        {
            executed = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.True(executed);
        Assert.Equal(PendingActionStatus.Executed, result.Status);
        Assert.Null(persisted!.ExecutionError);
        Assert.Equal(PendingActionStatus.Executed, persisted.Status);
        await AssertAuditDetailsAsync(db, action.Id, "retried", "retried, succeeded");
    }

    [Fact]
    public async Task Retry_replaces_the_error_and_rethrows_when_execution_fails_again()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var svc = CreateService(db.Context);
        var action = await svc.StageAsync(tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("first failure"), CancellationToken.None));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RetryAsync(action.Id, _ => throw new InvalidOperationException("second failure"), CancellationToken.None));

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal("second failure", exception.Message);
        Assert.Equal(PendingActionStatus.Approved, persisted!.Status);
        Assert.Equal("second failure", persisted.ExecutionError);
        await AssertAuditDetailsAsync(db, action.Id, "retried", "retried, failed again: second failure");
    }

    [Fact]
    public async Task Concurrent_retries_claim_once_and_execute_once()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var action = await CreateService(db.Context).StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(db.Context).ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));
        using var firstContext = db.CreateContext();
        using var secondContext = db.CreateContext();
        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);
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

        var firstRetry = firstService.RetryAsync(action.Id, Execute, CancellationToken.None);
        await firstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                secondService.RetryAsync(action.Id, Execute, CancellationToken.None));
        }
        finally
        {
            releaseFirstExecution.TrySetResult();
        }

        await firstRetry;

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.PendingActions.FindAsync(action.Id);
        Assert.Equal(1, executionCount);
        Assert.Equal(PendingActionStatus.Executed, persisted!.Status);
    }

    private static async Task AssertAuditDetailsAsync(TestDb db, Guid actionId, params string[] expectedDetails)
    {
        using var verifyContext = db.CreateContext();
        var events = await verifyContext.AuditEvents
            .Where(e => e.EntityType == nameof(PendingAction) && e.EntityId == actionId.ToString())
            .ToListAsync();
        Assert.All(events, e =>
        {
            Assert.Equal(AuditEventType.EntityModified, e.EventType);
            Assert.Equal(Actor.UserId, e.ActorUserId);
            Assert.Equal(Actor.Name, e.ActorName);
        });
        Assert.All(expectedDetails, detail => Assert.Contains(events, e => e.Detail == detail));
    }
}

public sealed class FakeCurrentActor : ICurrentActor
{
    public Guid? UserId { get; } = Guid.NewGuid();
    public string Name { get; } = "test actor";
}

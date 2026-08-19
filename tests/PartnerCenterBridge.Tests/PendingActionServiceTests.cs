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

        Assert.Equal(PendingActionStatus.Pending, action.Status);
        Assert.Equal(tenant.Id, action.TenantId);
        Assert.Contains("\"note\"", action.PayloadJson);
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

        Assert.True(executed);
        Assert.Equal(PendingActionStatus.Executed, result.Status);
        Assert.NotNull(result.ExecutedAt);
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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApproveAsync(action.Id, Guid.NewGuid(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var reloaded = await db.Context.PendingActions.FindAsync(action.Id);
        Assert.Equal("boom", reloaded!.ExecutionError);
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

        Assert.Equal(PendingActionStatus.Rejected, result.Status);
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

        Assert.Equal(PendingActionStatus.Expired, reloaded!.Status);
    }
}

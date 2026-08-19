using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Mcp;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class PendingActionToolsTests
{
    [Fact]
    public async Task CheckPendingAction_returns_the_staged_actions_status()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "tenant", DisplayName = "Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var action = await pending.StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var tools = new PendingActionTools(db.Context, new ToolAccessService(hasRole: true), pending);

        var result = await tools.CheckPendingAction(action.Id, CancellationToken.None);

        Assert.Equal(action.Id, result.Id);
        Assert.Equal(PendingActionStatus.Pending, result.Status);
        Assert.Null(result.ExecutionError);
    }

    [Fact]
    public async Task CheckPendingAction_rejects_a_caller_without_viewer_access()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = "tenant", DisplayName = "Tenant" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        var pending = new PendingActionService(db.Context, new FakeCurrentActor());
        var action = await pending.StageAsync(
            tenant.Id, "test.action", Guid.NewGuid(), new { }, "preview", CancellationToken.None);
        var access = new ToolAccessService(hasRole: false);
        var tools = new PendingActionTools(db.Context, access, pending);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.CheckPendingAction(action.Id, CancellationToken.None));

        Assert.Equal(TenantRole.Viewer, access.LastMinimumRole);
        Assert.Equal(tenant.Id, access.LastTenantId);
    }

    private sealed class ToolAccessService : ITenantAccessService
    {
        private readonly bool _hasRole;

        public ToolAccessService(bool hasRole) => _hasRole = hasRole;

        public bool IsSystemAdmin => false;
        public Guid? CurrentUserId => Guid.NewGuid();
        public Guid? LastTenantId { get; private set; }
        public TenantRole? LastMinimumRole { get; private set; }

        public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct)
        {
            LastTenantId = tenantId;
            LastMinimumRole = minimum;
            return Task.FromResult(_hasRole);
        }
    }
}

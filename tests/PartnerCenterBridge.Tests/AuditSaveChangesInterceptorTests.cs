using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Tests;

public class AuditSaveChangesInterceptorTests
{
    [Fact]
    public async Task PendingAction_created_audit_uses_the_actions_tenant_id()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var actor = new FakeCurrentActor();
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(actor))
            .Options;
        await using var db = new BridgeDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var tenant = new Tenant { TenantId = "tenant", DisplayName = "Tenant" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var action = new PendingAction
        {
            TenantId = tenant.Id,
            ActionType = "test.action",
            RequestedByUserId = Guid.NewGuid(),
            PayloadJson = "{}",
            PreviewSummary = "preview"
        };
        db.PendingActions.Add(action);

        await db.SaveChangesAsync();

        var audit = await db.AuditEvents.SingleAsync(a =>
            a.EntityType == nameof(PendingAction) && a.EntityId == action.Id.ToString());
        Assert.Equal(tenant.Id, audit.TenantId);
    }
}

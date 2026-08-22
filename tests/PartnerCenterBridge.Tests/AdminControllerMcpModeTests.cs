using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class AdminControllerMcpModeTests
{
    private static Tenant NewTenant() => new() { TenantId = "t1", DisplayName = "Contoso" };

    [Fact]
    public async Task SetMcpMode_defaults_to_Queue_and_system_admin_can_change_it()
    {
        using var db = new TestDb();
        var tenant = NewTenant();
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        Assert.Equal(McpApprovalMode.Queue, tenant.McpApprovalMode);

        var controller = new AdminController(new FakeSamTokenStore(), new FakeTenantAccessService(isSystemAdmin: true), db.Context);
        var result = await controller.SetMcpMode(tenant.Id, new AdminController.SetMcpModeRequest(McpApprovalMode.ClientTrust), CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        Assert.Equal(McpApprovalMode.ClientTrust, (await db.Context.Tenants.FindAsync(tenant.Id))!.McpApprovalMode);
    }

    [Fact]
    public async Task SetMcpMode_rejects_non_system_admin()
    {
        using var db = new TestDb();
        var tenant = NewTenant();
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();

        var controller = new AdminController(new FakeSamTokenStore(), new FakeTenantAccessService(isSystemAdmin: false), db.Context);
        var result = await controller.SetMcpMode(tenant.Id, new AdminController.SetMcpModeRequest(McpApprovalMode.ClientTrust), CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(result);
    }

    [Fact]
    public async Task SetMcpMode_rejects_out_of_range_mode_without_changing_tenant()
    {
        using var db = new TestDb();
        var tenant = NewTenant();
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();

        var controller = new AdminController(new FakeSamTokenStore(), new FakeTenantAccessService(isSystemAdmin: true), db.Context);
        var result = await controller.SetMcpMode(tenant.Id, new AdminController.SetMcpModeRequest((McpApprovalMode)42), CancellationToken.None);

        var badRequest = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        Assert.Equal("Invalid mode.", badRequest.Value);
        Assert.Equal(McpApprovalMode.Queue, (await db.Context.Tenants.FindAsync(tenant.Id))!.McpApprovalMode);
    }
}

public class FakeSamTokenStore : PartnerCenterBridge.Core.Abstractions.ISamTokenStore
{
    public Task<string?> GetRefreshTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;
}

public class FakeTenantAccessService : PartnerCenterBridge.Api.Auth.ITenantAccessService,
    PartnerCenterBridge.Api.Auth.IInstanceAccessService
{
    private readonly bool _isSystemAdmin;
    public FakeTenantAccessService(bool isSystemAdmin) : this(isSystemAdmin, Guid.NewGuid()) { }
    public FakeTenantAccessService(bool isSystemAdmin, Guid? currentUserId)
    {
        _isSystemAdmin = isSystemAdmin;
        CurrentUserId = currentUserId;
    }
    public bool IsSystemAdmin => _isSystemAdmin;
    public Guid? CurrentUserId { get; }
    public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct) => Task.FromResult(true);
    public Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>?>(null);
    public Task<InstanceRole> GetRolesAsync(CancellationToken ct) =>
        Task.FromResult(_isSystemAdmin ? InstanceRole.Administrator : InstanceRole.None);
    public Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) =>
        Task.FromResult(_isSystemAdmin);
}

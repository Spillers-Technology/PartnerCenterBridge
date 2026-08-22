using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class InstanceEndpointPermissionTests
{
    [Fact]
    public async Task Catalog_permission_can_author_catalog_but_not_seed_sam_or_onboard_tenants()
    {
        using var db = new TestDb();
        var instance = new PermissionAccess(InstancePermission.ManageCatalog);
        var tenant = new AlwaysTenantAccess();
        var templates = new AppTemplatesController(db.Context, tenant, instance);
        var contracts = new ContractsController(db.Context, tenant, instance);
        var admin = new AdminController(new FakeSamTokenStore(), instance, db.Context);
        var tenants = new TenantsController(db.Context, tenant, instance);

        var template = await templates.Create(NewTemplateRequest(), CancellationToken.None);
        var contract = await contracts.Create(new CreateContractRequest("Baseline", null), CancellationToken.None);
        var sam = await admin.Seed(new AdminController.SeedRequest("secret"), CancellationToken.None);
        var onboard = await tenants.Create(new CreateTenantRequest("tenant-id", "Tenant", null), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(template.Result);
        Assert.IsType<CreatedAtActionResult>(contract.Result);
        Assert.IsType<ForbidResult>(sam);
        Assert.IsType<ForbidResult>(onboard.Result);
    }

    [Fact]
    public async Task Credential_permission_can_seed_sam_but_not_author_catalog()
    {
        using var db = new TestDb();
        var instance = new PermissionAccess(InstancePermission.ManageSam);
        var tenant = new AlwaysTenantAccess();

        var sam = await new AdminController(new FakeSamTokenStore(), instance, db.Context)
            .Seed(new AdminController.SeedRequest("secret"), CancellationToken.None);
        var template = await new AppTemplatesController(db.Context, tenant, instance)
            .Create(NewTemplateRequest(), CancellationToken.None);

        Assert.IsType<NoContentResult>(sam);
        Assert.IsType<ForbidResult>(template.Result);
    }

    [Fact]
    public async Task Automation_policy_permission_can_change_policy_but_not_seed_sam()
    {
        using var db = new TestDb();
        var tenantEntity = new Tenant { TenantId = "tenant", DisplayName = "Tenant" };
        db.Context.Tenants.Add(tenantEntity);
        await db.Context.SaveChangesAsync();
        var instance = new PermissionAccess(InstancePermission.ManageMcpPolicy);
        var controller = new AdminController(new FakeSamTokenStore(), instance, db.Context);

        var policy = await controller.SetMcpMode(tenantEntity.Id,
            new AdminController.SetMcpModeRequest(McpApprovalMode.ClientTrust), CancellationToken.None);
        var sam = await controller.Seed(new AdminController.SeedRequest("secret"), CancellationToken.None);

        Assert.IsType<NoContentResult>(policy);
        Assert.IsType<ForbidResult>(sam);
    }

    [Fact]
    public async Task Registry_permission_can_onboard_but_not_author_catalog()
    {
        using var db = new TestDb();
        var instance = new PermissionAccess(InstancePermission.ManageTenantRegistry);
        var tenant = new AlwaysTenantAccess();

        var onboard = await new TenantsController(db.Context, tenant, instance)
            .Create(new CreateTenantRequest("tenant-id", "Tenant", null), CancellationToken.None);
        var contract = await new ContractsController(db.Context, tenant, instance)
            .Create(new CreateContractRequest("Baseline", null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(onboard.Result);
        Assert.IsType<ForbidResult>(contract.Result);
    }

    private static CreateAppTemplateRequest NewTemplateRequest() => new(
        "App", null, null, "install", "uninstall", null, null, null);

    private sealed class PermissionAccess : IInstanceAccessService
    {
        private readonly InstancePermission _allowed;
        public PermissionAccess(InstancePermission allowed) => _allowed = allowed;
        public Guid? CurrentUserId => null;
        public Task<InstanceRole> GetRolesAsync(CancellationToken ct) => Task.FromResult(InstanceRole.None);
        public Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) =>
            Task.FromResult(permission == _allowed);
    }

    private sealed class AlwaysTenantAccess : ITenantAccessService
    {
        public Guid? CurrentUserId => null;
        public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class InstanceAuthorizationTests
{
    [Fact]
    public void Fixed_roles_map_only_to_their_reviewed_permissions()
    {
        Assert.True(InstanceRolePermissions.Includes(InstanceRole.Administrator, InstancePermission.ManageRoles));
        Assert.True(InstanceRolePermissions.Includes(InstanceRole.Administrator, InstancePermission.ManageTenantRegistry));
        Assert.True(InstanceRolePermissions.Includes(InstanceRole.CatalogManager, InstancePermission.ManageCatalog));
        Assert.False(InstanceRolePermissions.Includes(InstanceRole.CatalogManager, InstancePermission.ManageSam));
        Assert.True(InstanceRolePermissions.Includes(InstanceRole.CredentialManager, InstancePermission.ManageSam));
        Assert.True(InstanceRolePermissions.Includes(InstanceRole.AutomationPolicyManager, InstancePermission.ManageMcpPolicy));
        Assert.False(InstanceRolePermissions.Includes(InstanceRole.AutomationPolicyManager, InstancePermission.ManageRoles));
        Assert.False(InstanceRolePermissions.IsValidAssignment(
            InstanceRole.Administrator | InstanceRole.CatalogManager));
        Assert.False(InstanceRolePermissions.IsValidAssignment((InstanceRole)128));
    }

    [Fact]
    public async Task Database_resolved_permissions_observe_demotion_despite_legacy_admin_claim()
    {
        using var db = new TestDb();
        var user = NewUser("admin@example.com", InstanceRole.Administrator);
        db.Context.AppUsers.Add(user);
        await db.Context.SaveChangesAsync();
        var http = LocalHttpContext(user.Id, legacyAdminClaim: true);
        var service = new InstanceAccessService(new HttpContextAccessor { HttpContext = http }, db.Context);

        Assert.True(await service.HasPermissionAsync(InstancePermission.ManageRoles, CancellationToken.None));

        user.InstanceRoles = InstanceRole.CatalogManager;
        await db.Context.SaveChangesAsync();

        Assert.False(await service.HasPermissionAsync(InstancePermission.ManageRoles, CancellationToken.None));
        Assert.True(await service.HasPermissionAsync(InstancePermission.ManageCatalog, CancellationToken.None));
    }

    [Fact]
    public async Task Role_replacement_is_audited_and_immediately_updates_the_target()
    {
        using var db = new TestDb();
        var admin = NewUser("admin@example.com", InstanceRole.Administrator);
        var target = NewUser("target@example.com", InstanceRole.None);
        db.Context.AppUsers.AddRange(admin, target);
        await db.Context.SaveChangesAsync();
        var access = new TestInstanceAccess(admin.Id, allowed: true);
        var controller = NewController(db, access, admin);

        var result = await controller.ReplaceRoles(target.Id,
            new InstanceAccessController.ReplaceInstanceRolesRequest(
                [InstanceRole.CatalogManager, InstanceRole.CredentialManager], target.AuthorizationVersion),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<InstanceAccessController.InstanceUserDto>(ok.Value);
        Assert.Contains(InstanceRole.CatalogManager, dto.Roles);
        Assert.Contains(InstanceRole.CredentialManager, dto.Roles);
        Assert.Equal(2, dto.AuthorizationVersion);
        var audit = Assert.Single(await db.Context.AuditEvents
            .Where(audit => audit.EventType == AuditEventType.InstanceRolesChanged).ToListAsync());
        Assert.Equal(target.Id.ToString(), audit.EntityId);
        Assert.Contains("CatalogManager", audit.Detail);
        Assert.DoesNotContain("\"after\":[2", audit.Detail);
    }

    [Fact]
    public async Task Role_replacement_rejects_self_change_stale_write_and_last_admin_removal()
    {
        using var db = new TestDb();
        var admin = NewUser("admin@example.com", InstanceRole.Administrator);
        var target = NewUser("target@example.com", InstanceRole.None);
        db.Context.AppUsers.AddRange(admin, target);
        await db.Context.SaveChangesAsync();
        var access = new TestInstanceAccess(admin.Id, allowed: true);
        var controller = NewController(db, access, admin);

        var self = await controller.ReplaceRoles(admin.Id,
            new InstanceAccessController.ReplaceInstanceRolesRequest([], admin.AuthorizationVersion),
            CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(self.Result);

        var stale = await controller.ReplaceRoles(target.Id,
            new InstanceAccessController.ReplaceInstanceRolesRequest(
                [InstanceRole.CatalogManager], target.AuthorizationVersion + 1),
            CancellationToken.None);
        var staleResult = Assert.IsType<ObjectResult>(stale.Result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, staleResult.StatusCode);

        var nonLocalController = NewController(db, new TestInstanceAccess(null, allowed: true), admin);
        var lastAdmin = await nonLocalController.ReplaceRoles(admin.Id,
            new InstanceAccessController.ReplaceInstanceRolesRequest([], admin.AuthorizationVersion),
            CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(lastAdmin.Result);
    }

    [Fact]
    public async Task Tenant_owner_does_not_receive_instance_permissions()
    {
        using var db = new TestDb();
        var owner = NewUser("owner@example.com", InstanceRole.None);
        var tenant = new Tenant { TenantId = "tenant-a", DisplayName = "Tenant A" };
        db.Context.AddRange(owner, tenant);
        db.Context.TenantAccessGrants.Add(new TenantAccessGrant
        {
            TenantId = tenant.Id,
            UserId = owner.Id,
            Role = TenantRole.Owner,
            GrantedByUserId = owner.Id
        });
        await db.Context.SaveChangesAsync();
        var service = new InstanceAccessService(
            new HttpContextAccessor { HttpContext = LocalHttpContext(owner.Id) }, db.Context);

        Assert.False(await service.HasPermissionAsync(InstancePermission.ManageCatalog, CancellationToken.None));
        Assert.False(await service.HasPermissionAsync(InstancePermission.ManageTenantRegistry, CancellationToken.None));
    }

    private static AppUser NewUser(string email, InstanceRole roles) => new()
    {
        Email = email,
        DisplayName = email,
        PasswordHash = "hash",
        InstanceRoles = roles
    };

    private static DefaultHttpContext LocalHttpContext(Guid userId, bool legacyAdminClaim = false)
    {
        var claims = new List<Claim> { new(LocalTokenService.UserIdClaim, userId.ToString()) };
        if (legacyAdminClaim) claims.Add(new Claim(LocalTokenService.SystemAdminClaim, "true"));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    private static InstanceAccessController NewController(
        TestDb db, IInstanceAccessService access, AppUser actor)
    {
        var controller = new InstanceAccessController(db.Context, access);
        controller.ControllerContext = new ControllerContext { HttpContext = LocalHttpContext(actor.Id) };
        return controller;
    }

    private sealed class TestInstanceAccess : IInstanceAccessService
    {
        private readonly bool _allowed;
        public TestInstanceAccess(Guid? currentUserId, bool allowed)
        {
            CurrentUserId = currentUserId;
            _allowed = allowed;
        }

        public Guid? CurrentUserId { get; }
        public Task<InstanceRole> GetRolesAsync(CancellationToken ct) =>
            Task.FromResult(_allowed ? InstanceRole.Administrator : InstanceRole.None);
        public Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) =>
            Task.FromResult(_allowed);
    }
}

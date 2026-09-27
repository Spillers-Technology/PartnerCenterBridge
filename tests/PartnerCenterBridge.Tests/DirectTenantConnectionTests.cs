using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Tests;

public class DirectTenantConnectionTests
{
    private sealed class Access : ITenantAccessService, IInstanceAccessService
    {
        public Guid? CurrentUserId { get; set; } = Guid.NewGuid();
        public List<Guid> Allowed { get; } = [];
        public bool CanManage { get; set; }
        public Task<bool> HasRoleAsync(Guid id, TenantRole minimum, CancellationToken ct) => Task.FromResult(Allowed.Contains(id));
        public Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>?>(Allowed);
        public Task<InstanceRole> GetRolesAsync(CancellationToken ct) => Task.FromResult(CanManage ? InstanceRole.Administrator : InstanceRole.None);
        public Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) => Task.FromResult(CanManage);
    }
    private sealed class NoSam : ISamTokenStore
    {
        public Task<string?> GetRefreshTokenAsync(CancellationToken ct = default) => throw new Exception("Must not fall back to another identity.");
        public Task SaveRefreshTokenAsync(string refreshToken, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private readonly EphemeralDataProtectionProvider protection = new();
    private DirectTenantConnection Create(TestDb db, Access access, bool local = true, bool loopback = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MicrosoftSignIn:ClientId"] = "11111111-1111-1111-1111-111111111111" }).Build();
        var hosting = new HostingInfo(local ? HostingProfile.Local : HostingProfile.Server,
            local ? new LocalWorkbenchOptions { DataRoot = Path.GetTempPath(), ListenAddress = loopback ? IPAddress.Loopback : IPAddress.Any } : null);
        var sam = new SamTokenService(new NoSam(), Options.Create(new PartnerOptions { ClientId = "configured", ClientSecret = "configured" }), NullLogger<SamTokenService>.Instance);
        return new(db.Context, protection, config, hosting, access, sam, access);
    }
    private void Seed(TestDb db, Guid userId, Tenant tenant, DirectConnectionState state)
    {
        db.Context.Secrets.Add(new SecretRecord
        {
            Name = $"direct:{userId}:{tenant.TenantId}",
            ProtectedValue = protection.CreateProtector("PartnerCenterBridge.DirectTenant.v1").Protect(JsonSerializer.Serialize(state))
        });
    }

    [Fact]
    public async Task Connection_list_is_isolated_by_operator_and_tenant_grants()
    {
        using var db = new TestDb();
        var access = new Access();
        var visible = new Tenant { TenantId = Guid.NewGuid().ToString(), DisplayName = "Visible" };
        var hidden = new Tenant { TenantId = Guid.NewGuid().ToString(), DisplayName = "Hidden" };
        db.Context.Tenants.AddRange(visible, hidden);
        access.Allowed.Add(visible.Id);
        Seed(db, access.CurrentUserId!.Value, visible, new("account", "my-admin@example.com", "secret-cache"));
        Seed(db, Guid.NewGuid(), visible, new("other", "other-admin@example.com", "other-cache"));
        Seed(db, access.CurrentUserId.Value, hidden, new("hidden", "hidden-admin@example.com", "hidden-cache"));
        await db.Context.SaveChangesAsync();
        var item = Assert.Single(await Create(db, access).ListAsync(default));
        Assert.Equal("my-admin@example.com", item.Username);
        Assert.DoesNotContain("cache", JsonSerializer.Serialize(item));
        Assert.All(db.Context.Secrets, record => Assert.DoesNotContain("admin@example.com", record.ProtectedValue));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Interactive_sign_in_is_disabled_on_server_and_network_listeners(bool local, bool loopback)
    {
        using var db = new TestDb();
        var service = Create(db, new Access(), local, loopback);
        Assert.False(service.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(null, default));
    }

    [Fact]
    public async Task New_tenant_requires_registry_permission_and_reconnect_requires_tenant_access()
    {
        using var db = new TestDb();
        var access = new Access();
        var controller = new MicrosoftConnectionsController(Create(db, access), access, access, db.Context);
        Assert.IsType<ForbidResult>(await controller.Connect(new(null), default));
        access.CanManage = true;
        Assert.IsType<ForbidResult>(await controller.Connect(new(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task Direct_tenant_never_falls_back_to_partner_credentials_for_another_operator()
    {
        using var db = new TestDb();
        var tenantId = Guid.NewGuid().ToString();
        db.Context.Secrets.Add(new SecretRecord { Name = $"direct-mode:{tenantId}", ProtectedValue = "marker" });
        await db.Context.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(db, new Access()).GetAccessTokenAsync(tenantId, Resources.Graph));
        Assert.Contains("Connect your Microsoft admin account", error.Message);
    }

    [Fact]
    public async Task Revoked_tenant_grant_prevents_use_of_previously_saved_credentials()
    {
        using var db = new TestDb();
        var access = new Access();
        var tenant = new Tenant { TenantId = Guid.NewGuid().ToString(), DisplayName = "Revoked" };
        db.Context.Tenants.Add(tenant);
        Seed(db, access.CurrentUserId!.Value, tenant, new("account", "admin@example.com", "not-loaded"));
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create(db, access).GetAccessTokenAsync(tenant.TenantId, Resources.Graph));
    }

    [Fact]
    public async Task Missing_cached_account_marks_only_its_connection_for_reconnect()
    {
        using var db = new TestDb();
        var access = new Access();
        var tenant = new Tenant { TenantId = Guid.NewGuid().ToString(), DisplayName = "Needs sign-in" };
        db.Context.Tenants.Add(tenant);
        access.Allowed.Add(tenant.Id);
        // Valid empty MSAL cache: silent acquisition cannot find this account and must not open a browser.
        var cache = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{}"));
        Seed(db, access.CurrentUserId!.Value, tenant, new("missing.account", "admin@example.com", cache));
        await db.Context.SaveChangesAsync();
        var service = Create(db, access);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccessTokenAsync(tenant.TenantId, Resources.Graph));
        Assert.Contains("admin@example.com", error.Message);
        Assert.True(Assert.Single(await service.ListAsync(default)).ReconnectRequired);
    }
}

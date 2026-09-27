using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Graph;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// The Exchange organization is read from Microsoft Graph for the tenant id -- never taken from the
/// supplied/imported DefaultDomain -- and persisted on the tenant.
/// </summary>
public class ExchangeOrganizationTests : IDisposable
{
    private const string TenantGuid = "11111111-2222-3333-4444-555555555555";
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private GraphExchangeOrganizationResolver Resolver() => new(
        new TenantGraphRest(new FakeTokenProvider(), new SingleHttpClientFactory(),
            Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! })));

    private void StubOrganization(string id, params (string Name, bool Initial)[] domains) =>
        _server.Given(Request.Create().WithPath("/organization").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new[]
                {
                    new
                    {
                        id,
                        verifiedDomains = domains.Select(d => new { name = d.Name, isInitial = d.Initial, isDefault = !d.Initial }).ToArray()
                    }
                }
            }));

    [Fact]
    public async Task Resolver_returns_the_initial_onmicrosoft_domain_from_graph()
    {
        StubOrganization(TenantGuid, ("contoso.com", false), ("contoso.onmicrosoft.com", true), ("contoso.mail.onmicrosoft.com", false));

        Assert.Equal("contoso.onmicrosoft.com", await Resolver().ResolveAsync(TenantGuid));
    }

    [Fact]
    public async Task Resolver_refuses_an_organization_object_for_another_tenant()
    {
        StubOrganization("99999999-2222-3333-4444-555555555555", ("fabrikam.onmicrosoft.com", true));

        var ex = await Assert.ThrowsAsync<ExchangeOrganizationException>(() => Resolver().ResolveAsync(TenantGuid));
        Assert.Contains("refusing", ex.Message);
    }

    [Fact]
    public async Task Resolver_fails_clearly_without_an_initial_domain()
    {
        StubOrganization(TenantGuid, ("contoso.com", false));

        var ex = await Assert.ThrowsAsync<ExchangeOrganizationException>(() => Resolver().ResolveAsync(TenantGuid));
        Assert.Contains("no verified initial onmicrosoft.com domain", ex.Message);
    }

    [Fact]
    public async Task Resolver_wraps_a_graph_failure()
    {
        _server.Given(Request.Create().WithPath("/organization").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("{\"error\":{\"code\":\"Authorization_RequestDenied\"}}"));

        var ex = await Assert.ThrowsAsync<ExchangeOrganizationException>(() => Resolver().ResolveAsync(TenantGuid));
        Assert.Contains("Authorization_RequestDenied", ex.Message);
    }

    [Fact]
    public async Task Provider_resolves_lazily_ignores_default_domain_and_persists()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = TenantGuid, DisplayName = "Contoso", DefaultDomain = "fabrikam.com" };
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        StubOrganization(TenantGuid, ("contoso.onmicrosoft.com", true));
        var resolver = new CountingResolver(Resolver());
        var provider = Provider(db, resolver);

        // Detached copy, as a caller that read the tenant with AsNoTracking would hold it.
        var detached = await db.CreateContext().Tenants.AsNoTracking().SingleAsync();
        Assert.Equal("contoso.onmicrosoft.com", await provider.GetOrganizationAsync(detached));
        Assert.Equal("contoso.onmicrosoft.com", await provider.GetOrganizationAsync(detached));
        Assert.Equal(1, resolver.Calls);

        using var fresh = db.CreateContext();
        var stored = await fresh.Tenants.SingleAsync();
        Assert.Equal("contoso.onmicrosoft.com", stored.ExchangeOrganization);
        Assert.NotNull(stored.ExchangeOrganizationVerifiedAt);
        Assert.Equal("fabrikam.com", stored.DefaultDomain);
    }

    [Fact]
    public async Task Provider_uses_the_persisted_value_without_calling_graph()
    {
        using var db = new TestDb();
        var resolver = new CountingResolver(new ThrowingResolver());
        var tenant = new Tenant { TenantId = TenantGuid, DisplayName = "Contoso", ExchangeOrganization = "contoso.onmicrosoft.com" };

        Assert.Equal("contoso.onmicrosoft.com", await Provider(db, resolver).GetOrganizationAsync(tenant));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Provider_surfaces_a_clear_error_when_graph_cannot_resolve()
    {
        using var db = new TestDb();
        var tenant = new Tenant { TenantId = TenantGuid, DisplayName = "Contoso", DefaultDomain = "contoso.com" };

        var ex = await Assert.ThrowsAsync<ExchangeOrganizationException>(
            () => Provider(db, new ThrowingResolver()).GetOrganizationAsync(tenant));
        Assert.Contains("Graph", ex.Message);
        Assert.Null(tenant.ExchangeOrganization);
    }

    [Fact]
    public async Task Tenant_add_resolves_the_exchange_organization_from_graph_not_the_supplied_domain()
    {
        using var db = new TestDb();
        StubOrganization(TenantGuid, ("contoso.onmicrosoft.com", true));
        var controller = new TenantsController(db.Context, new AllowAll(), new AllowAll(), Resolver());

        var result = await controller.Create(new CreateTenantRequest(TenantGuid, "Contoso", "fabrikam.onmicrosoft.com"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = await db.CreateContext().Tenants.SingleAsync();
        Assert.Equal("contoso.onmicrosoft.com", stored.ExchangeOrganization);
        Assert.Equal("fabrikam.onmicrosoft.com", stored.DefaultDomain);
    }

    [Fact]
    public async Task Tenant_add_still_succeeds_when_graph_is_not_reachable_yet()
    {
        using var db = new TestDb();
        var controller = new TenantsController(db.Context, new AllowAll(), new AllowAll(), new ThrowingResolver());

        var result = await controller.Create(new CreateTenantRequest(TenantGuid, "Contoso", "contoso.com"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null((await db.CreateContext().Tenants.SingleAsync()).ExchangeOrganization);
    }

    private static TenantExchangeOrganizationProvider Provider(TestDb db, IExchangeOrganizationResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db.CreateContext());
        return new TenantExchangeOrganizationProvider(resolver, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private sealed class CountingResolver(IExchangeOrganizationResolver inner) : IExchangeOrganizationResolver
    {
        public int Calls { get; private set; }
        public Task<string> ResolveAsync(string tenantId, CancellationToken ct = default)
        {
            Calls++;
            return inner.ResolveAsync(tenantId, ct);
        }
    }

    private sealed class ThrowingResolver : IExchangeOrganizationResolver
    {
        public Task<string> ResolveAsync(string tenantId, CancellationToken ct = default) =>
            throw new ExchangeOrganizationException($"Could not read the organization of tenant {tenantId} from Microsoft Graph: no SAM token.");
    }

    private sealed class AllowAll : IInstanceAccessService, ITenantAccessService
    {
        public Guid? CurrentUserId => null;
        public Task<InstanceRole> GetRolesAsync(CancellationToken ct) => Task.FromResult(InstanceRole.None);
        public Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }
}

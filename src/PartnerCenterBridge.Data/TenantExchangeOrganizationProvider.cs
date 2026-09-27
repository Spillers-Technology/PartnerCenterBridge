using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Data;

/// <summary>
/// Returns a tenant's verified Exchange organization, resolving it from Microsoft Graph on first
/// use when tenant add/sync could not. The lazily resolved value is written through a separate
/// DbContext scope so that persisting it never flushes a caller's unrelated pending changes.
/// <see cref="Tenant.DefaultDomain"/> is deliberately never consulted.
/// </summary>
public sealed class TenantExchangeOrganizationProvider : ITenantExchangeOrganizationProvider
{
    private readonly IExchangeOrganizationResolver _resolver;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public TenantExchangeOrganizationProvider(IExchangeOrganizationResolver resolver, IServiceScopeFactory scopes)
        : this(resolver, scopes, TimeProvider.System) { }

    public TenantExchangeOrganizationProvider(IExchangeOrganizationResolver resolver, IServiceScopeFactory scopes, TimeProvider time)
    {
        _resolver = resolver;
        _scopes = scopes;
        _time = time;
    }

    public async Task<string> GetOrganizationAsync(Tenant tenant, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(tenant.ExchangeOrganization)) return tenant.ExchangeOrganization;

        var organization = await _resolver.ResolveAsync(tenant.TenantId, ct);
        var now = _time.GetUtcNow();
        tenant.ExchangeOrganization = organization;
        tenant.ExchangeOrganizationVerifiedAt = now;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var row = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenant.Id, ct);
        if (row is not null && string.Equals(row.TenantId, tenant.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            row.ExchangeOrganization = organization;
            row.ExchangeOrganizationVerifiedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return organization;
    }
}

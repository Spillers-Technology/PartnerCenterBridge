using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Exchange.TenantAudits;
using PartnerCenterBridge.Graph.TenantAudits;

namespace PartnerCenterBridge.Api.TenantAudits;

/// <summary>
/// Wires the tenant audit engine: data providers, the catalog, and every check. Checks are found
/// by type in the Core and API assemblies, so adding one is adding a class -- the
/// TenantAuditCatalogTests suite guards ids, metadata and registration.
/// </summary>
public static class TenantAuditRegistration
{
    public static IServiceCollection AddTenantAudits(this IServiceCollection services)
    {
        services.AddGraphTenantAuditData();
        services.AddExchangeTenantAuditData();
        foreach (var type in CheckTypes())
            services.AddScoped(typeof(ITenantAuditCheck), type);
        services.AddScoped<TenantAuditCatalog>();
        services.AddScoped<TenantAuditService>();
        return services;
    }

    /// <summary>Concrete <see cref="ITenantAuditCheck"/> types in the assemblies that hold checks.</summary>
    public static IReadOnlyList<Type> CheckTypes() =>
        new[] { typeof(ITenantAuditCheck).Assembly, typeof(TenantAuditRegistration).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantAuditCheck).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartnerCenterBridge.Core.TenantAudits;

namespace PartnerCenterBridge.Graph.TenantAudits;

/// <summary>Registers the Graph-backed tenant audit data providers (kept here so their types stay internal).</summary>
public static class TenantAuditGraphRegistration
{
    public static IServiceCollection AddGraphTenantAuditData(this IServiceCollection services)
    {
        services.TryAddScoped<TenantGraphRest>();
        services.AddScoped<IAuditDirectoryData, GraphAuditDirectoryData>();
        services.AddScoped<IAuditDeviceData, GraphAuditDeviceData>();
        services.AddScoped<IAuditSecurityData, GraphAuditSecurityData>();
        return services;
    }
}

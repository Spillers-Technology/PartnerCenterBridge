using Microsoft.Extensions.DependencyInjection;
using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.Graph.ConfigSections;

/// <summary>Registers the Graph-backed config sections. TenantGraphRest is already registered by AddGraphWorkflows(); call both together.</summary>
public static class ConfigSectionRegistration
{
    public static IServiceCollection AddGraphConfigSections(this IServiceCollection services)
    {
        services.AddScoped<IConfigSection, ConditionalAccessPoliciesSection>();
        services.AddScoped<IConfigSection, NamedLocationsSection>();
        services.AddScoped<IConfigSection, DeviceCompliancePoliciesSection>();
        return services;
    }
}

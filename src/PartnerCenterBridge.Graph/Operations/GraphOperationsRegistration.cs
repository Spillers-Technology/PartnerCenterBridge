using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Operations;

/// <summary>Registers the Graph-backed planned operations and person-workspace reader.</summary>
public static class GraphOperationsRegistration
{
    public static IServiceCollection AddGraphOperations(this IServiceCollection services)
    {
        services.TryAddScoped<TenantGraphRest>();
        // Access Parity is an IWorkflow too, so it appears in the catalog (and the MCP queue) uniformly.
        services.AddScoped<IWorkflow, AccessParityOperation>();
        services.AddScoped<IPersonDirectoryReader, PersonDirectoryReader>();
        services.AddScoped<IOffboardingService, OffboardingOperation>();
        return services;
    }
}

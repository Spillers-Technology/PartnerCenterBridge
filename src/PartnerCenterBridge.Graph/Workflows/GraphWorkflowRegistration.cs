using Microsoft.Extensions.DependencyInjection;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Graph.Workflows;

/// <summary>Registers the Graph-backed workflows (kept here so their internal types stay internal).</summary>
public static class GraphWorkflowRegistration
{
    public static IServiceCollection AddGraphWorkflows(this IServiceCollection services)
    {
        services.AddScoped<TenantGraphRest>();
        services.AddScoped<IWorkflow, LicenseRepairWorkflow>();
        services.AddScoped<IWorkflow, MfaResetWorkflow>();
        return services;
    }
}

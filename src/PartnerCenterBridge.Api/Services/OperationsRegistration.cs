using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Exchange;
using PartnerCenterBridge.Graph.Operations;

namespace PartnerCenterBridge.Api.Services;

/// <summary>
/// Ops workbench services: planned operations (Access Parity), the person-workspace reader,
/// policy-driven offboarding and the Exchange capability probe. One call from Program.cs.
/// </summary>
public static class OperationsRegistration
{
    public static IServiceCollection AddOperations(this IServiceCollection services)
    {
        services.AddGraphOperations();
        services.AddSingleton<IExchangeCapability, ExchangeCapability>();
        services.AddScoped<OperationRunRecorder>();
        return services;
    }
}

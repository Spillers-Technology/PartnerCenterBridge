using Microsoft.Extensions.DependencyInjection;
using PartnerCenterBridge.Core.Workflows;

namespace PartnerCenterBridge.Exchange.Workflows;

/// <summary>Registers the Exchange-backed workflows (kept here so their internal types stay internal).</summary>
public static class ExchangeWorkflowRegistration
{
    public static IServiceCollection AddExchangeWorkflows(this IServiceCollection services)
    {
        services.AddScoped<IWorkflow, MailboxArchiveWorkflow>();
        return services;
    }
}

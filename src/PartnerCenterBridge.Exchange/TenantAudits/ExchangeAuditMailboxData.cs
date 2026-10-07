using Microsoft.Extensions.DependencyInjection;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.TenantAudits;

namespace PartnerCenterBridge.Exchange.TenantAudits;

/// <summary>
/// Exchange Online audit data, read once per tenant per run. Availability uses the same
/// <see cref="IExchangeCapability"/> probe as every other Exchange feature, so an instance without
/// Exchange set up reports the Exchange checks as unavailable with the same explanation the rest of
/// the app gives.
/// </summary>
public sealed class ExchangeAuditMailboxData(IExchangeCapability capability, IExchangeMailboxAuditReader reader) : IAuditMailboxData
{
    private static readonly string[] Missing =
        ["Exchange Online app-only access (Exchange:AppId, certificate, PowerShell 7, ExchangeOnlineManagement)"];

    public string? UnavailableReason
    {
        get
        {
            var status = capability.Check();
            return status.Available ? null : status.MissingDependency ?? "Exchange Online is not configured on this PCB instance.";
        }
    }

    public Task<AuditMailboxReport> GetMailboxReportAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("exchange:mailboxes", async () =>
        {
            if (UnavailableReason is { } reason) throw new AuditUnavailableException(reason, Missing);
            try
            {
                return await reader.GetMailboxAuditReportAsync(ctx.Tenant, ctx.CancellationToken);
            }
            catch (ExchangeOrganizationException ex)
            {
                throw new AuditUnavailableException($"The tenant's Exchange Online organization could not be determined: {ex.Message}",
                    ["Graph permission Organization.Read.All (to find the tenant's initial domain)"]);
            }
            catch (InvalidOperationException ex) when (IsAccessProblem(ex.Message))
            {
                throw new AuditUnavailableException($"Exchange Online refused PCB's app-only connection: {ex.Message}", Missing);
            }
        });

    /// <summary>Connect/auth/role failures reported by the script, as opposed to throttling or outages.</summary>
    private static bool IsAccessProblem(string message) =>
        message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
        || message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not recognized as a name of a cmdlet", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ExchangeOnlineManagement", StringComparison.OrdinalIgnoreCase);
}

public static class TenantAuditExchangeRegistration
{
    public static IServiceCollection AddExchangeTenantAuditData(this IServiceCollection services)
    {
        services.AddScoped<IExchangeMailboxAuditReader, ExchangeOnlineService>();
        services.AddScoped<IAuditMailboxData, ExchangeAuditMailboxData>();
        return services;
    }
}

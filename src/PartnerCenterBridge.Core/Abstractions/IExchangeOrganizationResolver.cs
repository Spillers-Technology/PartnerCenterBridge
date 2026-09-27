using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>
/// Reads a tenant's Exchange Online organization (its initial <c>*.onmicrosoft.com</c> domain)
/// from Microsoft Graph, using a token issued for that tenant id. The answer comes from Microsoft,
/// never from a display or imported domain, so an Exchange connection is bound to the Entra tenant.
/// </summary>
public interface IExchangeOrganizationResolver
{
    /// <summary>
    /// Returns the initial domain of <paramref name="tenantId"/>. Throws
    /// <see cref="ExchangeOrganizationException"/> when Graph does not return exactly that tenant or
    /// the tenant has no initial <c>onmicrosoft.com</c> domain.
    /// </summary>
    Task<string> ResolveAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Supplies the verified Exchange organization for a tenant: the persisted value when present,
/// otherwise resolved from Graph on first use and persisted.
/// </summary>
public interface ITenantExchangeOrganizationProvider
{
    Task<string> GetOrganizationAsync(Tenant tenant, CancellationToken ct = default);
}

/// <summary>The Exchange organization for a tenant could not be established from Microsoft Graph.</summary>
public sealed class ExchangeOrganizationException : InvalidOperationException
{
    public ExchangeOrganizationException(string message, Exception? inner = null) : base(message, inner) { }
}

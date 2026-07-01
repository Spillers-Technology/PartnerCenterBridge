namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>
/// Produces a Graph client scoped to a specific customer tenant by exchanging the stored SAM
/// refresh token for a per-tenant access token. Returns <c>object</c> to keep Core free of a
/// hard dependency on the Graph SDK; callers in the Graph project cast to GraphServiceClient.
/// </summary>
public interface IGraphTenantClientFactory
{
    /// <summary>Get a Graph client (a <c>GraphServiceClient</c>) authorized against <paramref name="tenantId"/>.</summary>
    Task<object> CreateForTenantAsync(string tenantId, CancellationToken ct = default);
}

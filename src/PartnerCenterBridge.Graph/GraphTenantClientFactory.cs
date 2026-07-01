using Microsoft.Kiota.Abstractions.Authentication;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.PartnerCenter;
using GraphBeta = Microsoft.Graph.Beta;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Builds a Graph (beta) client bound to one customer tenant. Each call resolves a per-tenant
/// access token via the SAM flow, so a single bridge instance can act across every delegated tenant.
/// </summary>
public class GraphTenantClientFactory : IGraphTenantClientFactory
{
    private readonly ITokenProvider _tokens;

    public GraphTenantClientFactory(ITokenProvider tokens) => _tokens = tokens;

    public Task<object> CreateForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var auth = new BaseBearerTokenAuthenticationProvider(new TenantAccessTokenProvider(_tokens, tenantId));
        var client = new GraphBeta.GraphServiceClient(auth);
        return Task.FromResult<object>(client);
    }

    /// <summary>Adapts our <see cref="ITokenProvider"/> to Kiota's token provider for a fixed tenant.</summary>
    private sealed class TenantAccessTokenProvider : IAccessTokenProvider
    {
        private readonly ITokenProvider _tokens;
        private readonly string _tenantId;

        public TenantAccessTokenProvider(ITokenProvider tokens, string tenantId)
        {
            _tokens = tokens;
            _tenantId = tenantId;
        }

        public AllowedHostsValidator AllowedHostsValidator { get; } =
            new(new[] { "graph.microsoft.com" });

        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
            => _tokens.GetAccessTokenAsync(_tenantId, Resources.Graph, cancellationToken);
    }
}

using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Graph;

/// <summary>Builds a per-tenant <see cref="GraphRestClient"/> — shared by the Graph-based workflows.</summary>
internal sealed class TenantGraphRest
{
    private readonly ITokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _baseUrl;

    public TenantGraphRest(ITokenProvider tokens, IHttpClientFactory httpFactory, IOptions<IntuneOptions> options)
    {
        _tokens = tokens;
        _httpFactory = httpFactory;
        _baseUrl = options.Value.GraphBetaBaseUrl;
    }

    public async Task<GraphRestClient> CreateAsync(Tenant tenant, CancellationToken ct)
    {
        var token = await _tokens.GetAccessTokenAsync(tenant.TenantId, Resources.Graph, ct);
        return new GraphRestClient(_httpFactory.CreateClient("graph"), token, _baseUrl);
    }
}

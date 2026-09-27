using System.Text.Json;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Resolves a tenant's Exchange Online organization from Graph <c>GET /organization</c>, called
/// with a token issued for that tenant id. The organization object must carry the same tenant id,
/// and the answer is its verified initial <c>*.onmicrosoft.com</c> domain -- the value Microsoft
/// directs app-only callers to pass as <c>-Organization</c>.
/// </summary>
public sealed class GraphExchangeOrganizationResolver : IExchangeOrganizationResolver
{
    private readonly TenantGraphRest _graph;

    internal GraphExchangeOrganizationResolver(TenantGraphRest graph) => _graph = graph;

    public GraphExchangeOrganizationResolver(
        PartnerCenter.ITokenProvider tokens, IHttpClientFactory httpFactory, Microsoft.Extensions.Options.IOptions<IntuneOptions> options)
        : this(new TenantGraphRest(tokens, httpFactory, options)) { }

    public async Task<string> ResolveAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ExchangeOrganizationException("The tenant has no Entra tenant id to resolve an Exchange organization for.");

        List<JsonElement> orgs;
        try
        {
            var graph = await _graph.CreateAsync(new Tenant { TenantId = tenantId, DisplayName = tenantId }, ct);
            orgs = await graph.GetAllAsync("/organization?$select=id,verifiedDomains", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ExchangeOrganizationException)
        {
            throw new ExchangeOrganizationException(
                $"Could not read the organization of tenant {tenantId} from Microsoft Graph: {e.Message}", e);
        }

        if (orgs.Count != 1)
            throw new ExchangeOrganizationException(
                $"Microsoft Graph returned {orgs.Count} organizations for tenant {tenantId}; expected exactly one.");
        var org = orgs[0];
        var id = org.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (!SameTenant(id, tenantId))
            throw new ExchangeOrganizationException(
                $"Microsoft Graph answered for tenant '{id}' when asked about tenant {tenantId}; refusing to use it for Exchange Online.");

        if (org.TryGetProperty("verifiedDomains", out var domains) && domains.ValueKind == JsonValueKind.Array)
            foreach (var d in domains.EnumerateArray())
            {
                var initial = d.TryGetProperty("isInitial", out var i) && i.ValueKind == JsonValueKind.True;
                var name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (initial && !string.IsNullOrWhiteSpace(name)
                    && name.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
                    return name.Trim().ToLowerInvariant();
            }

        throw new ExchangeOrganizationException(
            $"Tenant {tenantId} has no verified initial onmicrosoft.com domain in Microsoft Graph.");
    }

    private static bool SameTenant(string? a, string b) =>
        Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb)
            ? ga == gb
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

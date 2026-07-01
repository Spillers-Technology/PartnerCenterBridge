using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PartnerCenterBridge.PartnerCenter;

/// <summary>A customer tenant as returned by the Partner Center customer list.</summary>
public record CustomerSummary(string TenantId, string CompanyName, string? Domain);

/// <summary>
/// Thin client over the Partner Center REST v3 API (audience
/// <c>https://api.partnercenter.microsoft.com</c>). Used only for partner-specific data such as
/// the customer list; all in-tenant operations go through Microsoft Graph instead.
/// </summary>
public class PartnerCenterClient
{
    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly PartnerOptions _opts;

    public PartnerCenterClient(HttpClient http, ITokenProvider tokens, IOptions<PartnerOptions> opts)
    {
        _http = http;
        _tokens = tokens;
        _opts = opts.Value;
        _http.BaseAddress ??= new Uri("https://api.partnercenter.microsoft.com");
    }

    /// <summary>Enumerate the CSP customers under the partner tenant.</summary>
    public async Task<IReadOnlyList<CustomerSummary>> ListCustomersAsync(CancellationToken ct = default)
    {
        var token = await _tokens.GetAccessTokenAsync(_opts.PartnerTenantId, Resources.PartnerCenter, ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/customers");
        req.Headers.Authorization = new("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<CustomerPage>(cancellationToken: ct);
        return page?.Items?.Select(i => new CustomerSummary(
                   i.Id ?? string.Empty,
                   i.CompanyProfile?.CompanyName ?? "(unknown)",
                   i.CompanyProfile?.Domain))
               .Where(c => !string.IsNullOrEmpty(c.TenantId))
               .ToList()
               ?? new List<CustomerSummary>();
    }

    private sealed class CustomerPage
    {
        [JsonPropertyName("items")] public List<CustomerItem>? Items { get; set; }
    }

    private sealed class CustomerItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("companyProfile")] public CompanyProfile? CompanyProfile { get; set; }
    }

    private sealed class CompanyProfile
    {
        [JsonPropertyName("companyName")] public string? CompanyName { get; set; }
        [JsonPropertyName("domain")] public string? Domain { get; set; }
    }
}

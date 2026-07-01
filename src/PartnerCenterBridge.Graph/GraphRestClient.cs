using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PartnerCenterBridge.Graph;

/// <summary>
/// Minimal typed helper for the handful of Graph beta calls the Win32 upload flow needs. Using
/// raw REST (rather than the generated SDK fluent surface) keeps the request bodies explicit and
/// insulated from beta SDK churn.
/// </summary>
internal sealed class GraphRestClient
{
    public const string DefaultBeta = "https://graph.microsoft.com/beta";

    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string _baseUrl;

    public GraphRestClient(HttpClient http, string accessToken, string? baseUrl = null)
    {
        _http = http;
        _accessToken = accessToken;
        _baseUrl = (baseUrl ?? DefaultBeta).TrimEnd('/');
    }

    private HttpRequestMessage New(HttpMethod method, string url, object? body)
    {
        var req = new HttpRequestMessage(method, url.StartsWith("http") ? url : $"{_baseUrl}{url}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: JsonOpts);
        return req;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public Task<JsonDocument> PostAsync(string url, object body, CancellationToken ct) => SendAsync(New(HttpMethod.Post, url, body), ct);
    public Task<JsonDocument> GetAsync(string url, CancellationToken ct) => SendAsync(New(HttpMethod.Get, url, null), ct);
    public Task<JsonDocument> PatchAsync(string url, object body, CancellationToken ct) => SendAsync(New(HttpMethod.Patch, url, body), ct);

    private async Task<JsonDocument> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new GraphRequestException(req.Method, req.RequestUri!, resp.StatusCode, content);
        return string.IsNullOrWhiteSpace(content)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(content);
    }
}

/// <summary>Raised when a Graph call returns a non-success status; carries the response body for diagnosis.</summary>
public sealed class GraphRequestException(HttpMethod method, Uri uri, System.Net.HttpStatusCode status, string body)
    : Exception($"Graph {method} {uri} failed with {(int)status}: {body}")
{
    public System.Net.HttpStatusCode Status { get; } = status;
}

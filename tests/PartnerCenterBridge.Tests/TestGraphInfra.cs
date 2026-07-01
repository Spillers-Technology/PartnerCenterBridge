using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Tests;

/// <summary>Shared test doubles for exercising Graph services against a WireMock server.</summary>
internal sealed class FakeTokenProvider : ITokenProvider
{
    public Task<string> GetAccessTokenAsync(string tenantId, string resource, CancellationToken ct = default)
        => Task.FromResult("test-token");
}

internal sealed class SingleHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client = new();
    public HttpClient CreateClient(string name) => _client;
}

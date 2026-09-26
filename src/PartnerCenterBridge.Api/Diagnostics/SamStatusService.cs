using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Api.Diagnostics;

/// <summary>Secure Application Model readiness.</summary>
/// <param name="AppConfigured">Partner:ClientId, ClientSecret and PartnerTenantId are all set.</param>
/// <param name="Bootstrapped">An encrypted refresh token is stored (bootstrap-sam or /api/admin/sam/seed ran).</param>
/// <param name="SeedConfigured">Partner:SeedRefreshToken is set (used when nothing is stored yet).</param>
public sealed record SamStatus(bool AppConfigured, bool Bootstrapped, bool SeedConfigured)
{
    public bool Ready => AppConfigured && (Bootstrapped || SeedConfigured);
}

public interface ISamStatusService
{
    Task<SamStatus> GetAsync(CancellationToken ct);
}

/// <summary>Shared by <c>GET /api/admin/sam/status</c> and the system diagnostics.</summary>
public sealed class SamStatusService : ISamStatusService
{
    private readonly ISamTokenStore _store;
    private readonly PartnerOptions _options;

    public SamStatusService(ISamTokenStore store, IOptions<PartnerOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task<SamStatus> GetAsync(CancellationToken ct) => new(
        !string.IsNullOrWhiteSpace(_options.ClientId)
            && !string.IsNullOrWhiteSpace(_options.ClientSecret)
            && !string.IsNullOrWhiteSpace(_options.PartnerTenantId),
        await _store.GetRefreshTokenAsync(ct) is not null,
        !string.IsNullOrWhiteSpace(_options.SeedRefreshToken));
}

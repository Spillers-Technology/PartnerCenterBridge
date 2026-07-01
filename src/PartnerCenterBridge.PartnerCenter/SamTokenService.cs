using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.PartnerCenter;

/// <summary>Well-known resource audiences the bridge requests tokens for.</summary>
public static class Resources
{
    public const string Graph = "https://graph.microsoft.com";
    public const string PartnerCenter = "https://api.partnercenter.microsoft.com";
}

/// <summary>Acquires per-tenant access tokens under the Secure Application Model.</summary>
public interface ITokenProvider
{
    /// <summary>
    /// Get an access token for <paramref name="resource"/> scoped to <paramref name="tenantId"/>,
    /// using the stored SAM refresh token. Rotates and persists the refresh token as a side effect.
    /// </summary>
    Task<string> GetAccessTokenAsync(string tenantId, string resource, CancellationToken ct = default);
}

/// <summary>
/// MSAL-backed implementation of the SAM refresh-token flow. MSAL hides refresh tokens by design,
/// so we hook the token cache to capture the rotated refresh token after each acquisition and
/// persist it via <see cref="ISamTokenStore"/> — keeping it fresh well inside the 90-day window.
/// </summary>
public class SamTokenService : ITokenProvider
{
    private readonly ISamTokenStore _store;
    private readonly PartnerOptions _opts;
    private readonly ILogger<SamTokenService> _log;

    public SamTokenService(ISamTokenStore store, IOptions<PartnerOptions> opts, ILogger<SamTokenService> log)
    {
        _store = store;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<string> GetAccessTokenAsync(string tenantId, string resource, CancellationToken ct = default)
    {
        var refreshToken = await _store.GetRefreshTokenAsync(ct) ?? _opts.SeedRefreshToken
            ?? throw new InvalidOperationException(
                "Secure Application Model is not bootstrapped: no SAM refresh token is available. " +
                "Run the interactive MFA bootstrap first.");

        string? capturedCache = null;
        var app = ConfidentialClientApplicationBuilder.Create(_opts.ClientId)
            .WithClientSecret(_opts.ClientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();

        // Capture the serialized cache so we can extract the rotated refresh token afterwards.
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                capturedCache = Encoding.UTF8.GetString(args.TokenCache.SerializeMsalV3());
        });

        var scopes = new[] { $"{resource}/.default" };
        var result = await ((IByRefreshToken)app)
            .AcquireTokenByRefreshToken(scopes, refreshToken)
            .ExecuteAsync(ct);

        var rotated = ExtractRefreshToken(capturedCache);
        if (rotated is not null && rotated != refreshToken)
        {
            await _store.SaveRefreshTokenAsync(rotated, ct);
            _log.LogDebug("Rotated SAM refresh token after acquiring a token for tenant {Tenant}", tenantId);
        }

        return result.AccessToken;
    }

    /// <summary>Pull the newest refresh token secret out of an MSAL v3 cache blob.</summary>
    internal static string? ExtractRefreshToken(string? msalV3Cache)
    {
        if (string.IsNullOrEmpty(msalV3Cache))
            return null;

        using var doc = JsonDocument.Parse(msalV3Cache);
        if (!doc.RootElement.TryGetProperty("RefreshToken", out var rts) ||
            rts.ValueKind != JsonValueKind.Object)
            return null;

        string? newest = null;
        DateTimeOffset newestTime = DateTimeOffset.MinValue;
        foreach (var entry in rts.EnumerateObject())
        {
            var val = entry.Value;
            if (!val.TryGetProperty("secret", out var secret)) continue;

            var time = DateTimeOffset.MinValue;
            if (val.TryGetProperty("last_modification_time", out var lmt) &&
                long.TryParse(lmt.GetString(), out var unix))
                time = DateTimeOffset.FromUnixTimeSeconds(unix);

            if (newest is null || time >= newestTime)
            {
                newest = secret.GetString();
                newestTime = time;
            }
        }
        return newest;
    }
}

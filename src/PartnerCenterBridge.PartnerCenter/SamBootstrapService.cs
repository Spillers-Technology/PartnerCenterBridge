using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.PartnerCenter;

/// <summary>
/// One-time interactive bootstrap of the Secure Application Model. An MFA'd admin agent signs in
/// via device code against the multi-tenant SAM app; the resulting refresh token (captured from
/// the MSAL cache) is persisted encrypted and then auto-rotated by <see cref="SamTokenService"/>.
/// </summary>
public class SamBootstrapService
{
    private readonly ISamTokenStore _store;
    private readonly PartnerOptions _opts;

    public SamBootstrapService(ISamTokenStore store, IOptions<PartnerOptions> opts)
    {
        _store = store;
        _opts = opts.Value;
    }

    /// <summary>
    /// Run the device-code flow. <paramref name="showDeviceCode"/> receives the instruction text
    /// (URL + code) to present to the operator. Returns the signed-in account's username.
    /// </summary>
    public async Task<string> BootstrapAsync(Func<string, Task> showDeviceCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ClientId) || string.IsNullOrWhiteSpace(_opts.PartnerTenantId))
            throw new InvalidOperationException("Partner:ClientId and Partner:PartnerTenantId must be configured before bootstrap.");

        string? capturedCache = null;
        var app = PublicClientApplicationBuilder.Create(_opts.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{_opts.PartnerTenantId}")
            .WithDefaultRedirectUri()
            .Build();

        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                capturedCache = Encoding.UTF8.GetString(args.TokenCache.SerializeMsalV3());
        });

        // .default plus the implicit offline_access yields a refresh token redeemable for any
        // resource the app is consented for (Graph and Partner Center) via the confidential client.
        var scopes = new[] { $"{Resources.Graph}/.default" };
        var result = await app.AcquireTokenWithDeviceCode(scopes, dc => showDeviceCode(dc.Message))
            .ExecuteAsync(ct);

        var refreshToken = SamTokenService.ExtractRefreshToken(capturedCache)
            ?? throw new InvalidOperationException("Device code sign-in succeeded but no refresh token was issued.");
        await _store.SaveRefreshTokenAsync(refreshToken, ct);

        return result.Account?.Username ?? "(unknown)";
    }
}

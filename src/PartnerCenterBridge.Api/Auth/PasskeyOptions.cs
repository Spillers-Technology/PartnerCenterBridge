namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// WebAuthn relying-party configuration for <c>Auth:Mode=Local</c> passkeys. Bound from
/// <c>Auth:Local:Passkey</c>. <see cref="RelyingPartyId"/> must be the bare domain the SPA is
/// served from (no scheme/port) and <see cref="Origins"/> must exactly match the origin(s)
/// (scheme+host+port) the browser sends -- a mismatch fails every ceremony.
/// </summary>
public class PasskeyOptions
{
    public const string SectionName = "Auth:Local:Passkey";

    public string RelyingPartyId { get; set; } = "localhost";
    public string RelyingPartyName { get; set; } = "Partner Center Bridge";
    public List<string> Origins { get; set; } = new() { "http://localhost:8082" };
}

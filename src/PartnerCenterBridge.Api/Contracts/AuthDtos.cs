using PartnerCenterBridge.Core;
using Fido2NetLib;

namespace PartnerCenterBridge.Api.Contracts;

public record RegisterRequest(string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);

public record TenantAccessDto(Guid TenantId, string TenantName, TenantRole Role);

public record MeDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsSystemAdmin,
    bool TotpEnabled,
    IReadOnlyList<TenantAccessDto> TenantAccess,
    IReadOnlyList<InstanceRole> InstanceRoles,
    IReadOnlyList<string> InstancePermissions,
    long AuthorizationVersion);

public record AuthResponse(string AccessToken, MeDto User);

/// <summary>Returned from <c>/api/auth/login</c> instead of <see cref="AuthResponse"/> when the account has TOTP enabled -- the password was correct but a second factor is still required.</summary>
public record MfaChallengeResponse(string MfaTicket);

public record GrantAccessRequest(string Email, TenantRole Role, DateTimeOffset? ExpiresAt);

/// <summary>One row of "who has access to this tenant" -- distinct from <see cref="TenantAccessDto"/> (which is the other direction: "which tenants does this user have access to").</summary>
public record TenantGrantDto(Guid UserId, string Email, TenantRole Role, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt);

// --- TOTP ------------------------------------------------------------------------------------
public record TotpEnrollResponse(string PendingKey, string Secret, string OtpAuthUri);
public record TotpVerifyEnrollRequest(string PendingKey, string Code);
public record TotpVerifyEnrollResponse(IReadOnlyList<string> RecoveryCodes);
public record TotpDisableRequest(string Password);
public record TotpChallengeRequest(string MfaTicket, string Code);

// --- Passkeys ----------------------------------------------------------------------------------
// Options are strongly typed (not object) so serialization always dispatches through Fido2's
// Base64UrlConverter on the byte[] fields (Challenge, User.Id, credential Id) -- the browser's
// WebAuthn API and Fido2NetLib both expect base64url, not standard base64.
public record PasskeyRegisterOptionsResponse(string ChallengeKey, CredentialCreateOptions Options);
public record PasskeyLoginOptionsResponse(string ChallengeKey, AssertionOptions Options);
public record PasskeyRegisterVerifyRequest(string ChallengeKey, AuthenticatorAttestationRawResponse AttestationResponse, string? Nickname);
public record PasskeyLoginVerifyRequest(string ChallengeKey, AuthenticatorAssertionRawResponse AssertionResponse);
public record PasskeyDto(Guid Id, string? Nickname, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

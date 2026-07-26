namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A WebAuthn/FIDO2 credential registered to an <see cref="AppUser"/> -- a passkey. Registered as
/// a discoverable (resident) credential so login can be usernameless: the browser/authenticator
/// presents whichever passkeys it holds for this site without the user typing an email first.
/// </summary>
public class PasskeyCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>The authenticator-assigned credential id (WebAuthn <c>rawId</c>), used to look up the credential on login.</summary>
    public required byte[] CredentialId { get; set; }

    public required byte[] PublicKey { get; set; }

    public required byte[] UserHandle { get; set; }

    /// <summary>Authenticator signature counter, tracked to detect cloned credentials (a replayed counter that doesn't advance).</summary>
    public uint SignatureCounter { get; set; }

    public Guid AaGuid { get; set; }

    /// <summary>User-supplied label ("YubiKey", "MacBook Touch ID") so a Security page can list credentials meaningfully.</summary>
    public string? Nickname { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

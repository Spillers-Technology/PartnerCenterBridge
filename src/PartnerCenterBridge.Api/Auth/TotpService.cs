using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using OtpNet;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// RFC 6238 TOTP enrollment/verification plus single-use recovery codes. The secret is encrypted
/// at rest via ASP.NET Data Protection (same pattern as <c>ProtectedSamTokenStore</c>) rather than
/// hashed -- unlike a password, whoever holds the raw secret can generate valid codes forever, so
/// it has to be recoverable, just not sitting in the database in the clear.
/// </summary>
public class TotpService
{
    private const string ProtectorPurpose = "PartnerCenterBridge.TotpSecret.v1";
    private const string Issuer = "PartnerCenterBridge";
    private const string RecoveryCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    private static readonly PasswordHasher<AppUser> RecoveryCodeHasher = new();

    private readonly IDataProtector _protector;

    public TotpService(IDataProtectionProvider dp) => _protector = dp.CreateProtector(ProtectorPurpose);

    /// <summary>Generates a new secret and its otpauth:// URI. Not persisted or enabled yet -- callers must confirm a live code first (see <see cref="VerifyCode"/>).</summary>
    public (string Base32Secret, string OtpAuthUri) GenerateEnrollment(string accountEmail)
    {
        var key = KeyGeneration.GenerateRandomKey(20); // 160-bit, RFC 4226 recommended length
        var secret = Base32Encoding.ToString(key);
        var label = Uri.EscapeDataString($"{Issuer}:{accountEmail}");
        var uri = $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";
        return (secret, uri);
    }

    /// <summary>Allows one 30s step of clock drift each direction; wide enough to absorb skew without materially widening the guess window.</summary>
    public bool VerifyCode(string base32Secret, string code) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).VerifyTotp(code, out _, new VerificationWindow(1, 1));

    public string Protect(string base32Secret) => _protector.Protect(base32Secret);
    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);

    /// <summary>Returns the plaintext codes (show once, never again) and their hashes (what actually gets persisted).</summary>
    public (List<string> Plaintext, List<string> Hashes) GenerateRecoveryCodes(AppUser user, int count = 10)
    {
        var plaintext = new List<string>(count);
        var hashes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var code = FormatRecoveryCode(RandomNumberGenerator.GetBytes(10));
            plaintext.Add(code);
            hashes.Add(RecoveryCodeHasher.HashPassword(user, code));
        }
        return (plaintext, hashes);
    }

    /// <summary>True and the matched hash removed from <paramref name="remainingHashes"/> if <paramref name="code"/> matches an unused recovery code -- codes are single-use.</summary>
    public bool TryConsumeRecoveryCode(AppUser user, string code, out List<string> remainingHashes)
    {
        foreach (var hash in user.TotpRecoveryCodeHashes)
        {
            if (RecoveryCodeHasher.VerifyHashedPassword(user, hash, code) != PasswordVerificationResult.Failed)
            {
                remainingHashes = user.TotpRecoveryCodeHashes.Where(h => h != hash).ToList();
                return true;
            }
        }
        remainingHashes = user.TotpRecoveryCodeHashes;
        return false;
    }

    private static string FormatRecoveryCode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length + 1);
        foreach (var b in bytes) sb.Append(RecoveryCodeAlphabet[b % RecoveryCodeAlphabet.Length]);
        var s = sb.ToString();
        return $"{s[..5]}-{s[5..]}";
    }
}

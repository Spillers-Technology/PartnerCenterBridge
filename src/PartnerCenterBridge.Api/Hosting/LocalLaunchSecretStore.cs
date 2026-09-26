using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// The launch secret of a Local Workbench that runs without an account (the built-in workbench
/// owner, see <c>AppUser.IsWorkbenchOwner</c>): 256 random bits, stored in the data root protected
/// exactly like the generated signing key -- Data Protection with the key ring encrypted by DPAPI
/// for the current Windows user, published atomically under the data root's init lock, and
/// ACL-checked at startup (<see cref="LocalDataDirectory.FindInsecureSensitiveEntry"/>). Only this
/// Windows user can decrypt it, so only processes running as this user (the exe itself, including
/// a second launch that hands over to the running instance) can build the launch URL.
/// Deleting the file revokes every launch link; the owner protecting the workbench with a password
/// deletes it.
/// </summary>
public static class LocalLaunchSecretStore
{
    private const string Purpose = "PartnerCenterBridge.LocalWorkbench.LaunchSecret.v1";

    /// <summary>Returns the saved secret, or generates and saves one. An unreadable file (key ring changed) is replaced.</summary>
    public static string GetOrCreate(LocalWorkbenchOptions options, TimeSpan? lockTimeout = null)
    {
        using var initLock = LocalSigningKeyStore.AcquireInitLock(options, lockTimeout ?? TimeSpan.FromSeconds(30));
        var protector = CreateProtector(options);
        if (TryReadUnlocked(options, protector) is { } existing) return existing;

        var secret = NewSecret();
        var temp = $"{options.LaunchSecretPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, protector.Protect(secret));
            // Under the init lock nobody else can be publishing; overwrite only replaces an unreadable file.
            File.Move(temp, options.LaunchSecretPath, overwrite: true);
            return secret;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The saved secret, or null when there is none or it cannot be decrypted by this user.</summary>
    public static string? TryRead(LocalWorkbenchOptions options)
    {
        if (!File.Exists(options.LaunchSecretPath)) return null;
        try
        {
            return TryReadUnlocked(options, CreateProtector(options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Deletes the saved secret: every launch link stops working.</summary>
    public static void Delete(LocalWorkbenchOptions options, TimeSpan? lockTimeout = null)
    {
        using var initLock = LocalSigningKeyStore.AcquireInitLock(options, lockTimeout ?? TimeSpan.FromSeconds(30));
        if (File.Exists(options.LaunchSecretPath)) File.Delete(options.LaunchSecretPath);
    }

    private static IDataProtector CreateProtector(LocalWorkbenchOptions options) =>
        DataProtectionProvider.Create(new DirectoryInfo(options.KeysPath), builder => builder.ConfigureLocal(options.KeysPath))
            .CreateProtector(Purpose);

    private static string? TryReadUnlocked(LocalWorkbenchOptions options, IDataProtector protector)
    {
        if (!File.Exists(options.LaunchSecretPath)) return null;
        try
        {
            var secret = protector.Unprotect(File.ReadAllText(options.LaunchSecretPath).Trim());
            return string.IsNullOrWhiteSpace(secret) ? null : secret;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>32 random bytes, base64url without padding (safe in a URL fragment).</summary>
    private static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

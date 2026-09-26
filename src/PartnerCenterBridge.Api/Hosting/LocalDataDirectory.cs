using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>Creates the Local Workbench data directory layout.</summary>
public static class LocalDataDirectory
{
    /// <summary>
    /// Creates the data root and its subfolders. A root created here is restricted to the current
    /// user (plus SYSTEM on Windows) on a best-effort basis: %LOCALAPPDATA% is already per-user, so a
    /// failure is reported as a warning rather than blocking startup. An existing directory (for
    /// example one passed with --data-dir) keeps whatever permissions it already has.
    /// </summary>
    public static IReadOnlyList<string> Ensure(LocalWorkbenchOptions options)
    {
        var warnings = new List<string>();
        var created = !Directory.Exists(options.DataRoot);
        Directory.CreateDirectory(options.DataRoot);
        if (created)
        {
            try { RestrictToCurrentUser(options.DataRoot); }
            catch (Exception ex)
            {
                warnings.Add($"Could not restrict '{options.DataRoot}' to the current user: {ex.Message}");
            }
        }

        foreach (var path in new[] { options.KeysPath, options.LogsPath, options.PackagesPath, options.CertificatesPath })
            Directory.CreateDirectory(path);
        return warnings;
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user has no SID.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>
/// Data Protection settings shared by the app's key ring and the pre-host signing-key store, so
/// both read and write the same keys: same directory, same application name, and (on Windows)
/// keys encrypted at rest with DPAPI for the current user.
/// </summary>
public static class LocalDataProtection
{
    public const string ApplicationName = "PartnerCenterBridge";

    public static IDataProtectionBuilder ConfigureLocal(this IDataProtectionBuilder builder, string keysPath)
    {
        builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName(ApplicationName);
        if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        return builder;
    }
}

/// <summary>
/// The Local Workbench's generated <c>Auth:Local:SigningKey</c>: 256 random bits created on first
/// launch, stored protected with Data Protection in the data root, and reused on every restart so
/// issued tokens survive a restart. Deleting the file (or the key ring) rotates it, which signs
/// everyone out.
/// </summary>
public static class LocalSigningKeyStore
{
    private const string Purpose = "PartnerCenterBridge.LocalWorkbench.SigningKey.v1";

    public static string GetOrCreate(LocalWorkbenchOptions options)
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(options.KeysPath),
            builder => builder.ConfigureLocal(options.KeysPath));
        var protector = provider.CreateProtector(Purpose);

        if (File.Exists(options.SigningKeyPath))
        {
            try
            {
                return protector.Unprotect(File.ReadAllText(options.SigningKeyPath).Trim());
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"The saved sign-in signing key at '{options.SigningKeyPath}' cannot be decrypted (the key ring in " +
                    $"'{options.KeysPath}' changed or belongs to another user). Delete that file to generate a new key; " +
                    "everyone will have to sign in again.", ex);
            }
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var temp = options.SigningKeyPath + ".tmp";
        File.WriteAllText(temp, protector.Protect(key));
        File.Move(temp, options.SigningKeyPath, overwrite: true);
        return key;
    }
}

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
    /// failure is reported as a warning rather than blocking startup. An existing root (for example
    /// one passed with --data-dir) is validated instead and startup is refused when it is shared:
    /// it holds the account database, tenant data, evidence, keys and logs. PCB does not tighten an
    /// existing directory itself -- it may be deliberately shared for something else -- the message
    /// names the command that does.
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
        else
        {
            var problem = FindInsecurePermissions(options.DataRoot);
            if (problem is not null)
                throw new InvalidOperationException(
                    $"The data directory '{options.DataRoot}' is not private to the current user: {problem}. It holds the " +
                    "account database, tenant data, evidence, keys and logs, so PCB will not start from it. Fix: " +
                    FixCommand(options.DataRoot) + " -- or choose another folder with --data-dir (a new folder is created private).");

            var sensitive = FindInsecureSensitiveEntry(options);
            if (sensitive is not null)
                throw new InvalidOperationException(
                    $"'{sensitive.Value.Path}' in the data directory is not private to the current user: {sensitive.Value.Problem}. " +
                    "It holds account data or keys, so PCB will not start. Fix: " + ResetCommand(sensitive.Value.Path) +
                    " (removes its explicit permissions so it inherits the private data directory's).");
        }

        foreach (var path in new[] { options.KeysPath, options.LogsPath, options.PackagesPath, options.CertificatesPath })
            Directory.CreateDirectory(path);
        // Pre-release builds kept a reusable launch secret here; one-time tickets replaced it.
        try { if (File.Exists(options.LegacyLaunchSecretPath)) File.Delete(options.LegacyLaunchSecretPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return warnings;
    }

    private static string FixCommand(string path) => OperatingSystem.IsWindows()
        ? $"icacls \"{path}\" /inheritance:r /grant:r \"%USERNAME%:(OI)(CI)F\" \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\""
        : $"chmod 700 '{path}'";

    private static string ResetCommand(string path) => OperatingSystem.IsWindows()
        ? $"icacls \"{path}\" /reset"
        : $"chmod go-rwx '{path}'";

    /// <summary>
    /// The first sensitive entry under the data root (the database, the Data Protection key ring and
    /// its keys, the protected signing key) that carries an explicit permission entry giving someone
    /// else access, or null. Inherited entries come from the data root, which is validated
    /// separately; this catches a file or folder that was shared on its own. Windows only: elsewhere
    /// the private root's mode already keeps other users out of everything below it.
    /// </summary>
    public static (string Path, string Problem)? FindInsecureSensitiveEntry(LocalWorkbenchOptions options)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<string> { options.DatabasePath, options.SigningKeyPath, options.KeysPath };
        if (Directory.Exists(options.KeysPath)) candidates.AddRange(Directory.GetFiles(options.KeysPath));
        foreach (var candidate in candidates)
        {
            FileSystemSecurity security;
            if (Directory.Exists(candidate)) security = new DirectoryInfo(candidate).GetAccessControl();
            else if (File.Exists(candidate)) security = new FileInfo(candidate).GetAccessControl();
            else continue;
            var problem = FindUntrustedAccess(security, includeInherited: false);
            if (problem is not null) return (candidate, problem);
        }
        return null;
    }

    /// <summary>
    /// Why <paramref name="path"/> is readable/writable by others, or null when it is private.
    /// Windows: the owner must be the current user, Administrators or SYSTEM, and no allow entry may
    /// give anyone else read or write access -- not just Everyone/Users/Authenticated Users but any
    /// other named user or group. The only principals allowed in are the current user, SYSTEM,
    /// BUILTIN\Administrators and CREATOR OWNER / OWNER RIGHTS (which resolve to the owner, itself
    /// checked above). Elsewhere: no group/other permission bits.
    /// </summary>
    public static string? FindInsecurePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return FindInsecureAcl(path);

        const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        var mode = File.GetUnixFileMode(path);
        return (mode & shared) != 0 ? $"its mode {Convert.ToString((int)mode, 8)} gives group/other access" : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindInsecureAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var current = WindowsIdentity.GetCurrent().User;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var trustedOwners = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalSystemSid, null)
        };
        if (current is not null) trustedOwners.Add(current);
        if (owner is null || !trustedOwners.Contains(owner))
            return $"it is owned by {Describe(owner)}, not by the current user";

        return FindUntrustedAccess(security, includeInherited: true);
    }

    /// <summary>
    /// The first allow entry in <paramref name="security"/> that gives a principal other than the
    /// trusted ones read or write access, described; or null.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindUntrustedAccess(FileSystemSecurity security, bool includeInherited)
    {
        // Reading or changing anything in the entry. Generic bits can appear raw in inherited ACEs.
        const FileSystemRights sensitive =
            FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
            | (FileSystemRights)unchecked((int)0x80000000)   // GENERIC_READ
            | (FileSystemRights)0x40000000                     // GENERIC_WRITE
            | (FileSystemRights)0x10000000;                    // GENERIC_ALL
        // An allowlist: everyone else (another named user, a custom or domain group, Users, Everyone...)
        // is untrusted.
        var trusted = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.CreatorOwnerSid, null),
            new("S-1-3-4") // OWNER RIGHTS
        };
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null) trusted.Add(current);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, includeInherited, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || trusted.Contains(sid)) continue;
            if ((rule.FileSystemRights & sensitive) != 0)
                return $"{Describe(sid)} has {rule.FileSystemRights} access";
        }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "an unknown owner";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
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

    /// <summary>
    /// Returns the saved key, or generates and saves one. Two first launches can race here (port
    /// preflight runs later), so the whole read-or-create -- including the Data Protection key ring
    /// it creates -- runs under an exclusive lock file in the data root, and the key file is
    /// published without overwrite from a unique temporary file: if another process still won, its
    /// key is read back and used.
    /// </summary>
    public static string GetOrCreate(LocalWorkbenchOptions options, TimeSpan? lockTimeout = null)
    {
        using var initLock = AcquireInitLock(options, lockTimeout ?? TimeSpan.FromSeconds(30));
        var provider = DataProtectionProvider.Create(new DirectoryInfo(options.KeysPath),
            builder => builder.ConfigureLocal(options.KeysPath));
        var protector = provider.CreateProtector(Purpose);

        if (File.Exists(options.SigningKeyPath)) return ReadSaved(options, protector);

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var temp = $"{options.SigningKeyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, protector.Protect(key));
            try
            {
                File.Move(temp, options.SigningKeyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(options.SigningKeyPath))
            {
                return ReadSaved(options, protector); // someone else published first: use theirs
            }
            return key;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string ReadSaved(LocalWorkbenchOptions options, IDataProtector protector)
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

    /// <summary>Exclusive lock across processes (and threads): an open-exclusive lock file in the data root.</summary>
    internal static FileStream AcquireInitLock(LocalWorkbenchOptions options, TimeSpan timeout)
    {
        var path = Path.Combine(options.DataRoot, ".init.lock");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Another Partner Center Bridge process is initializing '{options.DataRoot}' (lock '{path}' is held). " +
                    "Wait for it to finish starting, or stop it, then try again.", ex);
            }
        }
    }
}

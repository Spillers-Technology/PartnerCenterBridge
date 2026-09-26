using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Local profile hardening: no silent network listeners, a private data root, and a race-free
/// first-launch signing key.
/// </summary>
public sealed class LocalHostingSecurityTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "pcb-sec-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _factories = new();

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static LocalWorkbenchOptions Options(string? listen = null, string dataDir = "") =>
        LocalWorkbenchOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostingKeys.DataDir] = dataDir.Length > 0 ? dataDir : Path.GetTempPath(),
            [HostingKeys.Listen] = listen,
            [HostingKeys.Port] = "5199"
        }).Build());

    private WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(HostingKeys.Profile, HostingProfile.Local);
            builder.UseSetting(HostingKeys.DataDir, _dataDir);
            builder.UseSetting(HostingKeys.Port, "5199");
            builder.UseSetting(HostingKeys.OpenBrowser, "false");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });
        _factories.Add(factory);
        return factory;
    }

    // --- Finding 1: configured Kestrel endpoints must not add listeners ---

    [Fact]
    public void Local_profile_refuses_configured_kestrel_endpoints()
    {
        var factory = Host(("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:5081"));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("refuses configured Kestrel endpoints", error.GetBaseException().Message);
    }

    [Fact]
    public void Effective_addresses_must_be_loopback_without_listen()
    {
        var local = Options();
        Assert.Null(LocalListeners.Validate(new[] { "http://127.0.0.1:5199" }, local));
        Assert.Null(LocalListeners.Validate(new[] { "http://localhost:5199", "http://[::1]:5199" }, local));
        Assert.NotNull(LocalListeners.Validate(new[] { "http://127.0.0.1:5199", "http://0.0.0.0:5081" }, local));
        Assert.NotNull(LocalListeners.Validate(new[] { "http://127.0.0.1:5199", "http://[::]:5081" }, local));
        Assert.NotNull(LocalListeners.Validate(new[] { "http://*:5081" }, local));
        Assert.NotNull(LocalListeners.Validate(new[] { "http://192.168.1.10:5199" }, local));
        // Unknown effective addresses fail closed.
        Assert.NotNull(LocalListeners.Validate(null, local));
        Assert.NotNull(LocalListeners.Validate(Array.Empty<string>(), local));
    }

    [Fact]
    public void Explicit_listen_address_is_the_only_non_loopback_address_allowed()
    {
        var local = Options("192.168.1.10");
        Assert.Null(LocalListeners.Validate(new[] { "http://127.0.0.1:5199", "http://192.168.1.10:5199" }, local));
        Assert.NotNull(LocalListeners.Validate(new[] { "http://127.0.0.1:5199", "http://10.0.0.5:5199" }, local));
        Assert.Null(LocalListeners.Validate(new[] { "http://0.0.0.0:5199" }, Options("0.0.0.0")));
    }

    // --- Finding 13: a specific --listen keeps the canonical loopback listener ---

    [Fact]
    public void Specific_listen_address_keeps_the_canonical_loopback_listener()
    {
        Assert.Equal(new[] { IPAddress.Loopback }, Options().ListenAddresses);
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.Parse("192.168.1.10") }, Options("192.168.1.10").ListenAddresses);
        // Wildcards already cover loopback: bound alone, no duplicate bind.
        Assert.Equal(new[] { IPAddress.Any }, Options("0.0.0.0").ListenAddresses);
        Assert.Equal(new[] { IPAddress.IPv6Any }, Options("::").ListenAddresses);
    }

    // --- Finding 4: an existing shared data root is refused ---

    [Fact]
    public void Existing_data_root_writable_by_users_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_dataDir);
        var info = new DirectoryInfo(_dataDir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        var error = Assert.Throws<InvalidOperationException>(() => LocalDataDirectory.Ensure(Options(dataDir: _dataDir)));
        Assert.Contains("not private to the current user", error.Message);
        Assert.Contains("icacls", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "keys"))); // nothing written into it
    }

    [Fact]
    public void Existing_data_root_shared_with_another_named_principal_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Empty(LocalDataDirectory.Ensure(Options(dataDir: _dataDir))); // fresh, private root
        Assert.Null(LocalDataDirectory.FindInsecurePermissions(_dataDir));
        // Not one of the broad groups: a specific group (or another user) that is still not the
        // current user, SYSTEM or Administrators.
        var other = new SecurityIdentifier(WellKnownSidType.BuiltinBackupOperatorsSid, null);
        var info = new DirectoryInfo(_dataDir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(other, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        Assert.NotNull(LocalDataDirectory.FindInsecurePermissions(_dataDir));
        var error = Assert.Throws<InvalidOperationException>(() => LocalDataDirectory.Ensure(Options(dataDir: _dataDir)));
        Assert.Contains("not private to the current user", error.Message);
    }

    [Fact]
    public void Sensitive_file_shared_explicitly_inside_a_private_root_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var options = Options(dataDir: _dataDir);
        Assert.Empty(LocalDataDirectory.Ensure(options)); // fresh, private root
        File.WriteAllText(options.DatabasePath, "db");
        Assert.Empty(LocalDataDirectory.Ensure(options)); // inherited-only file: fine

        var file = new FileInfo(options.DatabasePath);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinBackupOperatorsSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.Null(LocalDataDirectory.FindInsecurePermissions(_dataDir)); // the root itself is still private
        var error = Assert.Throws<InvalidOperationException>(() => LocalDataDirectory.Ensure(options));
        Assert.Contains("pcb.db", error.Message);
        Assert.Contains("icacls", error.Message);
    }

    [Fact]
    public void Existing_private_data_root_and_a_fresh_root_are_accepted()
    {
        // Fresh: created and restricted here.
        Assert.Empty(LocalDataDirectory.Ensure(Options(dataDir: _dataDir)));
        Assert.Null(LocalDataDirectory.FindInsecurePermissions(_dataDir));
        // Existing and private (the second launch): accepted.
        Assert.Empty(LocalDataDirectory.Ensure(Options(dataDir: _dataDir)));
    }

    // --- Finding 14: concurrent first launches agree on one signing key ---

    [Fact]
    public async Task Concurrent_first_launches_agree_on_one_signing_key()
    {
        var options = Options(dataDir: _dataDir);
        LocalDataDirectory.Ensure(options);

        using var start = new Barrier(6);
        var keys = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            return LocalSigningKeyStore.GetOrCreate(options);
        })));

        Assert.Single(keys.Distinct());
        // What is on disk is the key every caller got.
        Assert.Equal(keys[0], LocalSigningKeyStore.GetOrCreate(options));
        Assert.Empty(Directory.GetFiles(_dataDir, "*.tmp"));
    }
}

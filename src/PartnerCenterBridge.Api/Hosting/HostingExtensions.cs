using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Data.Sqlite;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Everything that differs between the Server and Local hosting profiles is decided here, so the
/// rest of the app only reads ordinary configuration (Persistence:Provider, Auth:Mode, ...).
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Applies the CLI overrides, resolves the hosting profile and, for the Local profile, the data
    /// directory, local configuration defaults, the generated signing key, loopback-only Kestrel,
    /// file logging and the startup banner/browser launcher. Server profile: nothing changes.
    /// </summary>
    public static HostingInfo AddBridgeHosting(this WebApplicationBuilder builder, CliOptions cli)
    {
        var cfg = builder.Configuration;
        var cliOverrides = cli.ToConfigurationOverrides();
        if (cliOverrides.Count > 0) cfg.AddInMemoryCollection(cliOverrides);

        var profile = HostingProfile.Resolve(cfg);
        if (profile != HostingProfile.Local)
        {
            var server = new HostingInfo(profile, null);
            builder.Services.AddSingleton(server);
            return server;
        }

        var local = LocalWorkbenchOptions.FromConfiguration(cfg);
        var directoryWarnings = LocalDataDirectory.Ensure(local).ToList();
        if (!local.IPv6LoopbackAvailable)
            directoryWarnings.Add("IPv6 loopback ([::1]) is not available on this computer, so only 127.0.0.1 is bound.");

        // Local defaults sit above appsettings*.json (which describe the container deployment) and
        // below environment variables and the command line, so those can still override any of it.
        // The optional pcb.local.json in the data root sits between the two.
        var insertAt = IndexOfEnvironmentSource(cfg);
        IConfigurationSource[] localSources =
        {
            new MemoryConfigurationSource { InitialData = LocalDefaults(local) },
            // AddJsonFile resolves the file provider for an absolute path; borrow its source.
            new ConfigurationBuilder().AddJsonFile(local.ConfigFilePath, optional: true, reloadOnChange: false).Sources[0]
        };
        foreach (var source in localSources.Reverse()) cfg.Sources.Insert(insertAt, source);

        var authMode = AuthModeInfo.Resolve(cfg);
        if (authMode == AuthModeInfo.Dev)
            throw new InvalidOperationException(
                "The Local profile refuses Auth:Mode=Dev (it would let anyone who can reach the port act as an " +
                "administrator). Remove the Auth:Mode/Auth:Enabled override, or use the Server profile for development.");

        if (authMode == AuthModeInfo.Local && string.IsNullOrWhiteSpace(cfg[$"{LocalAuthOptions.SectionName}:SigningKey"]))
        {
            cfg.Sources.Insert(insertAt + localSources.Length, new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    [$"{LocalAuthOptions.SectionName}:SigningKey"] = LocalSigningKeyStore.GetOrCreate(local)
                }
            });
        }

        // Listeners: only the ones decided here. Configured Kestrel endpoints would be added on top of
        // them (they are cumulative), so they are refused outright, and Kestrel's configuration
        // loader is replaced with an empty one so a later reload cannot add any either. URLs
        // (ASPNETCORE_URLS, --urls, ASPNETCORE_HTTP_PORTS) are overridden by code-bound listeners as
        // long as PreferHostingUrls stays false. Program.cs re-checks the effective addresses after
        // start (LocalListeners.Validate) and stops if anything non-loopback slipped through.
        LocalListeners.RefuseConfiguredEndpoints(cfg);
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            foreach (var address in local.ListenAddresses) kestrel.Listen(address, local.Port);
        });
        builder.Logging.AddProvider(new FileLoggerProvider(local.LogsPath));

        // Passkeys are bound to the canonical origin only. PasskeyOptions' compiled default origin
        // (the container SPA's) must not linger next to it; an explicitly configured one is kept.
        var configuredOrigins = cfg.GetSection($"{PasskeyOptions.SectionName}:Origins").Get<string[]>() ?? Array.Empty<string>();
        builder.Services.PostConfigure<PasskeyOptions>(options =>
            options.Origins = options.Origins.Where(configuredOrigins.Contains).Distinct().ToList());

        var info = new HostingInfo(profile, local);
        builder.Services.AddSingleton(info);
        builder.Services.AddSingleton(local);
        builder.Services.AddSingleton(new LocalStartupWarnings(directoryWarnings));
        builder.Services.AddHostedService<LocalWorkbenchLifetime>();
        // A second launch asks this process for a fresh one-time link over a current-user-only pipe.
        builder.Services.AddHostedService(sp => new LaunchHandOffServer(
            sp.GetRequiredService<IHostApplicationLifetime>(), local, sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(), sp.GetRequiredService<ILogger<LaunchHandOffServer>>()));
        return info;
    }

    /// <summary>
    /// Data Protection keys must be persisted so the encrypted SAM token (and the Local signing key)
    /// survive restarts. Local: the data root's keys folder, DPAPI-protected on Windows.
    /// </summary>
    public static IServiceCollection AddBridgeDataProtection(this IServiceCollection services, IConfiguration cfg, HostingInfo hosting)
    {
        var dataProtection = services.AddDataProtection();
        if (hosting.Local is { } local)
            dataProtection.ConfigureLocal(cfg["DataProtection:KeyRingPath"] ?? local.KeysPath);
        else
            dataProtection
                .PersistKeysToFileSystem(new DirectoryInfo(cfg["DataProtection:KeyRingPath"] ?? "/keys"))
                .SetApplicationName(LocalDataProtection.ApplicationName);
        return services;
    }

    private static Dictionary<string, string?> LocalDefaults(LocalWorkbenchOptions local)
    {
        var defaults = new Dictionary<string, string?>
        {
            [HostingKeys.Profile] = HostingProfile.Local,
            ["Persistence:Provider"] = PersistenceProviders.Sqlite,
            ["ConnectionStrings:Sqlite"] = SqliteBridgePersistence.ConnectionStringForFile(local.DatabasePath),
            ["DataProtection:KeyRingPath"] = local.KeysPath,
            ["Packages:Path"] = local.PackagesPath,
            ["Exchange:CertificatePath"] = Path.Combine(local.CertificatesPath, "exo.pfx"),
            ["Auth:Mode"] = AuthModeInfo.Local,
            // The exe's working directory usually has no appsettings.json, so set sane levels here.
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
            [$"{PasskeyOptions.SectionName}:RelyingPartyId"] = "localhost",
            [$"{PasskeyOptions.SectionName}:Origins:0"] = local.CanonicalUrl,
            // Host filtering: only the loopback names (plus an explicit --listen address) are served,
            // which also shuts out DNS-rebinding hosts.
            ["AllowedHosts"] = local.IsLoopbackOnly
                ? "localhost;127.0.0.1;[::1]"
                : $"localhost;127.0.0.1;[::1];{FormatHost(local.ListenAddress)}"
        };
        return defaults;
    }

    private static string FormatHost(System.Net.IPAddress address) =>
        LocalWorkbenchOptions.IsWildcard(address)
            ? "*"
            : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static int IndexOfEnvironmentSource(IConfigurationBuilder cfg)
    {
        // The first unprefixed environment-variables source is where "env and command line" begin;
        // prefixed ones (ASPNETCORE_/DOTNET_) belong to host configuration and come earlier.
        for (var i = 0; i < cfg.Sources.Count; i++)
            if (cfg.Sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
                return i;
        return cfg.Sources.Count;
    }
}

/// <summary>Non-fatal problems found while preparing the data directory, printed with the banner.</summary>
public sealed record LocalStartupWarnings(IReadOnlyList<string> Messages);

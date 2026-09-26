using System.Net;
using System.Reflection;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>Configuration keys owned by the hosting layer. CLI flags map onto these.</summary>
public static class HostingKeys
{
    public const string Profile = "Hosting:Profile";
    public const string Port = "Hosting:Port";
    public const string DataDir = "Hosting:DataDir";
    public const string Listen = "Hosting:Listen";
    public const string OpenBrowser = "Hosting:OpenBrowser";

    /// <summary>Assembly metadata key baked into the Local Workbench publish (PcbLocalWorkbench=true).</summary>
    public const string BakedProfileMetadata = "PcbHostingProfile";
}

/// <summary>
/// How this process is hosted. <see cref="Server"/> is today's container deployment (Postgres,
/// nginx in front, configured auth). <see cref="Local"/> is the single-user Local Workbench: SQLite
/// under the user's data directory, loopback-only Kestrel, embedded SPA and Local accounts.
/// </summary>
public static class HostingProfile
{
    public const string Server = "Server";
    public const string Local = "Local";

    /// <summary>
    /// Precedence: <c>Hosting:Profile</c> from configuration (which includes the <c>--local</c> flag,
    /// applied as the highest-precedence override, and env such as <c>Hosting__Profile</c>), then
    /// the profile baked into the build, then <see cref="Server"/>.
    /// </summary>
    public static string Resolve(IConfiguration configuration, Assembly? entryAssembly = null)
    {
        var configured = configuration[HostingKeys.Profile];
        if (!string.IsNullOrWhiteSpace(configured)) return Normalize(configured);

        var baked = (entryAssembly ?? typeof(HostingProfile).Assembly)
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == HostingKeys.BakedProfileMetadata)?.Value;
        return string.IsNullOrWhiteSpace(baked) ? Server : Normalize(baked);
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "server" => Server,
            "local" => Local,
            _ => throw new InvalidOperationException(
                $"Unknown Hosting:Profile '{value}'. Expected '{Server}' or '{Local}'.")
        };
}

/// <summary>Where and how the Local Workbench runs. Resolved once at startup from configuration.</summary>
public sealed class LocalWorkbenchOptions
{
    public const int DefaultPort = 5080;
    public const string AppFolderName = "PartnerCenterBridge";

    public required string DataRoot { get; init; }
    public int Port { get; init; } = DefaultPort;
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public bool OpenBrowser { get; init; } = true;

    public bool IsLoopbackOnly => IPAddress.IsLoopback(ListenAddress);

    /// <summary>The one origin the browser should use (passkeys are bound to it).</summary>
    public string CanonicalUrl => $"http://localhost:{Port}";

    public string DatabasePath => Path.Combine(DataRoot, "pcb.db");
    public string KeysPath => Path.Combine(DataRoot, "keys");
    public string LogsPath => Path.Combine(DataRoot, "logs");
    public string PackagesPath => Path.Combine(DataRoot, "packages");
    public string CertificatesPath => Path.Combine(DataRoot, "certs");
    /// <summary>Optional user-edited overrides (Partner app, Exchange app, ...), JSON in appsettings shape.</summary>
    public string ConfigFilePath => Path.Combine(DataRoot, "pcb.local.json");
    /// <summary>The generated Auth:Local:SigningKey, protected with Data Protection.</summary>
    public string SigningKeyPath => Path.Combine(DataRoot, "auth-signing-key.protected");

    public static LocalWorkbenchOptions FromConfiguration(IConfiguration configuration)
    {
        var portText = configuration[HostingKeys.Port];
        var port = DefaultPort;
        if (!string.IsNullOrWhiteSpace(portText)
            && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            throw new InvalidOperationException($"Hosting:Port '{portText}' is not a valid port.");

        var dataDir = configuration[HostingKeys.DataDir];
        return new LocalWorkbenchOptions
        {
            DataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? DefaultDataRoot() : dataDir),
            Port = port,
            ListenAddress = ParseListenAddress(configuration[HostingKeys.Listen]),
            OpenBrowser = configuration.GetValue(HostingKeys.OpenBrowser, true)
        };
    }

    /// <summary>
    /// %LOCALAPPDATA%\PartnerCenterBridge on Windows; $XDG_DATA_HOME/PartnerCenterBridge or
    /// ~/.local/share/PartnerCenterBridge elsewhere.
    /// </summary>
    public static string DefaultDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) return Path.Combine(local, AppFolderName);
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)) return Path.Combine(xdg, AppFolderName);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(home) ? AppContext.BaseDirectory : home, ".local", "share", AppFolderName);
    }

    /// <summary>
    /// Null/empty means the default 127.0.0.1. <c>localhost</c> maps to 127.0.0.1 too; any other
    /// value must be an IP literal (<c>0.0.0.0</c> binds every IPv4 interface, and only when asked).
    /// </summary>
    public static IPAddress ParseListenAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return IPAddress.Loopback;
        var trimmed = value.Trim();
        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var address)) return address;
        throw new InvalidOperationException(
            $"--listen/Hosting:Listen '{value}' is not an IP address (for example 127.0.0.1 or 192.168.1.10).");
    }
}

/// <summary>What the running process is: profile, version and (Local only) the workbench options.</summary>
public sealed class HostingInfo
{
    public HostingInfo(string profile, LocalWorkbenchOptions? local)
    {
        Profile = profile;
        Local = local;
    }

    public string Profile { get; }
    public LocalWorkbenchOptions? Local { get; }
    public bool IsLocal => Local is not null;
    public string Version => ProductVersion;

    /// <summary>The assembly informational version without the +commit suffix.</summary>
    public static string ProductVersion { get; } = ResolveVersion();

    /// <summary>How to invoke this binary in printed fix commands.</summary>
    public static string CommandName
    {
        get
        {
            var file = Path.GetFileName(Environment.ProcessPath ?? "");
            return string.IsNullOrEmpty(file) || file.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "dotnet PartnerCenterBridge.Api.dll"
                : file;
        }
    }

    private static string ResolveVersion()
    {
        var version = typeof(HostingInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HostingInfo).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}

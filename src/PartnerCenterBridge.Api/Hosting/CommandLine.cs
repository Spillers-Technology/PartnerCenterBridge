namespace PartnerCenterBridge.Api.Hosting;

public enum CliCommand
{
    /// <summary>Start the web host (the default).</summary>
    Run,
    /// <summary>Print the system diagnostics checks and exit 0/1 without starting the web server.</summary>
    Doctor,
    /// <summary>Interactive SAM device-code bootstrap, then exit.</summary>
    BootstrapSam,
    Help,
    Version
}

/// <summary>
/// The parsed command line. Recognized flags are consumed here and applied to configuration under
/// the <c>Hosting:*</c> keys (see <see cref="HostingKeys"/>) so config/env can set the same things;
/// anything that is plain host configuration (<c>--Section:Key=value</c>, <c>--environment=...</c>)
/// is passed through untouched in <see cref="HostArgs"/>.
/// </summary>
public sealed record CliOptions
{
    public CliCommand Command { get; init; } = CliCommand.Run;
    public bool Local { get; init; }
    public int? Port { get; init; }
    public bool NoBrowser { get; init; }
    public string? DataDir { get; init; }
    public string? Listen { get; init; }
    public string[] HostArgs { get; init; } = Array.Empty<string>();

    /// <summary>Set when the arguments were not understood; the caller prints help and exits 2.</summary>
    public string? Error { get; init; }

    /// <summary>The recognized flags as configuration overrides (highest precedence).</summary>
    public Dictionary<string, string?> ToConfigurationOverrides()
    {
        var overrides = new Dictionary<string, string?>();
        if (Local) overrides[HostingKeys.Profile] = HostingProfile.Local;
        if (Port is { } port) overrides[HostingKeys.Port] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (DataDir is not null) overrides[HostingKeys.DataDir] = DataDir;
        if (Listen is not null) overrides[HostingKeys.Listen] = Listen;
        if (NoBrowser) overrides[HostingKeys.OpenBrowser] = "false";
        return overrides;
    }
}

public static class CliParser
{
    // Generic-host switches that take a separate value (`--environment Production`). Anything with
    // a ':' in its name is treated as a configuration key the same way.
    private static readonly HashSet<string> HostValueSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "environment", "contentRoot", "applicationName", "urls", "webroot"
    };

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        var hostArgs = new List<string>();
        CliCommand? command = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var name = arg;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 2)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            string? TakeValue()
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                return null;
            }

            switch (name.ToLowerInvariant())
            {
                case "-h" or "-?" or "--help" or "/?" or "help":
                    return options with { Command = CliCommand.Help };
                case "--version":
                    return options with { Command = CliCommand.Version };
                case "--local":
                    options = options with { Local = true };
                    continue;
                case "--no-browser":
                    options = options with { NoBrowser = true };
                    continue;
                case "--port":
                {
                    var value = TakeValue();
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                        return Fail($"--port needs a number between 1 and 65535 (got '{value}').");
                    options = options with { Port = port };
                    continue;
                }
                case "--data-dir":
                {
                    var value = TakeValue();
                    if (string.IsNullOrWhiteSpace(value)) return Fail("--data-dir needs a directory path.");
                    options = options with { DataDir = value };
                    continue;
                }
                case "--listen":
                {
                    var value = TakeValue();
                    if (string.IsNullOrWhiteSpace(value)) return Fail("--listen needs an IP address.");
                    options = options with { Listen = value };
                    continue;
                }
                case "doctor" or "bootstrap-sam":
                {
                    var parsed = name.Equals("doctor", StringComparison.OrdinalIgnoreCase)
                        ? CliCommand.Doctor : CliCommand.BootstrapSam;
                    if (command is not null) return Fail($"Only one command may be given (got '{arg}' after another).");
                    command = parsed;
                    continue;
                }
            }

            // Host configuration passthrough: --Key=value, /Key=value, Key=value, or
            // --Section:Key value / --environment value.
            if (inlineValue is not null || (!arg.StartsWith('-') && arg.Contains('=')) || (arg.StartsWith('/') && arg.Contains('=')))
            {
                hostArgs.Add(arg);
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal)
                && (arg.Contains(':') || HostValueSwitches.Contains(arg[2..]))
                && i + 1 < args.Count)
            {
                hostArgs.Add(arg);
                hostArgs.Add(args[++i]);
                continue;
            }

            return Fail($"Unknown argument '{arg}'.");
        }

        return options with { Command = command ?? CliCommand.Run, HostArgs = hostArgs.ToArray() };

        CliOptions Fail(string error) => options with { Error = error };
    }

    public static string HelpText(string exeName) =>
        $"""
        Partner Center Bridge

        Usage: {exeName} [command] [options]

        Commands:
          (none)          Start the web app (API + UI).
          doctor          Check configuration and dependencies, print the results and exit
                          (exit code 0 = no errors, 1 = at least one error). Does not start the server.
          bootstrap-sam   Run the interactive Secure Application Model bootstrap (device code) and exit.

        Options:
          --local             Use the Local Workbench profile (SQLite, loopback only, local accounts).
                              The Local Workbench build uses it by default.
          --port <N>          Port to listen on (Local profile; default {LocalWorkbenchOptions.DefaultPort}).
          --data-dir <path>   Data directory (Local profile; default {LocalWorkbenchOptions.DefaultDataRoot()}).
          --listen <address>  Bind to another address instead of 127.0.0.1. Exposes the app to the
                              network; only use it when you understand the consequences.
          --no-browser        Do not open the browser after startup. A workbench used without an
                              account prints its sign-in link instead: that link grants full
                              access to this workbench, so do not share it.
          --version           Print the version and exit.
          -h, --help          Print this help and exit.

        Any --Section:Key=value argument is passed through as configuration.
        """;
}

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Exchange;

namespace PartnerCenterBridge.Api.Diagnostics;

/// <summary>What the Exchange Online path (pwsh + ExchangeOnlineManagement + app-only cert) has available.</summary>
public sealed record ExchangeDependencyState(
    string PwshCommand,
    string? PwshPath,
    string? PwshVersion,
    string? PwshError,
    string? ModuleVersion,
    string? ModuleError,
    bool AppIdConfigured,
    string CertificatePath,
    bool CertificatePresent)
{
    public bool PwshAvailable => PwshPath is not null && PwshVersion is not null;
    public bool ModuleAvailable => ModuleVersion is not null;
    public bool AppConfigured => AppIdConfigured && CertificatePresent;
    public bool Ready => PwshAvailable && ModuleAvailable && AppConfigured;

    /// <summary>The first missing piece in the order an operator has to fix them, or null when ready.</summary>
    public string? MissingReason =>
        !PwshAvailable ? PwshError ?? $"PowerShell 7 ('{PwshCommand}') was not found"
        : !ModuleAvailable ? ModuleError ?? "the ExchangeOnlineManagement module is not installed for pwsh"
        : !AppIdConfigured ? "Exchange:AppId is not set"
        : !CertificatePresent ? $"the app-only certificate was not found at '{CertificatePath}'"
        : null;
}

public interface IExchangeDependencyProbe
{
    Task<ExchangeDependencyState> GetAsync(CancellationToken ct);
}

/// <summary>
/// Probes pwsh and the EXO module out of process. Those probes cost seconds, so their result is
/// cached for <see cref="CacheDuration"/>; the app-registration part is cheap and always fresh.
/// </summary>
public sealed class ExchangeDependencyProbe : IExchangeDependencyProbe
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IOptionsMonitor<ExchangeOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, string Command, RuntimeProbe Result)? _cached;

    public ExchangeDependencyProbe(IOptionsMonitor<ExchangeOptions> options) => _options = options;

    private sealed record RuntimeProbe(string? PwshPath, string? PwshVersion, string? PwshError, string? ModuleVersion, string? ModuleError);

    public async Task<ExchangeDependencyState> GetAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var command = string.IsNullOrWhiteSpace(options.PwshPath) ? "pwsh" : options.PwshPath;
        var runtime = await GetRuntimeAsync(command, ct);
        var certificatePath = options.CertificatePath ?? "";
        return new ExchangeDependencyState(
            command, runtime.PwshPath, runtime.PwshVersion, runtime.PwshError, runtime.ModuleVersion, runtime.ModuleError,
            !string.IsNullOrWhiteSpace(options.AppId),
            certificatePath,
            !string.IsNullOrWhiteSpace(certificatePath) && File.Exists(certificatePath));
    }

    private async Task<RuntimeProbe> GetRuntimeAsync(string command, CancellationToken ct)
    {
        if (_cached is { } hit && hit.Command == command && DateTimeOffset.UtcNow - hit.At < CacheDuration) return hit.Result;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } again && again.Command == command && DateTimeOffset.UtcNow - again.At < CacheDuration)
                return again.Result;
            var result = await ProbeAsync(command, ct);
            _cached = (DateTimeOffset.UtcNow, command, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<RuntimeProbe> ProbeAsync(string command, CancellationToken ct)
    {
        var path = ExecutableLocator.Find(command);
        if (path is null)
            return new RuntimeProbe(null, null, $"PowerShell 7 ('{command}') was not found on PATH", null, null);

        var version = await RunAsync(path, "$PSVersionTable.PSVersion.ToString()", TimeSpan.FromSeconds(15), ct);
        if (version.Error is not null)
            return new RuntimeProbe(path, null, $"'{path}' did not run: {version.Error}", null, null);

        var module = await RunAsync(path,
            "$m = Get-Module -ListAvailable ExchangeOnlineManagement | Sort-Object Version -Descending | Select-Object -First 1; " +
            "if ($m) { $m.Version.ToString() }",
            TimeSpan.FromSeconds(30), ct);
        return new RuntimeProbe(path, version.Output, null,
            module.Error is null && !string.IsNullOrWhiteSpace(module.Output) ? module.Output : null,
            module.Error is not null ? $"could not list modules: {module.Error}" : null);
    }

    private static async Task<(string? Output, string? Error)> RunAsync(string pwsh, string script, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Either the probe timed out or the caller went away (e.g. a diagnostics request
                // was aborted): in both cases the pwsh process must not outlive this call, or
                // repeated requests pile up hung module enumerations.
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch { /* best effort */ }
                ct.ThrowIfCancellationRequested();
                return (null, $"timed out after {timeout.TotalSeconds:0}s");
            }
            if (process.ExitCode != 0)
                return (null, $"exit code {process.ExitCode}: {FirstLine(stderr.ToString())}");
            return (stdout.ToString().Trim(), null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}

/// <summary>Resolves a command the way a shell would: a path as-is, otherwise PATH (+PATHEXT on Windows).</summary>
public static class ExecutableLocator
{
    public static string? Find(string command)
    {
        var extensions = OperatingSystem.IsWindows()
            ? new[] { "" }.Concat((Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : new[] { "" };

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return extensions.Select(ext => command + ext).FirstOrDefault(File.Exists);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), command + ext); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

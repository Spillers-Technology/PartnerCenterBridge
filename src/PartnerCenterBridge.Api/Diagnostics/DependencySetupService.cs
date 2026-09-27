using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Api.Diagnostics;

/// <summary>Only these two local, fixed installation operations are accepted by the API.</summary>
public static class DependencyIds
{
    public const string Pwsh = "pwsh";
    public const string ExchangeModule = "exchange-module";
    public static bool IsInstallable(string id) => id is Pwsh or ExchangeModule;
}

public sealed record DependencyInstallResult(bool Installed, string Detail);

public interface IDependencySetupService
{
    bool IsDeclined(string id);
    Task SetDeclinedAsync(string id, bool declined, CancellationToken ct);
    Task<DependencyInstallResult> InstallAsync(string id, CancellationToken ct);
}

/// <summary>
/// Local Workbench dependency setup. The request supplies only an allowlisted ID, never a command,
/// path, script or argument. One install runs at a time; a decline survives browser and app restarts.
/// </summary>
public sealed class DependencySetupService : IDependencySetupService
{
    private readonly HostingInfo _hosting;
    private readonly IExchangeDependencyProbe _probe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _decisionPath;
    private readonly HashSet<string> _declined;

    public DependencySetupService(HostingInfo hosting, IExchangeDependencyProbe probe)
    {
        _hosting = hosting;
        _probe = probe;
        _decisionPath = hosting.Local is { } local ? Path.Combine(local.DataRoot, "dependency-decisions.json") : null;
        _declined = _decisionPath is not null && File.Exists(_decisionPath)
            ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_decisionPath)) ?? new()
            : new();
    }

    public bool IsDeclined(string id)
    {
        lock (_declined) return _declined.Contains(id);
    }

    public async Task SetDeclinedAsync(string id, bool declined, CancellationToken ct)
    {
        EnsureLocal(id);
        await _gate.WaitAsync(ct);
        try
        {
            lock (_declined)
            {
                if (declined) _declined.Add(id);
                else _declined.Remove(id);
            }
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<DependencyInstallResult> InstallAsync(string id, CancellationToken ct)
    {
        EnsureLocal(id);
        await _gate.WaitAsync(ct);
        try
        {
            var state = await _probe.GetAsync(ct);
            if (id == DependencyIds.Pwsh && state.PwshAvailable)
                return new(true, "PowerShell 7 is already available.");
            if (id == DependencyIds.Pwsh && state.PwshCommand is not "pwsh" and not "pwsh.exe")
                return new(false, "Exchange:PwshPath points to a custom location. Correct that setting before installing PowerShell 7.");
            if (id == DependencyIds.ExchangeModule && !state.PwshAvailable)
                return new(false, "Install PowerShell 7 first.");
            if (id == DependencyIds.ExchangeModule && state.ModuleAvailable)
                return new(true, "ExchangeOnlineManagement is already available.");

            string? executable;
            string[] args;
            if (id == DependencyIds.Pwsh)
            {
                executable = OperatingSystem.IsWindows() ? ExecutableLocator.Find("winget") : null;
                if (executable is null) return new(false, "Automatic PowerShell installation needs WinGet on Windows. Use the displayed manual command instead.");
                args = ["install", "--id", "Microsoft.PowerShell", "--exact", "--source", "winget",
                    "--silent", "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements"];
            }
            else
            {
                executable = state.PwshPath!;
                args = ["-NoProfile", "-NonInteractive", "-Command",
                    "$ErrorActionPreference = 'Stop'; Install-Module -Name ExchangeOnlineManagement -Scope CurrentUser -Force -ErrorAction Stop"];
            }

            var run = await RunAsync(executable, args, ct);
            await _probe.InvalidateAsync(ct);
            var after = await _probe.GetAsync(ct);
            var available = id == DependencyIds.Pwsh ? after.PwshAvailable : after.ModuleAvailable;
            if (available)
            {
                lock (_declined) _declined.Remove(id);
                await SaveAsync(ct);
                return new(true, id == DependencyIds.Pwsh ? "PowerShell 7 is ready." : "ExchangeOnlineManagement is ready.");
            }
            return new(false, run.ExitCode == 0
                ? "The installer finished, but the dependency is not visible yet. Check PATH or the module installation, then try again."
                : $"Installer exited with code {run.ExitCode}: {run.Detail}");
        }
        finally { _gate.Release(); }
    }

    private void EnsureLocal(string id)
    {
        if (_hosting.Local is null || !DependencyIds.IsInstallable(id))
            throw new InvalidOperationException("Dependency setup is available only for supported Local Workbench dependencies.");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var path = _decisionPath!;
        string json;
        lock (_declined) json = JsonSerializer.Serialize(_declined.OrderBy(x => x).ToArray());
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<(int ExitCode, string Detail)> RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        // Read both streams concurrently to avoid a full pipe blocking the installer.
        var stdout = ReadOutputAsync(process.StandardOutput, ct);
        var stderr = ReadOutputAsync(process.StandardError, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            var output = await stdout;
            var error = await stderr;
            var detail = (error.Trim().Length > 0 ? error : output).Trim();
            return (process.ExitCode, detail);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            catch { /* best effort */ }
            ct.ThrowIfCancellationRequested();
            return (-1, "Timed out after 10 minutes.");
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
            if (captured.Length < 500) captured.Append(buffer, 0, Math.Min(read, 500 - captured.Length));
        return captured.ToString();
    }
}

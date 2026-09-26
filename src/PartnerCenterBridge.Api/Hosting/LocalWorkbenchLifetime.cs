using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Local profile startup UX: once the host has started, print the banner, wait until the
/// canonical URL's /health answers, then (unless disabled) open the browser there once.
/// </summary>
public sealed class LocalWorkbenchLifetime : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LocalWorkbenchOptions _options;
    private readonly LocalStartupWarnings _warnings;
    private readonly ILogger<LocalWorkbenchLifetime> _log;

    public LocalWorkbenchLifetime(IHostApplicationLifetime lifetime, LocalWorkbenchOptions options,
        LocalStartupWarnings warnings, ILogger<LocalWorkbenchLifetime> log)
    {
        _lifetime = lifetime;
        _options = options;
        _warnings = warnings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await using var stopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try { await started.Task; }
        catch (OperationCanceledException) { return; }

        PrintBanner();
        if (!_options.OpenBrowser) return;

        if (await WaitForHealthAsync(_options.CanonicalUrl, stoppingToken))
            BrowserLauncher.TryOpen(_options.CanonicalUrl, _log);
        else
            _log.LogWarning("{Url}/health did not answer; not opening the browser. Open {Url} manually.",
                _options.CanonicalUrl, _options.CanonicalUrl);
    }

    private void PrintBanner()
    {
        var lines = new List<string>
        {
            "",
            "  Partner Center Bridge " + HostingInfo.ProductVersion + " (Local Workbench)",
            "  Open:      " + _options.CanonicalUrl,
            "  Data:      " + _options.DataRoot,
            "  Profile:   " + HostingProfile.Local,
            "  Stop:      press Ctrl+C in this window",
            ""
        };
        if (!_options.IsLoopbackOnly)
        {
            lines.Insert(5, $"  WARNING:   listening on {_options.ListenAddress}:{_options.Port} -- reachable from other machines.");
        }
        foreach (var warning in _warnings.Messages) lines.Insert(lines.Count - 1, "  Note:      " + warning);
        Console.Out.WriteLine(string.Join(Environment.NewLine, lines));
        _log.LogInformation("Local Workbench listening at {Url} (data {DataRoot})", _options.CanonicalUrl, _options.DataRoot);
    }

    internal static async Task<bool> WaitForHealthAsync(string baseUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 40 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(baseUrl + "/health", ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }
}

public static class BrowserLauncher
{
    public static void TryOpen(string url, ILogger? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not open the browser; open {Url} manually.", url);
            Console.Out.WriteLine($"Could not open the browser; open {url} manually.");
        }
    }
}

public enum PortState { Free, ThisApp, OtherProgram }

/// <summary>
/// Before the Local profile binds its port: is it free, already a Partner Center Bridge (a second
/// launch -- just open the browser there), or someone else's (fail with an actionable message)?
/// </summary>
public static class PortPreflight
{
    public const string InstanceHeader = "X-PCB-Instance";
    public const string InstanceHeaderValue = "PartnerCenterBridge";

    public static async Task<PortState> CheckAsync(LocalWorkbenchOptions options, CancellationToken ct = default)
    {
        // Browsers resolve "localhost" to ::1 first, so something on [::1]:port would shadow us even
        // though our 127.0.0.1 bind succeeds.
        if (CanBind(options.ListenAddress, options.Port) && !await AcceptsConnectionAsync(IPAddress.IPv6Loopback, options.Port, ct))
            return PortState.Free;
        return await IsBridgeAsync(options.CanonicalUrl, ct) ? PortState.ThisApp : PortState.OtherProgram;
    }

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<bool> AcceptsConnectionAsync(IPAddress address, int port, CancellationToken ct)
    {
        if (!Socket.OSSupportsIPv6) return false;
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> IsBridgeAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync(baseUrl + "/api/system/status", ct);
            if (!response.IsSuccessStatusCode
                || !response.Headers.TryGetValues(InstanceHeader, out var values)
                || !values.Contains(InstanceHeaderValue))
                return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("profile", out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Local profile startup UX: once the host has started, print the banner, wait until the
/// canonical URL's /health answers, then (unless disabled) open the browser there once. A
/// workbench used without an account is opened through its launch URL (the launch secret in the
/// fragment), which signs the browser in; with --no-browser that URL is printed instead.
/// </summary>
public sealed class LocalWorkbenchLifetime : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LocalWorkbenchOptions _options;
    private readonly LocalStartupWarnings _warnings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LocalWorkbenchLifetime> _log;

    public LocalWorkbenchLifetime(IHostApplicationLifetime lifetime, LocalWorkbenchOptions options,
        LocalStartupWarnings warnings, IServiceScopeFactory scopes, ILogger<LocalWorkbenchLifetime> log)
    {
        _lifetime = lifetime;
        _options = options;
        _warnings = warnings;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await using var stopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try { await started.Task; }
        catch (OperationCanceledException) { return; }

        string? launchUrl = null;
        try
        {
            launchUrl = await ResolveLaunchUrlAsync(_scopes, _options, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not prepare the launch link for this workbench (no account); open it again from PartnerCenterBridge.exe.");
        }

        PrintBanner(launchUrl);
        if (!_options.OpenBrowser) return;

        if (await WaitForHealthAsync(_options, stoppingToken))
            BrowserLauncher.TryOpen(launchUrl ?? _options.CanonicalUrl, _log);
        else
            _log.LogWarning("{Url}/health did not answer; not opening the browser. Open {Url} manually.",
                _options.CanonicalUrl, _options.CanonicalUrl);
    }

    /// <summary>
    /// The URL that opens this workbench signed in when it is used without an account (creating the
    /// launch secret if needed), or null when it has accounts (the plain canonical URL applies).
    /// </summary>
    public static async Task<string?> ResolveLaunchUrlAsync(IServiceScopeFactory scopes, LocalWorkbenchOptions options, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        if (!await WorkbenchOwnerService.IsAccountlessAsync(db, ct)) return null;
        var owner = scope.ServiceProvider.GetRequiredService<WorkbenchOwnerService>();
        return owner.UnavailableReason is null ? options.LaunchUrl(owner.EnsureSecret()) : null;
    }

    private void PrintBanner(string? launchUrl)
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
        if (launchUrl is not null)
        {
            // The secret is printed only when no browser is opened, and only to this console -- never to the log file.
            var account = new List<string> { "  Account:   none -- anyone who can run programs as " + Environment.UserName + " here can use it" };
            if (!_options.OpenBrowser)
            {
                account.Add("  Sign in:   " + launchUrl);
                account.Add("             (this link signs in with full administrator rights; do not share it)");
            }
            else
            {
                account.Add("             run " + HostingInfo.CommandName + " again to open another signed-in window");
            }
            lines.InsertRange(3, account);
        }
        if (!_options.IsLoopbackOnly)
        {
            lines.Insert(lines.Count - 2, $"  WARNING:   listening on {_options.ListenAddress}:{_options.Port} -- reachable from other machines.");
            _log.LogWarning("--listen {Address}: port {Port} is reachable from other machines (loopback stays bound for {Url}).",
                _options.ListenAddress, _options.Port, _options.CanonicalUrl);
        }
        foreach (var warning in _warnings.Messages) lines.Insert(lines.Count - 1, "  Note:      " + warning);
        Console.Out.WriteLine(string.Join(Environment.NewLine, lines));
        _log.LogInformation("Local Workbench listening at {Url} (data {DataRoot})", _options.CanonicalUrl, _options.DataRoot);
    }

    internal static async Task<bool> WaitForHealthAsync(LocalWorkbenchOptions options, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 40 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var response = await http.SendAsync(LoopbackProbe.Get(options, "/health"), ct);
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
            // The log file never gets a launch secret: only the part before the fragment is logged.
            log?.LogWarning(ex, "Could not open the browser; open {Url} manually.", url.Split('#')[0]);
            Console.Out.WriteLine($"Could not open the browser; open {url} manually.");
        }
    }
}

public enum PortState { Free, ThisApp, OtherProgram }

/// <summary>The preflight outcome; <see cref="Accountless"/> is what a running instance reported in its status.</summary>
public sealed record PortPreflightResult(PortState State, bool Accountless = false);

/// <summary>
/// Before the Local profile binds its port: is it free, already a Partner Center Bridge (a second
/// launch -- just open the browser there), or someone else's (fail with an actionable message)?
/// </summary>
public static class PortPreflight
{
    public const string InstanceHeader = "X-PCB-Instance";
    public const string InstanceHeaderValue = "PartnerCenterBridge";

    public static async Task<PortState> CheckAsync(LocalWorkbenchOptions options, CancellationToken ct = default) =>
        (await CheckDetailedAsync(options, ct)).State;

    public static async Task<PortPreflightResult> CheckDetailedAsync(LocalWorkbenchOptions options, CancellationToken ct = default)
    {
        // Browsers resolve "localhost" to ::1 first, so something on [::1]:port would shadow us even
        // though our 127.0.0.1 bind succeeds.
        if (options.ListenAddresses.All(address => CanBind(address, options.Port)) && !await AcceptsConnectionAsync(IPAddress.IPv6Loopback, options.Port, ct))
            return new PortPreflightResult(PortState.Free);
        return await ProbeBridgeAsync(options, ct) ?? new PortPreflightResult(PortState.OtherProgram);
    }

    /// <summary>
    /// Reads a <c>/api/system/status</c> body: ThisApp (with its accountless flag) when it is a
    /// Partner Center Bridge status, else null. Instances older than the flag report false.
    /// </summary>
    public static PortPreflightResult? ParseStatus(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("profile", out _)) return null;
        var accountless = json.RootElement.TryGetProperty("accountless", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new PortPreflightResult(PortState.ThisApp, accountless);
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

    private static async Task<PortPreflightResult?> ProbeBridgeAsync(LocalWorkbenchOptions options, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.SendAsync(LoopbackProbe.Get(options, "/api/system/status"), ct);
            if (!response.IsSuccessStatusCode
                || !response.Headers.TryGetValues(InstanceHeader, out var values)
                || !values.Contains(InstanceHeaderValue))
                return null;
            return ParseStatus(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Requests to our own listener. They go to the bound IP (127.0.0.1 unless --listen chose another)
/// with the canonical Host header: resolving "localhost" first tries [::1], and on Windows a refused
/// loopback connect takes about two seconds before falling back, which would stall every probe.
/// </summary>
internal static class LoopbackProbe
{
    public static HttpRequestMessage Get(LocalWorkbenchOptions options, string path)
    {
        // 127.0.0.1 is always bound (LocalWorkbenchOptions.ListenAddresses), except when --listen
        // chose another loopback address or dual-mode [::].
        var address = options.ListenAddress.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
            : IPAddress.IsLoopback(options.ListenAddress) ? options.ListenAddress
            : IPAddress.Loopback;
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{options.Port}{path}");
        request.Headers.Host = $"localhost:{options.Port}";
        return request;
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Local profile startup UX: once the host has started, print the banner, wait until the
/// canonical URL's /health answers, then (unless disabled) open the browser there once. On first
/// run, and for a workbench used without an account, the browser is opened through a one-time
/// ticket URL minted by this process (see <see cref="WorkbenchOwnerService"/>); with --no-browser
/// that URL is printed to this console instead. Tickets are never written to a log.
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

        BrowserLink link = new(_options.CanonicalUrl, BrowserLinkKind.Plain);
        try
        {
            link = await CreateBrowserLinkAsync(_scopes, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Thrown before any ticket exists, so the exception cannot carry one.
            _log.LogWarning(ex, "Could not prepare the one-time link for this workbench; run PartnerCenterBridge.exe again to get one.");
        }

        PrintBanner(link);
        if (!_options.OpenBrowser) return;

        if (await WaitForHealthAsync(_options, stoppingToken))
            BrowserLauncher.TryOpen(link.Url, _log);
        else
            _log.LogWarning("{Url}/health did not answer; not opening the browser. Open {Url} manually.",
                _options.CanonicalUrl, _options.CanonicalUrl);
    }

    /// <summary>
    /// The URL a browser opened by this process should use (see
    /// <see cref="WorkbenchOwnerService.CreateBrowserLinkAsync"/>): a one-time setup or sign-in
    /// ticket URL, or the plain canonical URL once the workbench has accounts.
    /// </summary>
    public static async Task<BrowserLink> CreateBrowserLinkAsync(IServiceScopeFactory scopes, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var owner = scope.ServiceProvider.GetRequiredService<WorkbenchOwnerService>();
        return await owner.CreateBrowserLinkAsync(scope.ServiceProvider.GetRequiredService<BridgeDbContext>(), ct);
    }

    /// <summary>Console lines describing a one-time link (never logged).</summary>
    public static IEnumerable<string> DescribeLink(BrowserLink link, string indent) => link.Kind switch
    {
        BrowserLinkKind.Setup => new[]
        {
            indent + "Set up:    " + link.Url,
            indent + "           (one-time link, valid for " + (int)WorkbenchOwnerService.SetupLifetime.TotalMinutes +
                " minutes; run " + HostingInfo.CommandName + " again for a new one)"
        },
        BrowserLinkKind.SignIn => new[]
        {
            indent + "Sign in:   " + link.Url,
            indent + "           (one-time link, valid for " + (int)WorkbenchOwnerService.SignInLifetime.TotalMinutes +
                " minutes; it signs in with full administrator rights -- do not share it)"
        },
        _ => Array.Empty<string>()
    };

    private void PrintBanner(BrowserLink link)
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
        // A ticket is printed only when no browser is opened, and only to this console -- never to a log.
        var extra = new List<string>();
        if (link.Kind == BrowserLinkKind.SignIn)
        {
            extra.Add("  Account:   none -- anyone who can run programs as " + Environment.UserName + " here can use it");
            if (_options.OpenBrowser)
                extra.Add("             run " + HostingInfo.CommandName + " again to open another signed-in window");
        }
        else if (link.Kind == BrowserLinkKind.Setup)
        {
            extra.Add("  Setup:     not finished -- create the first account (or choose no account) in the browser");
        }
        if (!_options.OpenBrowser) extra.AddRange(DescribeLink(link, "  "));
        lines.InsertRange(3, extra);
        if (!_options.IsLoopbackOnly)
        {
            lines.Insert(lines.Count - 2, $"  WARNING:   listening on {_options.ListenAddress}:{_options.Port} -- reachable from other machines.");
            _log.LogWarning("--listen {Address}: port {Port} is reachable from other machines (loopback stays bound for {Url}).",
                _options.ListenAddress, _options.Port, _options.CanonicalUrl);
        }
        foreach (var warning in _warnings.Messages)
        {
            lines.Insert(lines.Count - 1, "  Note:      " + warning);
            _log.LogWarning("{Note}", warning);
        }
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

/// <summary>
/// Opens the browser. The URL may carry a one-time ticket in its fragment, and a failed
/// <see cref="Process.Start(ProcessStartInfo)"/> puts the whole file name (the URL) into the
/// exception message, so neither the exception nor the URL ever reaches a logger: only the
/// exception type and native error code are logged. The URL goes to this console alone, so the
/// operator can still open it by hand.
/// </summary>
public static class BrowserLauncher
{
    public static void TryOpen(string url, ILogger? log = null, TextWriter? console = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            var nativeError = ex is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : (int?)null;
            log?.LogWarning("Could not open the browser ({ExceptionType}, native error {NativeError}); the link was printed to the console.",
                ex.GetType().Name, nativeError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
            (console ?? Console.Out).WriteLine(url.Contains("#ticket=", StringComparison.Ordinal)
                ? $"Could not open the browser; open this one-time link yourself: {url}"
                : $"Could not open the browser; open {url} yourself.");
        }
    }
}

public enum PortState { Free, InUse }

/// <summary>
/// Before the Local profile binds its port: is it free on every address we bind? If not, Program.cs
/// asks whoever holds it over the verified hand-off pipe (<see cref="LaunchHandOff"/>) whether it is
/// this user's own Partner Center Bridge. What answers on the HTTP port is never trusted as identity.
/// </summary>
public static class PortPreflight
{
    public static async Task<PortState> CheckAsync(LocalWorkbenchOptions options, CancellationToken ct = default)
    {
        // Browsers resolve "localhost" to ::1 first, so something on [::1]:port would shadow us even
        // if only our 127.0.0.1 bind were checked.
        if (options.ListenAddresses.All(address => CanBind(address, options.Port)) && !await AcceptsConnectionAsync(IPAddress.IPv6Loopback, options.Port, ct))
            return PortState.Free;
        return PortState.InUse;
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

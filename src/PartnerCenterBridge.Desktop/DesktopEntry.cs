using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Desktop;

public static class DesktopEntry
{
    [STAThread]
    public static int Main(string[] args)
    {
        var browser = args.Any(a => a.Equals("--browser", StringComparison.OrdinalIgnoreCase));
        var forwarded = args.Where(a => !a.Equals("--browser", StringComparison.OrdinalIgnoreCase)).ToArray();
        var parsed = CliParser.Parse(forwarded);
        var gui = !browser && parsed.Error is null && parsed.Command == CliCommand.Run
            && !parsed.NoBrowser && parsed.Port is null && parsed.Listen is null;
        if (!gui)
        {
            AttachParentConsole();
            return Program.RunAsync([.. forwarded, "--local"]).GetAwaiter().GetResult();
        }

        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var single = new Mutex(true, "Local\\PartnerCenterBridge.Desktop." + sid, out var first);
        if (!first)
        {
            var existing = FindWindow(null, "PartnerCenterBridge");
            if (existing != 0) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new DesktopWindow(forwarded, parsed.DataDir);
        app.Run(window);
        window.StopHost();
        return 0;
    }

    private static void AttachParentConsole()
    {
        // Preserve inherited output pipes for automation instead of replacing them with the
        // parent's console handles. A GUI executable may still require Start-Process -Wait
        // when a PowerShell caller needs its exit code.
        if (GetFileType(GetStdHandle(-11)) != 3) AttachConsole(-1);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
    [DllImport("kernel32")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32")] private static extern nint GetStdHandle(int standardHandle);
    [DllImport("kernel32")] private static extern uint GetFileType(nint handle);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? title);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern bool SetForegroundWindow(nint window);
}

public sealed class DesktopWindow : Window
{
    private readonly string[] args;
    private readonly string dataRoot;
    private readonly WebView2 browser = new();
    private readonly TextBlock status = new()
    {
        Text = "Starting PartnerCenterBridge...", Foreground = Brushes.White,
        FontSize = 18, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(36)
    };
    private Task<int>? hostTask;
    private IHostApplicationLifetime? hostLifetime;
    private Uri? origin;

    public DesktopWindow(string[] args, string? dataDir)
    {
        this.args = args;
        dataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? LocalWorkbenchOptions.DefaultDataRoot() : dataDir);
        Title = "PartnerCenterBridge";
        Width = 1320; Height = 850; MinWidth = 860; MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(7, 20, 38));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/brand/logo-128.png"));
        var content = new Grid();
        content.Children.Add(status);
        content.Children.Add(browser);
        browser.Visibility = Visibility.Collapsed;
        Content = content;
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        var stage = "local API and data store";
        try
        {
            // The desktop process owns the actual API host. Existing services, SQLite and auth run unchanged.
            var port = ChoosePort(dataRoot);
            var ready = DesktopHostSession.Prepare();
            hostTask = Task.Run(() => Program.RunAsync([.. args, "--local", "--no-browser",
                "--Hosting:Desktop=true", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            var completed = await Task.WhenAny(ready, hostTask, Task.Delay(TimeSpan.FromSeconds(60)));
            if (completed == hostTask)
            {
                await hostTask;
                throw new InvalidOperationException("The workbench host stopped before it was ready. See the logs for details.");
            }
            if (completed != ready)
                throw new TimeoutException("The local API did not start within 60 seconds. See the logs for details.");
            var running = await ready;
            hostLifetime = running.Services.GetRequiredService<IHostApplicationLifetime>();
            var local = running.Services.GetRequiredService<LocalWorkbenchOptions>();
            if (!await LocalWorkbenchLifetime.WaitForHealthAsync(local, CancellationToken.None))
                throw new InvalidOperationException("The local API did not become healthy. See the logs for details.");
            var link = await LocalWorkbenchLifetime.CreateBrowserLinkAsync(
                running.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
            origin = new Uri(running.Url);
            // Reuse an available origin across restarts so SPA localStorage/auth state remains
            // in the same WebView2 origin. This is a preference, never a fixed-port requirement.
            File.WriteAllText(Path.Combine(dataRoot, "desktop-port"), port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // One user profile per workbench data root keeps local session state across restarts.
            stage = "WebView2 desktop browser";
            var profile = Path.Combine(dataRoot, "webview2");
            Directory.CreateDirectory(profile);
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!DesktopNavigation.IsInternal(origin, e.Uri))
                {
                    e.Cancel = true;
                    DesktopNavigation.OpenExternal(e.Uri);
                }
            };
            browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (DesktopNavigation.IsInternal(origin, e.Uri)) browser.CoreWebView2.Navigate(e.Uri);
                else DesktopNavigation.OpenExternal(e.Uri);
            };
            browser.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Collapsed;
            browser.CoreWebView2.Navigate(link.Url);
        }
        catch (Exception ex)
        {
            browser.Visibility = Visibility.Collapsed;
            status.Visibility = Visibility.Visible;
            status.Text = ex is WebView2RuntimeNotFoundException
                ? "Microsoft Edge WebView2 Runtime is required. Install the Evergreen Runtime, then reopen PartnerCenterBridge."
                : "PartnerCenterBridge could not start its " + stage + ".\nError: " + ex.GetBaseException().GetType().Name
                    + "\n\nLogs: " + Path.Combine(dataRoot, "logs")
                    + "\nCheck that this folder is accessible and run doctor from a terminal for configuration diagnostics.";
            hostLifetime?.StopApplication();
        }
    }

    public void StopHost()
    {
        browser.Dispose();
        hostLifetime?.StopApplication();
        try { hostTask?.Wait(TimeSpan.FromSeconds(10)); } catch { /* startup failure is shown in the window */ }
    }

    private static int ChoosePort(string dataRoot)
    {
        var previous = Path.Combine(dataRoot, "desktop-port");
        if (File.Exists(previous) && int.TryParse(File.ReadAllText(previous), out var preferred)
            && preferred is > 1023 and <= 65535)
        {
            try
            {
                using var ipv4 = new TcpListener(IPAddress.Loopback, preferred);
                ipv4.Start();
                if (!LoopbackSupport.IPv6) return preferred;
                using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, preferred);
                ipv6.Start();
                return preferred;
            }
            catch (SocketException) { /* choose another available origin below */ }
        }
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var ipv4 = new TcpListener(IPAddress.Loopback, 0);
            ipv4.Start();
            var port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
            if (!LoopbackSupport.IPv6) return port;
            try
            {
                using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
                ipv6.Start();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No available loopback port was found.");
    }
}

public static class DesktopNavigation
{
    public static bool IsInternal(Uri? origin, string target) => origin is not null
        && Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp && uri.Host == origin.Host && uri.Port == origin.Port;

    public static void OpenExternal(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "mailto")) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}

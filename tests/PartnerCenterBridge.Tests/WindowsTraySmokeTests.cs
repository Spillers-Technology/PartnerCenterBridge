using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Tests;

public class WindowsTraySmokeTests
{
    public sealed class InteractiveTrayFactAttribute : FactAttribute
    {
        public InteractiveTrayFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("PCB_TRAY_SMOKE") != "1")
                Skip = "Set PCB_TRAY_SMOKE=1 in an interactive Windows session to test the real notification area.";
        }
    }

    [InteractiveTrayFact]
    public async Task Icon_is_registered_with_the_windows_shell_and_removed_on_stop()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var lifetime = new Lifetime();
        var tray = new WindowsTrayService(new HostingInfo(HostingProfile.Local,
                new LocalWorkbenchOptions { DataRoot = Path.GetTempPath(), OpenBrowser = true }),
            services.GetRequiredService<IServiceScopeFactory>(), lifetime,
            new ConfigurationBuilder().Build(), NullLogger<WindowsTrayService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await tray.StartAsync(timeout.Token);
        lifetime.Started.Cancel();
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Id = 1 };
        try
        {
            do
            {
                await Task.Delay(50, timeout.Token);
                identifier.Window = FindWindow("PartnerCenterBridgeTray-" + Environment.ProcessId, null);
            } while (identifier.Window == 0 || Shell_NotifyIconGetRect(ref identifier, out _) != 0);
            Assert.NotEqual(0, identifier.Window);
        }
        finally { await tray.StopAsync(CancellationToken.None); }
        Assert.NotEqual(0, Shell_NotifyIconGetRect(ref identifier, out _));
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        public CancellationTokenSource Started { get; } = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() { Started.Dispose(); stopping.Dispose(); stopped.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("shell32")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rect);
}

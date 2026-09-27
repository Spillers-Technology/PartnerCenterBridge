using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>A native notification-area host, without a Windows Desktop framework dependency.</summary>
public sealed class WindowsTrayService(HostingInfo hosting, IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime, IConfiguration config, ILogger<WindowsTrayService> log) : IHostedService
{
    private Thread? thread;
    private nint window;
    private nint icon;
    private WindowProc? callback;
    private NotifyData data;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint taskbarCreated;
    private const uint TrayMessage = 0x8001;

    public Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || hosting.Local is not { OpenBrowser: true } ||
            !Environment.UserInteractive || !config.GetValue("Hosting:Tray", true)) return Task.CompletedTask;
        lifetime.ApplicationStarted.Register(() =>
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Workbench tray" };
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        });
        return Task.CompletedTask;
    }

    private void Run()
    {
        try
        {
            callback = ProcessMessage;
            var className = "PartnerCenterBridgeTray-" + Environment.ProcessId;
            var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = callback,
                Instance = GetModuleHandle(null), ClassName = className };
            if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("Cannot register tray window.");
            window = CreateWindowEx(0, className, "PartnerCenterBridge", 0, 0, 0, 0, 0, 0, 0, wc.Instance, 0);
            if (window == 0) throw new InvalidOperationException("Cannot create tray window.");
            using var stream = typeof(WindowsTrayService).Assembly.GetManifestResourceStream("PartnerCenterBridge.AppIcon.ico");
            if (stream is not null)
            {
                // ICO directory: choose the last (largest) entry, containing PNG image bytes.
                using var reader = new BinaryReader(stream);
                reader.ReadUInt32();
                var count = reader.ReadUInt16();
                stream.Position = 6 + (count - 1) * 16 + 8;
                var size = reader.ReadUInt32();
                var offset = reader.ReadUInt32();
                stream.Position = offset;
                var bytes = reader.ReadBytes((int)size);
                icon = CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x30000, 32, 32, 0);
            }
            if (icon == 0) throw new InvalidOperationException("Cannot load workbench icon.");
            data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window, Id = 1,
                Flags = 7, CallbackMessage = TrayMessage, Icon = icon, Tip = "PartnerCenterBridge - running",
                Info = "", InfoTitle = "" };
            if (!Shell_NotifyIcon(0, ref data)) throw new InvalidOperationException("Windows notification area is unavailable.");
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            // Only detach a console owned solely by this process (Explorer launch), never hide a user's terminal.
            var processes = new uint[2];
            if (GetConsoleProcessList(processes, 2) == 1) FreeConsole();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Tray unavailable; the workbench remains available in the browser and console."); }
        finally
        {
            if (window != 0) { Shell_NotifyIcon(2, ref data); DestroyWindow(window); }
            if (icon != 0) DestroyIcon(icon);
            finished.TrySetResult();
        }
    }

    private nint ProcessMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (taskbarCreated != 0 && message == taskbarCreated) Shell_NotifyIcon(0, ref data);
        if (message == 0x10) { PostQuitMessage(0); return 0; }
        if (message == TrayMessage)
        {
            if ((uint)lParam == 0x203) _ = OpenAsync(); // double click
            if ((uint)lParam is 0x205 or 0x7B)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "Open workbench");
                    AppendMenu(menu, 0, 2, "Open logs");
                    AppendMenu(menu, 0x800, 0, null);
                    AppendMenu(menu, 0, 3, "Exit PartnerCenterBridge");
                    GetCursorPos(out var point);
                    SetForegroundWindow(hwnd);
                    var choice = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
                    if (choice == 1) _ = OpenAsync();
                    if (choice == 2) Process.Start(new ProcessStartInfo(hosting.Local!.LogsPath) { UseShellExecute = true })?.Dispose();
                    if (choice == 3) lifetime.StopApplication();
                    PostMessage(hwnd, 0, 0, 0);
                }
                catch (Exception ex) { log.LogWarning("Tray action failed ({Type}).", ex.GetType().Name); }
                finally { DestroyMenu(menu); }
            }
            return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private async Task OpenAsync()
    {
        try
        {
            var link = await LocalWorkbenchLifetime.CreateBrowserLinkAsync(scopes, lifetime.ApplicationStopping);
            BrowserLauncher.TryOpen(link.Url, log);
        }
        catch (Exception ex) { log.LogWarning("Could not open workbench from tray ({Type}).", ex.GetType().Name); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (thread is null) return;
        // Window creation may still be in progress immediately after startup.
        while (window == 0 && !finished.Task.IsCompleted) await Task.Delay(20, ct);
        if (window != 0) PostMessage(window, 0x10, 0, 0);
        await finished.Task.WaitAsync(ct);
    }

    private delegate nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass { public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32")] private static extern uint GetConsoleProcessList(uint[] ids, uint count);
    [DllImport("kernel32")] private static extern bool FreeConsole();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32")] private static extern int GetMessage(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32")] private static extern nint DispatchMessage(ref Message msg);
    [DllImport("user32")] private static extern bool PostMessage(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32")] private static extern void PostQuitMessage(int code);
    [DllImport("user32")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32")] private static extern nint CreateIconFromResourceEx(byte[] bytes, uint size, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyData data);
    [DllImport("user32")] private static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
}

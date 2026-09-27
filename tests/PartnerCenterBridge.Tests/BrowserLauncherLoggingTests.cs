using Microsoft.Extensions.Logging;
using PartnerCenterBridge.Api.Hosting;

namespace PartnerCenterBridge.Tests;

/// <summary>
/// Finding 4: a failed browser launch must not write the one-time ticket (which .NET puts into the
/// Process.Start exception message along with the whole URL) to any log.
/// </summary>
public sealed class BrowserLauncherLoggingTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "pcb-browserlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); }
        catch (IOException) { }
    }

    private sealed class MemoryLogger : ILogger, ILoggerProvider
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Lines.AddRange(values.Select(pair => $"{pair.Key}={pair.Value}"));
            if (exception is not null) Lines.Add(exception.ToString());
        }
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
    }

    [Fact]
    public void Failed_browser_launch_logs_no_ticket_anywhere()
    {
        if (!OperatingSystem.IsWindows()) return; // the failing shell-execute below is Windows-specific
        const string ticket = "RECOGNIZABLE_TICKET_0123456789abcdefghijklmnopq";
        // A target the shell cannot open: Process.Start throws, with the whole "URL" in its message.
        var url = $@"C:\pcb-no-such-dir-{Guid.NewGuid():N}\open#ticket={ticket}";
        var memory = new MemoryLogger();
        var console = new StringWriter();
        using (var fileProvider = new FileLoggerProvider(_logDir))
        using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace)
                   .AddProvider(fileProvider).AddProvider(memory)))
        {
            BrowserLauncher.TryOpen(url, factory.CreateLogger("test"), console);
        }

        var fileText = string.Concat(Directory.GetFiles(_logDir).Select(File.ReadAllText));
        Assert.Contains("Could not open the browser", fileText); // the failure itself is logged ...
        Assert.Contains("Win32Exception", fileText);              // ... with its type
        Assert.DoesNotContain(ticket, fileText);
        Assert.NotEmpty(memory.Lines);
        Assert.All(memory.Lines, line => Assert.DoesNotContain(ticket, line));
        Assert.All(memory.Lines, line => Assert.DoesNotContain("pcb-no-such-dir", line));
        // The operator still sees the link in the console to open it by hand.
        Assert.Contains(ticket, console.ToString());
    }
}

using System.Globalization;
using System.Text;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Minimal daily-rolling file logger for the Local Workbench (logs/pcb-yyyyMMdd.log under the data
/// root), so a double-clicked exe still leaves a trail after its console window closes. Level
/// filtering comes from the normal Logging configuration (alias <c>File</c>). Files older than
/// <see cref="RetentionDays"/> are removed at startup.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;

    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || today != _writerDate)
                {
                    _writer?.Dispose();
                    var path = Path.Combine(_directory, $"pcb-{today:yyyyMMdd}.log");
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = today;
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never take the app down (disk full, file locked by an editor...).
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "pcb-*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(' ').Append(Level(logLevel)).Append(' ').Append(_category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null) builder.AppendLine().Append(exception);
            _provider.Write(builder.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "NONE"
        };
    }
}

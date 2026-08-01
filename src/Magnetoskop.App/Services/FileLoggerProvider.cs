using System.IO;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>
/// Minimal rolling file logger: one file per day under the given directory,
/// files older than <see cref="KeepDays"/> deleted at startup. Writes are
/// serialized with a lock; volume is low (Information and above).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeepDays = 14;

    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _failed;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        PruneOldLogs();
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagnetoskopCapture", "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => !_failed && level >= _minLevel;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Abbreviate(level)}] {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            if (_failed) return;
            try
            {
                var today = DateOnly.FromDateTime(now);
                if (_writer is null || today != _writerDate)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    var path = Path.Combine(_directory, $"magnetoskop_{today:yyyyMMdd}.log");
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    { AutoFlush = true };
                    _writerDate = today;
                }
                _writer.WriteLine(line);
            }
            catch (Exception)
            {
                // File logging must never take the app down; disable on first failure.
                _failed = true;
            }
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "magnetoskop_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
        lock (_sync)
        {
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

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}

using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.App.Services;

/// <summary>
/// Opt-in per-instance debug file logger. When <see cref="Enabled"/> is true,
/// writes Debug+ messages to a unique file under <see cref="LogDirectory"/>.
/// Never throws into the application.
/// </summary>
public sealed class DebugSessionFileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly LogLevel _minLevel;
    private StreamWriter? _writer;
    private string? _currentPath;
    private bool _enabled;
    private bool _failed;

    public DebugSessionFileLoggerProvider(LogLevel minLevel = LogLevel.Debug)
    {
        _minLevel = minLevel;
    }

    /// <summary>Directory for session logs: {AppBase}/logs.</summary>
    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
    }

    /// <summary>Path of the active session log, if any.</summary>
    public string? CurrentLogPath
    {
        get { lock (_sync) return _currentPath; }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (!enabled)
            {
                CloseWriter_NoLock();
            }
            else
            {
                _failed = false;
                // Open immediately so the file exists even before the first log line.
                EnsureWriter_NoLock();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new DebugFileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level)
    {
        lock (_sync)
        {
            return _enabled && !_failed && level >= _minLevel;
        }
    }

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
            if (!_enabled || _failed) return;
            try
            {
                EnsureWriter_NoLock();
                _writer!.WriteLine(line);
            }
            catch (Exception)
            {
                _failed = true;
                CloseWriter_NoLock();
            }
        }
    }

    private void EnsureWriter_NoLock()
    {
        if (_writer is not null) return;

        Directory.CreateDirectory(LogDirectory);
        var fileName = $"magnetoskop_debug_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log";
        _currentPath = Path.Combine(LogDirectory, fileName);
        _writer = new StreamWriter(
            new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };

        _writer.WriteLine(
            $"# Magnetoskop Capture debug log — started {DateTime.Now:O}, pid {Environment.ProcessId}");
        try
        {
            var proc = Process.GetCurrentProcess();
            _writer.WriteLine($"# BaseDirectory: {AppContext.BaseDirectory}");
            _writer.WriteLine($"# Process: {proc.ProcessName}");
        }
        catch (Exception)
        {
            // Header extras are best-effort.
        }
    }

    private void CloseWriter_NoLock()
    {
        try { _writer?.Dispose(); } catch (Exception) { }
        _writer = null;
        _currentPath = null;
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
            _enabled = false;
            CloseWriter_NoLock();
        }
    }

    private sealed class DebugFileLogger : ILogger
    {
        private readonly DebugSessionFileLoggerProvider _provider;
        private readonly string _category;

        public DebugFileLogger(DebugSessionFileLoggerProvider provider, string category)
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

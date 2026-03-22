using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MinoLink.Desktop.Services;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileLogSink _sink;

    public FileLoggerProvider(string logDirectoryPath)
    {
        _sink = new FileLogSink(logDirectoryPath);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _sink));

    public void Dispose()
    {
        _loggers.Clear();
        _sink.Dispose();
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLogSink _sink;

    public FileLogger(string categoryName, FileLogSink sink)
    {
        _categoryName = categoryName;
        _sink = sink;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
            return;

        _sink.Write(logLevel, _categoryName, eventId, message, exception);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

internal sealed class FileLogSink : IDisposable
{
    private readonly string _logDirectoryPath;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private bool _disposed;

    public FileLogSink(string logDirectoryPath)
    {
        _logDirectoryPath = logDirectoryPath;
    }

    public void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureWriter();

            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            _writer!.Write('[');
            _writer.Write(timestamp);
            _writer.Write("] [");
            _writer.Write(logLevel);
            _writer.Write("] [");
            _writer.Write(categoryName);
            _writer.Write("]");

            if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
            {
                _writer.Write(" [");
                _writer.Write(eventId.Id);
                if (!string.IsNullOrWhiteSpace(eventId.Name))
                {
                    _writer.Write(':');
                    _writer.Write(eventId.Name);
                }
                _writer.Write(']');
            }

            _writer.Write(' ');
            _writer.WriteLine(message);

            if (exception is not null)
                _writer.WriteLine(exception);

            _writer.Flush();
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _currentDate == today)
            return;

        Directory.CreateDirectory(_logDirectoryPath);
        _writer?.Dispose();

        _currentDate = today;
        var filePath = Path.Combine(_logDirectoryPath, $"MinoLink-{today:yyyyMMdd}.log");
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream)
        {
            AutoFlush = true,
        };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FileLogSink));
    }
}

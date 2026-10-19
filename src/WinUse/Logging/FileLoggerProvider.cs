using System.Text;
using Microsoft.Extensions.Logging;

namespace WinUse.Logging;

/// <summary>Appends log lines to <c>{directory}/winuse-{yyyyMMdd}.log</c>. Never throws.</summary>
public sealed class FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information) : ILoggerProvider
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _lock = new();
    private readonly LogLevel _minLevel = minLevel;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"winuse-{DateTime.Now:yyyyMMdd}.log"), line, Utf8);
            }
            catch
            {
                // Logging must never break the server.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(' ').Append(Short(logLevel))
                .Append(' ').Append(category)
                .Append(": ").Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null) sb.AppendLine(exception.ToString());
            owner.Write(sb.ToString());
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            _ => "CRIT",
        };
    }
}

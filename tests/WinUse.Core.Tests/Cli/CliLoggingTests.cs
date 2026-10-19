using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WinUse.Tools;

namespace WinUse.Core.Tests.Cli;

public sealed class CliLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winuse-log-" + Guid.NewGuid().ToString("N"));

    public CliLoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(RecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines) owner.Lines.Add(formatter(state, exception));
            }
        }
    }

    private static JsonObject WriteArgs(string path) => new() { ["path"] = path, ["text"] = "x" };

    [Fact]
    public async Task ToolContext_UsesTheProvidedLoggerFactory_ForStepLogs()
    {
        var rec = new RecordingProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(rec));
        using var ctx = ToolContext.Create(TestConfig.WithRoots(_dir), lf);

        var o = await new ToolDispatcher(ctx).InvokeAsync(new("write_text", WriteArgs(Path.Combine(_dir, "a.txt")), null), default);

        Assert.True(o.Ok, o.Error);
        Assert.Contains(rec.Lines, l => l.Contains("write_text"));
    }

    [Fact]
    public async Task CliLoggerFactory_WritesStepLogsToTheLogFile()
    {
        var logDir = Path.Combine(_dir, "logs");
        var cfg = TestConfig.WithRoots(_dir) with { LogDirectory = logDir };
        using (var lf = Program.CreateCliLoggerFactory(cfg))
        using (var ctx = ToolContext.Create(cfg, lf))
        {
            var o = await new ToolDispatcher(ctx).InvokeAsync(new("write_text", WriteArgs(Path.Combine(_dir, "b.txt")), null), default);
            Assert.True(o.Ok, o.Error);
        }
        var log = Assert.Single(Directory.GetFiles(logDir, "winuse-*.log"));
        Assert.Contains("write_text", File.ReadAllText(log));
    }
}

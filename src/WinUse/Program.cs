using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinUse.Core.Config;
using WinUse.Logging;
using WinUse.Tools;

namespace WinUse;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--mcp":
                await RunMcpAsync(LoadConfig());
                return 0;
            case "run":
            case "doctor":
                // Implemented by the CLI task.
                Console.Error.WriteLine($"winuse {args[0]}: not implemented yet");
                return Usage();
            default:
                return Usage();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: winuse --mcp | winuse run <scenario.json> [--heal] [--report <dir>] | winuse doctor");
        return 2;
    }

    private static WinUseConfig LoadConfig()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;
        return WinUseConfig.Load(Environment.CurrentDirectory, AppContext.BaseDirectory, env);
    }

    /// <summary>MCP server over stdio. stdout carries only JSON-RPC; all logs go to stderr and the log file.</summary>
    private static async Task RunMcpAsync(WinUseConfig cfg)
    {
        // Command-line args are not passed to the host: "--mcp" is not a configuration key.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddProvider(new FileLoggerProvider(cfg.LogDirectory));
        builder.Services.AddSingleton(cfg);
        builder.Services.AddSingleton(sp => ToolContext.Create(cfg, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<ObserveTools>()
            .WithTools<IntentTools>()
            .WithTools<InputTools>()
            .WithTools<ElementTools>()
            .WithTools<WindowTools>()
            .WithTools<WaitTools>()
            .WithTools<ProcessTools>()
            .WithTools<FileTools>()
            .WithTools<AssertTools>()
            .WithTools<ScenarioTools>();
        await builder.Build().RunAsync();
    }
}

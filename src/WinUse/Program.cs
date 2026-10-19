using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinUse.Cli;
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
                return await RunAsync(args);
            case "doctor":
                using (var ctx = ToolContext.Create(LoadConfig()))
                    return await DoctorCommand.ExecuteAsync(ctx, Console.Out);
            default:
                return Usage();
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? path = null, report = null;
        var heal = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--heal": heal = true; break;
                case "--report" when i + 1 < args.Length: report = args[++i]; break;
                case var a when !a.StartsWith("--") && path is null: path = a; break;
                default: return Usage();
            }
        }
        if (path is null) return Usage();
        using var ctx = ToolContext.Create(LoadConfig());
        return await RunCommand.ExecuteAsync(path, heal, report, ctx);
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

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Processes;
using WinUse.Core.Refs;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ProcessTools
{
    private static readonly TimeSpan CdpConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitWindowTimeout = TimeSpan.FromSeconds(30);

    [McpServerTool(Name = "process_run")]
    [Description("Start a program. With webview_debug=true a free CDP port is passed to WebView2 and get_dom/w{n} refs become available once connected. With wait_window, waits (30 s) for a window whose title matches.")]
    public static Task<CallToolResult> ProcessRun(
        ToolContext ctx,
        [Description("Executable path.")] string path,
        [Description("Command-line arguments as one string.")] string? args = null,
        [Description("Working directory.")] string? cwd = null,
        [Description("Extra environment variables (JSON object of name to value).")] Dictionary<string, string>? env = null,
        [Description("Enable the WebView2 DevTools port and connect to it.")] bool webview_debug = false,
        [Description("Title regex of a window to wait for after start.")] string? wait_window = null,
        [Description("Refuse to start when a process with the same name is already running (needed for Office).")] bool require_fresh = false) =>
        ctx.RunStepAsync("process_run", ToolContext.Args(("path", path), ("args", args), ("cwd", cwd), ("env", env),
            ("webview_debug", webview_debug), ("wait_window", wait_window), ("require_fresh", require_fresh)), async _ =>
        {
            var run = ctx.Processes.Run(new RunSpec(path, args, cwd, env, webview_debug, require_fresh));
            object? window = null;
            if (!string.IsNullOrEmpty(wait_window))
            {
                var w = await ctx.Windows.WaitForAsync(new WindowLocator(wait_window, null, null), WaitWindowTimeout)
                        ?? throw new TimeoutException($"Process {run.Pid} started but no window matching '{wait_window}' appeared within {WaitWindowTimeout.TotalSeconds:0} s.");
                ctx.LastWindow = w.Hwnd;
                ctx.Web.Window = ToolContext.LocatorOf(w);
                window = new { @ref = ctx.RegisterWindow(w).Id, title = w.Title };
            }
            bool? cdpConnected = null;
            string? cdpError = null;
            if (run.CdpPort is { } port)
            {
                try
                {
                    await ctx.Web.ConnectAsync(port, CdpConnectTimeout);
                    cdpConnected = true;
                }
                catch (Exception ex)
                {
                    ctx.Log.LogWarning(ex, "CDP connect to port {Port} failed", port);
                    cdpConnected = false;
                    cdpError = ex.Message;
                }
            }
            return new
            {
                pid = run.Pid,
                process_name = run.ProcessName,
                cdp_port = run.CdpPort,
                cdp_connected = cdpConnected,
                cdp_error = cdpError,
                window,
            };
        });

    [McpServerTool(Name = "process_kill", Destructive = true)]
    [Description("Kill a process started by winuse (by pid) or one listed in WINUSE_KILLABLE_PROCESSES (by name).")]
    public static Task<CallToolResult> ProcessKill(
        ToolContext ctx,
        [Description("Process name (with or without .exe) or pid.")] string name_or_pid) =>
        ctx.RunStepAsync("process_kill", ToolContext.Args(("name_or_pid", name_or_pid)), _ =>
            Task.FromResult<object>(new { killed = ctx.Processes.Kill(name_or_pid) }));

    [McpServerTool(Name = "process_exists", ReadOnly = true)]
    [Description("Check whether a process with the given name is running.")]
    public static Task<CallToolResult> ProcessExists(
        ToolContext ctx,
        [Description("Process name (with or without .exe).")] string name) =>
        ctx.RunStepAsync("process_exists", ToolContext.Args(("name", name)), _ =>
            Task.FromResult<object>(new { exists = ctx.Processes.Exists(name) }));
}

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Processes;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ProcessTools
{
    private static readonly TimeSpan CdpConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitWindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitWindowPoll = TimeSpan.FromMilliseconds(200);

    [McpServerTool(Name = "process_run")]
    [Description("Start a program. With webview_debug=true a free CDP port is passed to WebView2 and get_dom/w{n} refs become available once connected. With wait_window, waits (30 s) for a window of the started process (same pid or process name) whose title matches; if none appears, falls back to any window with a matching title and reports window.matched_by=\"title-only\".")]
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
            var run = ctx.Processes.Run(new RunSpec(path, args, cwd, env, webview_debug, require_fresh, DetachStdio: true));
            object? window = null;
            if (!string.IsNullOrEmpty(wait_window))
            {
                var sw = Stopwatch.StartNew();
                (WindowInfo? Window, string? MatchedBy) pick;
                while (true)
                {
                    var timedOut = sw.Elapsed >= WaitWindowTimeout;
                    pick = PickStartedWindow(await ctx.Windows.ListAsync(), wait_window, run.Pid, run.ProcessName, allowTitleOnly: timedOut);
                    if (pick.Window is not null || timedOut) break;
                    await Task.Delay(WaitWindowPoll);
                }
                var w = pick.Window
                        ?? throw new TimeoutException($"Process {run.Pid} started but no window matching '{wait_window}' appeared within {WaitWindowTimeout.TotalSeconds:0} s.");
                ctx.LastWindow = w.Hwnd;
                ctx.Web.Window = ToolContext.LocatorOf(w);
                window = new { @ref = ctx.RegisterWindow(w).Id, title = w.Title, matched_by = pick.MatchedBy };
            }
            bool? cdpConnected = null;
            string? cdpError = null;
            if (run.CdpPort is { } port)
            {
                try
                {
                    await ctx.Web.ConnectAsync(port, CdpConnectTimeout);
                    cdpConnected = true;
                    ctx.WebOwnerPid = run.Pid;
                    ctx.WebOwnerProcessName = run.ProcessName;
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

    /// <summary>
    /// Picks the window <c>process_run wait_window</c> waits for: a title match owned by the started pid, else by a
    /// process with the started process name. Only with <paramref name="allowTitleOnly"/> (after the timeout) is any
    /// title match accepted. <c>MatchedBy</c> is "pid", "process" or "title-only".
    /// </summary>
    public static (WindowInfo? Window, string? MatchedBy) PickStartedWindow(IReadOnlyList<WindowInfo> windows, string titleRegex,
        int pid, string processName, bool allowTitleOnly)
    {
        var loc = new WindowLocator(titleRegex, null, null);
        var titled = windows.Where(w => WindowService.Matches(w, loc)).ToList();
        if (titled.FirstOrDefault(w => w.Pid == pid) is { } byPid) return (byPid, "pid");
        if (titled.FirstOrDefault(w => WindowService.ProcessMatches(w.ProcessName, processName)) is { } byName) return (byName, "process");
        return allowTitleOnly && titled.Count > 0 ? (titled[0], "title-only") : (null, null);
    }

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

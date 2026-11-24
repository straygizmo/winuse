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

    [McpServerTool(Name = "process_run", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Start a program. With webview_debug=true a free CDP port is passed to WebView2 and get_dom/w{n} refs become available once connected. With wait_window, waits (30 s) for a window of the started process (same pid or process name) whose title matches; if none appears, falls back to any window with a matching title and reports window.matched_by=\"title-only\". Java processes are started with accessibility enabled (JAVA_TOOL_OPTIONS) so their Swing windows can be read, unless WINUSE_JAVA_ACCESSIBILITY=off. With wait_exit=true the program is run to its end instead (a command-line tool, a script): the result carries exit_code, stdout, stderr (the last max_output_chars characters of each; *_truncated when cut, *_chars the full length), elapsed_ms and timed_out (after timeout_ms the process tree is killed and exit_code is null). Its stdin is closed and no console window is shown.")]
    public static Task<CallToolResult> ProcessRun(
        ToolContext ctx,
        [Description("Executable path.")] string path,
        [Description("Command-line arguments as one string.")] string? args = null,
        [Description("Working directory.")] string? cwd = null,
        [Description("Extra environment variables (JSON object of name to value).")] Dictionary<string, string>? env = null,
        [Description("Enable the WebView2 DevTools port and connect to it.")] bool webview_debug = false,
        [Description("Title regex of a window to wait for after start.")] string? wait_window = null,
        [Description("Refuse to start when a process with the same name is already running (needed for Office).")] bool require_fresh = false,
        [Description("Wait for the program to exit and return exit_code, stdout and stderr. Not combinable with wait_window or webview_debug.")] bool wait_exit = false,
        [Description("wait_exit: milliseconds to wait before the process tree is killed (1000 to 600000).")] int timeout_ms = 60_000,
        [Description("wait_exit: characters of stdout and of stderr to return at most (the end of the output is kept).")] int max_output_chars = 20_000,
        [Description("wait_exit: how the output is decoded: \"auto\" (UTF-8 when the bytes are valid UTF-8, else the OEM code page that console programs such as cmd print in), \"utf-8\", \"oem\", or a code page number / encoding name such as \"932\" or \"shift_jis\".")] string output_encoding = "auto") =>
        ctx.RunStepAsync("process_run", ToolContext.Args(("path", path), ("args", args), ("cwd", cwd), ("env", env),
            ("webview_debug", webview_debug), ("wait_window", wait_window), ("require_fresh", require_fresh),
            ("wait_exit", wait_exit ? true : null), ("timeout_ms", wait_exit ? timeout_ms : null),
            ("max_output_chars", wait_exit ? max_output_chars : null), ("output_encoding", wait_exit && output_encoding != "auto" ? output_encoding : null)), async _ =>
        {
            if (wait_exit)
            {
                if (webview_debug || !string.IsNullOrEmpty(wait_window))
                    throw new ArgumentException("wait_exit cannot be combined with wait_window or webview_debug: it runs the program to its end.");
                if (timeout_ms is < 1000 or > 600_000) throw new ArgumentOutOfRangeException(nameof(timeout_ms), "timeout_ms must be between 1000 and 600000.");
                if (max_output_chars < 0) throw new ArgumentOutOfRangeException(nameof(max_output_chars), "max_output_chars must be 0 or more.");
                var o = await ctx.Processes.RunToExitAsync(new RunSpec(path, args, cwd, env, false, require_fresh),
                    TimeSpan.FromMilliseconds(timeout_ms), max_output_chars, OutputEncoding.Parse(output_encoding));
                return (object)new
                {
                    pid = o.Pid,
                    process_name = o.ProcessName,
                    exit_code = o.ExitCode,
                    timed_out = o.TimedOut,
                    elapsed_ms = o.ElapsedMs,
                    stdout = o.Stdout,
                    stderr = o.Stderr,
                    stdout_chars = o.StdoutChars,
                    stderr_chars = o.StderrChars,
                    stdout_truncated = o.StdoutTruncated ? true : (bool?)null,
                    stderr_truncated = o.StderrTruncated ? true : (bool?)null,
                };
            }
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

    [McpServerTool(Name = "process_kill", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Kill a process started by winuse (by pid) or one listed in WINUSE_KILLABLE_PROCESSES (by name).")]
    public static Task<CallToolResult> ProcessKill(
        ToolContext ctx,
        [Description("Process name (with or without .exe) or pid.")] string name_or_pid) =>
        ctx.RunStepAsync("process_kill", ToolContext.Args(("name_or_pid", name_or_pid)), _ =>
            Task.FromResult<object>(new { killed = ctx.Processes.Kill(name_or_pid) }));

    [McpServerTool(Name = "process_exists", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Check whether a process with the given name is running.")]
    public static Task<CallToolResult> ProcessExists(
        ToolContext ctx,
        [Description("Process name (with or without .exe).")] string name) =>
        ctx.RunStepAsync("process_exists", ToolContext.Args(("name", name)), _ =>
            Task.FromResult<object>(new { exists = ctx.Processes.Exists(name) }));
}

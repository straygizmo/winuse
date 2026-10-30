using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    [McpServerTool(Name = "window_activate", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Bring a window to the foreground (restoring it when minimized).")]
    public static Task<CallToolResult> WindowActivate(
        ToolContext ctx,
        [Description("Window ref (win{n}). Either ref or title is required.")] string? @ref = null,
        [Description("Window title regex.")] string? title = null) =>
        OnWindow(ctx, "window_activate", ToolContext.Args(("ref", @ref), ("title", title)), @ref, title, async w =>
        {
            var ok = await ctx.Windows.ActivateAsync(w.Hwnd);
            if (!ok) throw new InvalidOperationException($"Could not activate window '{w.Title}'.");
            return new { activated = true };
        });

    [McpServerTool(Name = "window_close", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Ask a window to close (WM_CLOSE) and wait up to timeout_ms for it to go away. The result separates the request from the effect: close_requested is true once WM_CLOSE was posted; closed is true and window_exists_after_request false only when the window is gone (process_alive then says whether its process is still running). Otherwise closed is false with reason \"still_open\", window_exists_after_request true, and the windows the app opened in the meantime (an exit confirmation, a save prompt) are listed in new_windows with refs (same process only), the current foreground window in foreground, and hint says what to do: read the prompt with get_ui_tree on its ref (or on the same window, where a modal dialog appears as a focused child Window), answer it, then wait_for window_closed.")]
    public static Task<CallToolResult> WindowClose(
        ToolContext ctx,
        [Description("Window ref (win{n}). Either ref or title is required.")] string? @ref = null,
        [Description("Window title regex.")] string? title = null,
        [Description("How long to wait for the window to go away, in milliseconds.")] int timeout_ms = 2000) =>
        OnWindow(ctx, "window_close", ToolContext.Args(("ref", @ref), ("title", title), ("timeout_ms", timeout_ms)), @ref, title, async w =>
        {
            var before = (await ctx.Windows.ListAsync()).Where(x => x.Pid == w.Pid).Select(x => x.Hwnd).ToHashSet();
            if (!await ctx.Windows.CloseAsync(w.Hwnd)) throw new InvalidOperationException($"Could not post WM_CLOSE to window '{w.Title}'.");
            var gone = await ctx.Windows.WaitGoneAsync(w.Hwnd, TimeSpan.FromMilliseconds(Math.Max(0, timeout_ms)));
            var opened = (await ctx.Windows.ListAsync()).Where(x => x.Pid == w.Pid && !before.Contains(x.Hwnd)).ToList();
            var fg = gone ? null : await ctx.Windows.ForegroundAsync();
            return new
            {
                close_requested = true,
                closed = gone,
                window_exists_after_request = !gone,
                reason = gone ? null : "still_open",
                process_alive = gone ? ctx.Processes.IsAlive(w.Pid) : (bool?)null,
                foreground = fg is null ? null : ObserveTools.WindowJson(ctx.RegisterWindow(fg).Id, fg),
                new_windows = opened.Select(x => ObserveTools.WindowJson(ctx.RegisterWindow(x).Id, x)).ToList(),
                hint = gone ? null
                    : opened.Count > 0 || (fg is not null && fg.Pid == w.Pid && fg.Hwnd != w.Hwnd)
                        ? "The window is still there and the app opened another window after the close request: most likely a confirmation or save prompt. Read it with get_ui_tree (its ref from new_windows / foreground, or this window, where a modal dialog is a focused child Window), answer it, then wait_for {condition: \"window_closed\"} on this window."
                        : "The window is still there after the timeout and no new window appeared: the app may be busy or ignore WM_CLOSE. Take a screenshot, or retry with a longer timeout_ms.",
            };
        });

    [McpServerTool(Name = "window_set_state", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Set a window to normal, minimized or maximized.")]
    public static Task<CallToolResult> WindowSetState(
        ToolContext ctx,
        [Description("Window ref (win{n}). Either ref or title is required.")] string? @ref = null,
        [Description("Window title regex.")] string? title = null,
        [Description("normal, minimized or maximized.")] string state = "normal") =>
        OnWindow(ctx, "window_set_state", ToolContext.Args(("ref", @ref), ("title", title), ("state", state)), @ref, title, async w =>
        {
            var s = state.ToLowerInvariant() switch
            {
                "normal" => WindowState.Normal,
                "minimized" => WindowState.Minimized,
                "maximized" => WindowState.Maximized,
                _ => throw new ArgumentException($"Unknown state '{state}'. Use normal, minimized or maximized."),
            };
            await ctx.Windows.SetStateAsync(w.Hwnd, s);
            return new { state = s.ToString().ToLowerInvariant() };
        });

    [McpServerTool(Name = "window_move", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Move and resize a window (screen coordinates, physical pixels).")]
    public static Task<CallToolResult> WindowMove(
        ToolContext ctx,
        [Description("Left edge.")] int x,
        [Description("Top edge.")] int y,
        [Description("Width.")] int width,
        [Description("Height.")] int height,
        [Description("Window ref (win{n}). Either ref or title is required.")] string? @ref = null,
        [Description("Window title regex.")] string? title = null) =>
        OnWindow(ctx, "window_move", ToolContext.Args(("ref", @ref), ("title", title), ("x", x), ("y", y), ("width", width), ("height", height)),
            @ref, title, async w =>
        {
            var rect = new Rect(x, y, width, height);
            if (!await ctx.Windows.MoveAsync(w.Hwnd, rect)) throw new InvalidOperationException($"Could not move window '{w.Title}'.");
            return new { rect };
        });

    [McpServerTool(Name = "window_exists", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Check whether a visible top-level window with a matching title (and process) exists.")]
    public static Task<CallToolResult> WindowExists(
        ToolContext ctx,
        [Description("Window title regex.")] string title,
        [Description("Optional process name.")] string? process = null) =>
        ctx.RunStepAsync("window_exists", ToolContext.Args(("title", title), ("process", process)), async _ =>
        {
            var w = await ctx.Windows.FindAsync(new WindowLocator(title, process, null));
            return new { exists = w is not null, window = w is null ? null : new { @ref = ctx.RegisterWindow(w).Id, title = w.Title } };
        });

    private static Task<CallToolResult> OnWindow(ToolContext ctx, string tool, System.Text.Json.Nodes.JsonObject args,
        string? refId, string? title, Func<WindowInfo, Task<object>> body) =>
        ctx.RunStepAsync(tool, args, async step =>
        {
            var w = await ctx.ResolveWindowAsync(refId, title, fallback: false);
            if (refId is not null) step.Resolved = ctx.GetRef(refId).Locator;
            ctx.LastWindow = w.Hwnd;
            var payload = await body(w);
            return new { window = new { @ref = refId ?? ctx.RegisterWindow(w).Id, title = w.Title }, result = payload };
        });
}

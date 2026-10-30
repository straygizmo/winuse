using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private const int WebScrollPixelsPerNotch = 100;

    private const string WindowArg = "Window ref (win{n}) or title regex. When given, the window is activated first and x,y are relative to its rect (the origin a window screenshot reports), so an image pixel of screenshot(window) can be passed as x,y directly; without it x,y are screen coordinates.";
    private const string InputWindowArg = "Window ref (win{n}) or title regex to activate before sending the input; fails when the window does not exist or cannot take the foreground. Without it the input goes to whichever window has focus.";

    [McpServerTool(Name = "click", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Click an element by ref (UIA: centre of its rectangle plus offset; DOM: Playwright click) or at coordinates x,y (physical pixels; screen coordinates, or window-relative when window is given).")]
    public static Task<CallToolResult> Click(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n}) or window ref (win{n}). Either ref or x,y is required.")] string? @ref = null,
        [Description("x coordinate (physical pixels).")] int? x = null,
        [Description("y coordinate (physical pixels).")] int? y = null,
        [Description("left, right or middle.")] string button = "left",
        [Description("Number of clicks (2 = double click).")] int count = 1,
        [Description("Horizontal offset from the target point.")] int offset_x = 0,
        [Description("Vertical offset from the target point.")] int offset_y = 0,
        [Description(WindowArg)] string? window = null) =>
        ctx.RunStepAsync("click", ToolContext.Args(("ref", @ref), ("x", x), ("y", y), ("button", button), ("count", count),
            ("offset_x", offset_x), ("offset_y", offset_y), ("window", window)), async step =>
        {
            var b = ParseButton(button);
            var target = await ResolveTargetWindowAsync(ctx, window);
            if (@ref is not null)
            {
                var r = ctx.GetRef(@ref);
                step.Resolved = r.Locator;
                if (r.Kind == RefKind.Dom)
                {
                    await ctx.Web.ClickAsync(r.Id, b, count, offset_x, offset_y);
                    return new { clicked = r.Id, window = target?.Json };
                }
                if (target is null) await ctx.ActivateOwnerAsync(r);
                var (cx, cy) = await ctx.CenterOfAsync(r);
                await ctx.Input.ClickAsync(cx + offset_x, cy + offset_y, b, count);
                return new { clicked = r.Id, x = cx + offset_x, y = cy + offset_y, window = target?.Json };
            }
            var (px, py) = ToScreen(target, RequirePoint(x, y, "ref or x,y"));
            await ctx.Input.ClickAsync(px + offset_x, py + offset_y, b, count);
            return new { x = px + offset_x, y = py + offset_y, window = target?.Json };
        });

    [McpServerTool(Name = "hover", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Move the mouse over an element (ref) or to coordinates x,y (screen, or window-relative when window is given).")]
    public static Task<CallToolResult> Hover(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n}) or window ref. Either ref or x,y is required.")] string? @ref = null,
        [Description("x coordinate (physical pixels).")] int? x = null,
        [Description("y coordinate (physical pixels).")] int? y = null,
        [Description(WindowArg)] string? window = null) =>
        ctx.RunStepAsync("hover", ToolContext.Args(("ref", @ref), ("x", x), ("y", y), ("window", window)), async step =>
        {
            var target = await ResolveTargetWindowAsync(ctx, window);
            if (@ref is not null)
            {
                var r = ctx.GetRef(@ref);
                step.Resolved = r.Locator;
                if (r.Kind == RefKind.Dom)
                {
                    await ctx.Web.HoverAsync(r.Id);
                    return new { hovered = r.Id, window = target?.Json };
                }
                var (cx, cy) = await ctx.CenterOfAsync(r);
                await ctx.Input.MoveToAsync(cx, cy);
                return new { hovered = r.Id, x = cx, y = cy, window = target?.Json };
            }
            var (px, py) = ToScreen(target, RequirePoint(x, y, "ref or x,y"));
            await ctx.Input.MoveToAsync(px, py);
            return new { x = px, y = py, window = target?.Json };
        });

    [McpServerTool(Name = "drag", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Drag with the left button from one element/point to another. Both ends must be DOM refs, or UIA refs / coordinates (screen, or window-relative when window is given).")]
    public static Task<CallToolResult> Drag(
        ToolContext ctx,
        [Description("Start element ref.")] string? from_ref = null,
        [Description("Start x (physical pixels).")] int? from_x = null,
        [Description("Start y (physical pixels).")] int? from_y = null,
        [Description("End element ref.")] string? to_ref = null,
        [Description("End x (physical pixels).")] int? to_x = null,
        [Description("End y (physical pixels).")] int? to_y = null,
        [Description(WindowArg)] string? window = null) =>
        ctx.RunStepAsync("drag", ToolContext.Args(("from_ref", from_ref), ("from_x", from_x), ("from_y", from_y),
            ("to_ref", to_ref), ("to_x", to_x), ("to_y", to_y), ("window", window)), async step =>
        {
            var from = from_ref is null ? null : ctx.GetRef(from_ref);
            var to = to_ref is null ? null : ctx.GetRef(to_ref);
            step.Resolved = from?.Locator;
            if (from?.Kind == RefKind.Dom || to?.Kind == RefKind.Dom)
            {
                if (from?.Kind != RefKind.Dom || to?.Kind != RefKind.Dom)
                    throw new NotSupportedException("Dragging between a DOM ref and a non-DOM target is not supported.");
                await ctx.Web.DragToAsync(from.Id, to.Id);
                return new { from = from.Id, to = to.Id };
            }
            var target = await ResolveTargetWindowAsync(ctx, window);
            if (target is null && from is not null) await ctx.ActivateOwnerAsync(from);
            var (x1, y1) = from is not null ? await ctx.CenterOfAsync(from) : ToScreen(target, RequirePoint(from_x, from_y, "from_ref or from_x,from_y"));
            var (x2, y2) = to is not null ? await ctx.CenterOfAsync(to) : ToScreen(target, RequirePoint(to_x, to_y, "to_ref or to_x,to_y"));
            await ctx.Input.DragAsync(x1, y1, x2, y2);
            return new { from = new { x = x1, y = y1 }, to = new { x = x2, y = y2 }, window = target?.Json };
        });

    [McpServerTool(Name = "scroll", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Scroll the mouse wheel over an element (ref) or at coordinates x,y (screen, or window-relative when window is given). Native targets support up/down; DOM refs also left/right.")]
    public static Task<CallToolResult> Scroll(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n}) or window ref. Either ref or x,y is required.")] string? @ref = null,
        [Description("x coordinate (physical pixels).")] int? x = null,
        [Description("y coordinate (physical pixels).")] int? y = null,
        [Description("up, down, left or right.")] string direction = "down",
        [Description("Number of wheel notches.")] int amount = 3,
        [Description(WindowArg)] string? window = null) =>
        ctx.RunStepAsync("scroll", ToolContext.Args(("ref", @ref), ("x", x), ("y", y), ("direction", direction), ("amount", amount), ("window", window)), async step =>
        {
            var dir = direction.ToLowerInvariant();
            if (dir is not ("up" or "down" or "left" or "right"))
                throw new ArgumentException($"Unknown direction '{direction}'. Use up, down, left or right.");
            var r = @ref is null ? null : ctx.GetRef(@ref);
            step.Resolved = r?.Locator;
            if (r?.Kind == RefKind.Dom)
            {
                var px = (float)(amount * WebScrollPixelsPerNotch);
                var (dx, dy) = dir switch { "up" => (0f, -px), "down" => (0f, px), "left" => (-px, 0f), _ => (px, 0f) };
                await ctx.Web.ScrollAsync(r.Id, dx, dy);
                return new { scrolled = r.Id, direction = dir, amount };
            }
            if (dir is "left" or "right")
                throw new NotSupportedException("Horizontal scrolling is only supported on DOM refs.");
            var target = await ResolveTargetWindowAsync(ctx, window);
            if (target is null && r is not null) await ctx.ActivateOwnerAsync(r);
            var (sx, sy) = r is not null ? await ctx.CenterOfAsync(r) : ToScreen(target, RequirePoint(x, y, "ref or x,y"));
            // Positive wheel delta scrolls up.
            await ctx.Input.ScrollAsync(sx, sy, dir == "up" ? amount : -amount);
            return new { x = sx, y = sy, direction = dir, amount, window = target?.Json };
        });

    [McpServerTool(Name = "type_text", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Type text into the focused control as Unicode keystrokes (newlines become Enter). With window, that window is activated first so the keystrokes cannot land elsewhere. With secret=true the text is kept out of the log, the journal and saved scenarios.")]
    public static Task<CallToolResult> TypeText(
        ToolContext ctx,
        [Description("The text to type.")] string text,
        [Description("Delay between characters in milliseconds.")] int per_char_delay_ms = 0,
        [Description(InputWindowArg)] string? window = null,
        [Description(ToolContext.SecretArg)] bool secret = false) =>
        ctx.RunStepAsync("type_text", ToolContext.Args(("text", ToolContext.Journaled(text, secret)), ("per_char_delay_ms", per_char_delay_ms), ("window", window),
            ("secret", secret ? true : null)), async _ =>
        {
            var target = await ResolveTargetWindowAsync(ctx, window);
            await ctx.Input.TypeTextAsync(text, per_char_delay_ms);
            return new { typed = text.Length, window = target?.Json };
        });

    [McpServerTool(Name = "press_keys", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Press a key chord such as \"ctrl+s\", \"alt+f4\", \"enter\" or \"f5\". Several chords separated by spaces are pressed in order (\"ctrl+a delete\"). With window, that window is activated first so the keys cannot land elsewhere.")]
    public static Task<CallToolResult> PressKeys(
        ToolContext ctx,
        [Description("Chord(s): modifiers ctrl/alt/shift/win joined with '+' to one key (letter, digit, enter, tab, esc, space, backspace, delete, home, end, pageup, pagedown, arrows, insert, f1-f24).")] string keys,
        [Description(InputWindowArg)] string? window = null) =>
        ctx.RunStepAsync("press_keys", ToolContext.Args(("keys", keys), ("window", window)), async _ =>
        {
            var chords = keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyChord.Parse).ToList();
            if (chords.Count == 0) throw new ArgumentException("keys is empty.");
            var target = await ResolveTargetWindowAsync(ctx, window);
            foreach (var c in chords) await ctx.Input.PressAsync(c);
            return new { pressed = chords.Count, window = target?.Json };
        });

    /// <summary>A window named by a tool's window argument, activated; its rect is the origin of window-relative coordinates.</summary>
    public sealed record TargetWindow(WindowInfo Info, string Ref)
    {
        public object Json => new { @ref = Ref, title = Info.Title, origin = new { x = Info.Rect.X, y = Info.Rect.Y } };
    }

    /// <summary>Resolves and activates the window argument (null when none was given); fails when it does not exist or will not take the foreground.</summary>
    public static async Task<TargetWindow?> ResolveTargetWindowAsync(ToolContext ctx, string? window)
    {
        if (string.IsNullOrEmpty(window)) return null;
        var w = await ctx.ResolveWindowAsync(null, window, fallback: false);
        if (!await ctx.Windows.ActivateAsync(w.Hwnd))
            throw new InvalidOperationException($"Could not activate window '{w.Title}'.");
        ctx.LastWindow = w.Hwnd;
        return new TargetWindow(w, ctx.RegisterWindow(w).Id);
    }

    /// <summary>Window-relative coordinates to screen coordinates (screen coordinates pass through when there is no window).</summary>
    public static (int X, int Y) ToScreen(TargetWindow? target, (int X, int Y) p) =>
        target is null ? p : (target.Info.Rect.X + p.X, target.Info.Rect.Y + p.Y);

    private static MouseButton ParseButton(string button) => button.ToLowerInvariant() switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => throw new ArgumentException($"Unknown button '{button}'. Use left, right or middle."),
    };

    private static (int X, int Y) RequirePoint(int? x, int? y, string what) =>
        x is { } px && y is { } py ? (px, py) : throw new ArgumentException($"Specify {what}.");
}

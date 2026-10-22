using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Input;
using WinUse.Core.Refs;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private const int WebScrollPixelsPerNotch = 100;

    [McpServerTool(Name = "click")]
    [Description("Click an element by ref (UIA: centre of its rectangle plus offset; DOM: Playwright click) or at screen coordinates x,y (physical pixels).")]
    public static Task<CallToolResult> Click(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n}) or window ref (win{n}). Either ref or x,y is required.")] string? @ref = null,
        [Description("Screen x coordinate (physical pixels).")] int? x = null,
        [Description("Screen y coordinate (physical pixels).")] int? y = null,
        [Description("left, right or middle.")] string button = "left",
        [Description("Number of clicks (2 = double click).")] int count = 1,
        [Description("Horizontal offset from the target point.")] int offset_x = 0,
        [Description("Vertical offset from the target point.")] int offset_y = 0) =>
        ctx.RunStepAsync("click", ToolContext.Args(("ref", @ref), ("x", x), ("y", y), ("button", button), ("count", count),
            ("offset_x", offset_x), ("offset_y", offset_y)), async step =>
        {
            var b = ParseButton(button);
            if (@ref is not null)
            {
                var r = ctx.GetRef(@ref);
                step.Resolved = r.Locator;
                if (r.Kind == RefKind.Dom)
                {
                    await ctx.Web.ClickAsync(r.Id, b, count, offset_x, offset_y);
                    return new { clicked = r.Id };
                }
                await ctx.ActivateOwnerAsync(r);
                var (cx, cy) = await ctx.CenterOfAsync(r);
                await ctx.Input.ClickAsync(cx + offset_x, cy + offset_y, b, count);
                return new { clicked = r.Id, x = cx + offset_x, y = cy + offset_y };
            }
            var (px, py) = RequirePoint(x, y, "ref or x,y");
            await ctx.Input.ClickAsync(px + offset_x, py + offset_y, b, count);
            return new { x = px + offset_x, y = py + offset_y };
        });

    [McpServerTool(Name = "hover")]
    [Description("Move the mouse over an element (ref) or to screen coordinates x,y.")]
    public static Task<CallToolResult> Hover(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n}) or window ref. Either ref or x,y is required.")] string? @ref = null,
        [Description("Screen x coordinate (physical pixels).")] int? x = null,
        [Description("Screen y coordinate (physical pixels).")] int? y = null) =>
        ctx.RunStepAsync("hover", ToolContext.Args(("ref", @ref), ("x", x), ("y", y)), async step =>
        {
            if (@ref is not null)
            {
                var r = ctx.GetRef(@ref);
                step.Resolved = r.Locator;
                if (r.Kind == RefKind.Dom)
                {
                    await ctx.Web.HoverAsync(r.Id);
                    return new { hovered = r.Id };
                }
                var (cx, cy) = await ctx.CenterOfAsync(r);
                await ctx.Input.MoveToAsync(cx, cy);
                return new { hovered = r.Id, x = cx, y = cy };
            }
            var (px, py) = RequirePoint(x, y, "ref or x,y");
            await ctx.Input.MoveToAsync(px, py);
            return new { x = px, y = py };
        });

    [McpServerTool(Name = "drag")]
    [Description("Drag with the left button from one element/point to another. Both ends must be DOM refs, or UIA refs / coordinates.")]
    public static Task<CallToolResult> Drag(
        ToolContext ctx,
        [Description("Start element ref.")] string? from_ref = null,
        [Description("Start x (physical pixels).")] int? from_x = null,
        [Description("Start y (physical pixels).")] int? from_y = null,
        [Description("End element ref.")] string? to_ref = null,
        [Description("End x (physical pixels).")] int? to_x = null,
        [Description("End y (physical pixels).")] int? to_y = null) =>
        ctx.RunStepAsync("drag", ToolContext.Args(("from_ref", from_ref), ("from_x", from_x), ("from_y", from_y),
            ("to_ref", to_ref), ("to_x", to_x), ("to_y", to_y)), async step =>
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
            if (from is not null) await ctx.ActivateOwnerAsync(from);
            var (x1, y1) = from is not null ? await ctx.CenterOfAsync(from) : RequirePoint(from_x, from_y, "from_ref or from_x,from_y");
            var (x2, y2) = to is not null ? await ctx.CenterOfAsync(to) : RequirePoint(to_x, to_y, "to_ref or to_x,to_y");
            await ctx.Input.DragAsync(x1, y1, x2, y2);
            return new { from = new { x = x1, y = y1 }, to = new { x = x2, y = y2 } };
        });

    [McpServerTool(Name = "scroll")]
    [Description("Scroll the mouse wheel over an element (ref) or at screen coordinates x,y. Native targets support up/down; DOM refs also left/right.")]
    public static Task<CallToolResult> Scroll(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n}) or window ref. Either ref or x,y is required.")] string? @ref = null,
        [Description("Screen x coordinate (physical pixels).")] int? x = null,
        [Description("Screen y coordinate (physical pixels).")] int? y = null,
        [Description("up, down, left or right.")] string direction = "down",
        [Description("Number of wheel notches.")] int amount = 3) =>
        ctx.RunStepAsync("scroll", ToolContext.Args(("ref", @ref), ("x", x), ("y", y), ("direction", direction), ("amount", amount)), async step =>
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
            if (r is not null) await ctx.ActivateOwnerAsync(r);
            var (sx, sy) = r is not null ? await ctx.CenterOfAsync(r) : RequirePoint(x, y, "ref or x,y");
            // Positive wheel delta scrolls up.
            await ctx.Input.ScrollAsync(sx, sy, dir == "up" ? amount : -amount);
            return new { x = sx, y = sy, direction = dir, amount };
        });

    [McpServerTool(Name = "type_text")]
    [Description("Type text into the focused control as Unicode keystrokes (newlines become Enter).")]
    public static Task<CallToolResult> TypeText(
        ToolContext ctx,
        [Description("The text to type.")] string text,
        [Description("Delay between characters in milliseconds.")] int per_char_delay_ms = 0) =>
        ctx.RunStepAsync("type_text", ToolContext.Args(("text", text), ("per_char_delay_ms", per_char_delay_ms)), async _ =>
        {
            await ctx.Input.TypeTextAsync(text, per_char_delay_ms);
            return new { typed = text.Length };
        });

    [McpServerTool(Name = "press_keys")]
    [Description("Press a key chord such as \"ctrl+s\", \"alt+f4\", \"enter\" or \"f5\". Several chords separated by spaces are pressed in order (\"ctrl+a delete\").")]
    public static Task<CallToolResult> PressKeys(
        ToolContext ctx,
        [Description("Chord(s): modifiers ctrl/alt/shift/win joined with '+' to one key (letter, digit, enter, tab, esc, space, backspace, delete, home, end, pageup, pagedown, arrows, insert, f1-f24).")] string keys) =>
        ctx.RunStepAsync("press_keys", ToolContext.Args(("keys", keys)), async _ =>
        {
            var chords = keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyChord.Parse).ToList();
            if (chords.Count == 0) throw new ArgumentException("keys is empty.");
            foreach (var c in chords) await ctx.Input.PressAsync(c);
            return new { pressed = chords.Count };
        });

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

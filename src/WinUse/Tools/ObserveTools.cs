using System.Buffers.Binary;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Refs;
using WinUse.Core.Screen;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ObserveTools
{
    [McpServerTool(Name = "screenshot", ReadOnly = true)]
    [Description("Capture a PNG of the whole screen, a window, or a single element. Returns the image plus width/height (image pixels), scale and origin {x, y}: the virtual-screen physical pixel of image pixel (0,0). Map an image pixel to click(x, y) with screen_x = origin.x + pixel_x / scale, screen_y = origin.y + pixel_y / scale (origin is null for a DOM element capture).")]
    public static Task<CallToolResult> Screenshot(
        ToolContext ctx,
        [Description("What to capture: \"screen\", \"window\" (window, ref or title; default: last observed / foreground window) or \"element\" (ref). Default: \"window\" when window or title is given, else \"screen\".")] string? target = null,
        [Description("Window ref (win{n}) for target=window, or element ref (u{n} / w{n}) for target=element.")] string? @ref = null,
        [Description("Window title regex for target=window.")] string? title = null,
        [Description("Downscale factor between 0.25 and 1.0.")] double scale = 1.0,
        [Description("Window ref (win{n}) or title regex to capture, like the window argument of the other tools.")] string? window = null) =>
        ctx.RunStepAsync("screenshot", ToolContext.Args(("target", target), ("ref", @ref), ("title", title), ("scale", scale), ("window", window)), async step =>
        {
            var (kind, windowRef, windowTitle) = NormalizeScreenshotArgs(target, @ref, title, window);
            Capture c;
            switch (kind)
            {
                case "screen":
                    c = await ctx.Screen.CaptureScreenAsync(scale);
                    break;
                case "window":
                {
                    var w = await ctx.ResolveWindowAsync(windowRef, windowTitle, fallback: true);
                    if (windowRef is not null) step.Resolved = ctx.GetRef(windowRef).Locator;
                    c = await ctx.Screen.CaptureWindowAsync(w.Hwnd, scale);
                    break;
                }
                case "element":
                {
                    if (string.IsNullOrEmpty(@ref)) throw new ArgumentException("target=element requires ref.");
                    var r = ctx.GetRef(@ref);
                    step.Resolved = r.Locator;
                    if (r.Kind == RefKind.Dom)
                    {
                        // Playwright captures the element from the page: its screen position is not known.
                        var png = await ctx.Web.ScreenshotAsync(r.Id);
                        var (width, height) = PngSize(png);
                        step.Extra.Add(ImageContentBlock.FromBytes(png, "image/png"));
                        return new { width, height, scale = 1.0, origin = (object?)null };
                    }
                    var rect = r.Kind == RefKind.Uia
                        ? await ctx.Uia.GetRectAsync(r.Id)
                        : (await ctx.ResolveWindowAsync(r.Id, null, false)).Rect;
                    c = await ctx.Screen.CaptureRectAsync(rect, scale);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown target '{target}'. Use screen, window or element.");
            }
            step.Extra.Add(ImageContentBlock.FromBytes(c.Png, "image/png"));
            return ScreenshotPayload(c);
        });

    /// <summary>Result of a screen/window/rect capture: size, scale and the virtual-screen origin of pixel (0,0).</summary>
    public static object ScreenshotPayload(Capture c) =>
        new { width = c.Width, height = c.Height, scale = c.Scale, origin = (object?)new { x = c.OriginX, y = c.OriginY } };

    /// <summary>
    /// Effective screenshot target and window arguments. <paramref name="window"/> (a ref or title regex, like every
    /// other tool's window argument) wins over ref/title and makes "window" the default target; so does a title.
    /// </summary>
    public static (string Target, string? WindowRef, string? WindowTitle) NormalizeScreenshotArgs(string? target, string? @ref, string? title, string? window)
    {
        var t = string.IsNullOrEmpty(target)
            ? (!string.IsNullOrEmpty(window) || !string.IsNullOrEmpty(title) ? "window" : "screen")
            : target.ToLowerInvariant();
        if (!string.IsNullOrEmpty(window)) return (t, null, window); // ResolveWindowAsync accepts a win{n} ref in the title slot
        return (t, t == "window" ? @ref : null, title);
    }

    private static (int Width, int Height) PngSize(byte[] png) =>
        png.Length >= 24
            ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)))
            : (0, 0);

    [McpServerTool(Name = "list_windows", ReadOnly = true)]
    [Description("List visible top-level windows (foreground first) with a window ref, title, class, process, pid, rect and state.")]
    public static Task<CallToolResult> ListWindows(
        ToolContext ctx,
        [Description("Optional title regex filter.")] string? title_regex = null,
        [Description("Optional process name filter (with or without .exe).")] string? process = null) =>
        ctx.RunStepAsync("list_windows", ToolContext.Args(("title_regex", title_regex), ("process", process)), async _ =>
        {
            var list = await ctx.Windows.ListAsync(title_regex, process);
            return new { windows = list.Select(w => WindowJson(ctx.RegisterWindow(w).Id, w)).ToList() };
        });

    /// <summary>Serializable window projection (WindowInfo itself holds an nint handle, which System.Text.Json rejects).</summary>
    internal static object WindowJson(string refId, WindowInfo w) => new
    {
        @ref = refId,
        title = w.Title,
        class_name = w.ClassName,
        process = w.ProcessName,
        pid = w.Pid,
        rect = w.Rect,
        state = w.State.ToString().ToLowerInvariant(),
    };

    [McpServerTool(Name = "get_ui_tree", ReadOnly = true)]
    [Description("Get the compressed UI Automation tree of a window (refs u{n}, control type, name, automationId, value, state, rect). Use the refs with the other tools.")]
    public static Task<CallToolResult> GetUiTree(
        ToolContext ctx,
        [Description("Window ref (win{n}) or title regex. Default: last observed window, else the foreground window.")] string? window = null,
        [Description("Optional element ref (u{n}) to return only that subtree.")] string? scope = null,
        [Description("Maximum depth to walk.")] int max_depth = 12,
        [Description("Keep only interactive elements and labelled text.")] bool interactive_only = true,
        [Description("Include offscreen elements.")] bool include_offscreen = false) =>
        ctx.RunStepAsync("get_ui_tree", ToolContext.Args(("window", window), ("scope", scope), ("max_depth", max_depth),
            ("interactive_only", interactive_only), ("include_offscreen", include_offscreen)), async _ =>
        {
            var w = scope is not null && window is null
                ? await ctx.ResolveWindowAsync(scope, null, false)
                : await ctx.ResolveWindowAsync(null, window, fallback: true);
            ctx.LastWindow = w.Hwnd;
            var opt = new TreeOptions(scope, max_depth, interactive_only, include_offscreen);
            var tree = scope is null
                ? await UiaService.RetryIfChildlessAsync(() => ctx.Uia.GetTreeAsync(w.Hwnd, opt), UiaService.EmptyTreeRetryDelay)
                : await ctx.Uia.GetTreeAsync(w.Hwnd, opt);
            return new
            {
                window = new { @ref = ctx.RegisterWindow(w).Id, title = w.Title, process = w.ProcessName },
                tree,
            };
        });

    [McpServerTool(Name = "get_dom", ReadOnly = true)]
    [Description("List the interactive elements of the connected WebView2 page (refs w{n}, role, name, tag, value, state, rect, selector). Requires process_run with webview_debug=true.")]
    public static Task<CallToolResult> GetDom(
        ToolContext ctx,
        [Description("Optional page id (p{n}), URL substring or title substring to switch to first.")] string? page = null,
        [Description("Return only interactive elements.")] bool interactive_only = true) =>
        ctx.RunStepAsync("get_dom", ToolContext.Args(("page", page), ("interactive_only", interactive_only)), async _ =>
        {
            if (!string.IsNullOrEmpty(page)) await ctx.Web.SelectPageAsync(page);
            var nodes = await ctx.Web.GetDomAsync(interactive_only);
            var pages = await ctx.Web.ListPagesAsync();
            return new { pages, nodes };
        });

    [McpServerTool(Name = "get_element", ReadOnly = true)]
    [Description("Get details of a single element by ref (UIA properties, DOM selector/text, or window info).")]
    public static Task<CallToolResult> GetElement(
        ToolContext ctx,
        [Description("Element ref (u{n}, w{n} or win{n}).")] string @ref) =>
        ctx.RunStepAsync("get_element", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = ctx.GetRef(@ref);
            step.Resolved = r.Locator;
            switch (r.Kind)
            {
                case RefKind.Uia:
                    return new { element = (object)await ctx.Uia.GetElementAsync(r.Id) };
                case RefKind.Dom:
                {
                    var sel = ((DomLocator)r.Locator).Selector;
                    return new
                    {
                        element = (object)new
                        {
                            @ref = r.Id,
                            selector = sel,
                            text = await ctx.Web.GetTextAsync(r.Id),
                            enabled = await ctx.Web.IsEnabledAsync(r.Id),
                        },
                    };
                }
                default:
                {
                    var w = await ctx.ResolveWindowAsync(r.Id, null, false);
                    return new { element = (object)WindowJson(r.Id, w) };
                }
            }
        });

    [McpServerTool(Name = "get_text", ReadOnly = true)]
    [Description("Get the text of an element (UIA Value/Text pattern or name; DOM input value or innerText).")]
    public static Task<CallToolResult> GetText(
        ToolContext ctx,
        [Description("Element ref (u{n} or w{n}).")] string @ref) =>
        ctx.RunStepAsync("get_text", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = ctx.GetRef(@ref);
            step.Resolved = r.Locator;
            return new { text = await ReadTextAsync(ctx, r) };
        });

    internal static Task<string> ReadTextAsync(ToolContext ctx, ElementRef r) => r.Kind switch
    {
        RefKind.Uia => ctx.Uia.GetTextAsync(r.Id),
        RefKind.Dom => ctx.Web.GetTextAsync(r.Id),
        _ => throw new NotSupportedException($"Ref '{r.Id}' is not an element ref."),
    };

    [McpServerTool(Name = "get_table", ReadOnly = true)]
    [Description("Read a UIA table/grid/list view as a 2-D array of cell texts (header row first when available).")]
    public static Task<CallToolResult> GetTable(
        ToolContext ctx,
        [Description("UIA element ref (u{n}) of the table.")] string @ref) =>
        ctx.RunStepAsync("get_table", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = RequireUia(ctx, @ref);
            step.Resolved = r.Locator;
            return new { rows = await ctx.Uia.GetTableAsync(r.Id) };
        });

    [McpServerTool(Name = "get_list_items", ReadOnly = true)]
    [Description("List the item names of a UIA list, combo box, tab or tree.")]
    public static Task<CallToolResult> GetListItems(
        ToolContext ctx,
        [Description("UIA element ref (u{n}) of the list or combo box.")] string @ref) =>
        ctx.RunStepAsync("get_list_items", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = RequireUia(ctx, @ref);
            step.Resolved = r.Locator;
            return new { items = await ctx.Uia.GetListItemsAsync(r.Id) };
        });

    internal static ElementRef RequireUia(ToolContext ctx, string id)
    {
        var r = ctx.GetRef(id);
        return r.Kind == RefKind.Uia ? r : throw new NotSupportedException($"Ref '{id}' must be a UIA ref (u{{n}}).");
    }
}

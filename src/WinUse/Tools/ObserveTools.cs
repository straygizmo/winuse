using System.Buffers.Binary;
using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinUse.Core.Java;
using WinUse.Core.Refs;
using WinUse.Core.Screen;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Tools;

[McpServerToolType]
public sealed class ObserveTools
{
    [McpServerTool(Name = "screenshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Capture a PNG of a monitor, the whole virtual screen, a window, or a single element. Returns the image plus width/height (image pixels), scale, origin {x, y} (the screen pixel of image pixel (0,0): screen_x = origin.x + pixel_x / scale; null for a DOM element capture), method (\"screen\": copied from the screen, what the user sees including dialogs; \"print\": PrintWindow, used when the window is not in the foreground, which misses owned dialogs and can lag in Chromium apps) and, for screen captures, the monitor layout. To click an image pixel of a window capture, pass it to click with window and the same x,y; for other captures add origin.")]
    public static Task<CallToolResult> Screenshot(
        ToolContext ctx,
        [Description("What to capture: \"screen\" (the primary monitor, or monitor), \"virtual\" (all monitors in one image), \"window\" (window, ref or title; default: last observed / foreground window) or \"element\" (ref). Default: \"window\" when window or title is given, else \"screen\".")] string? target = null,
        [Description("Window ref (win{n}) for target=window, or element ref (u{n} / j{n} / w{n}) for target=element.")] string? @ref = null,
        [Description("Window title regex for target=window.")] string? title = null,
        [Description("Downscale factor between 0.25 and 1.0.")] double scale = 1.0,
        [Description("Window ref (win{n}) or title regex to capture, like the window argument of the other tools.")] string? window = null,
        [Description("Monitor index from a previous result's monitors (target=screen). Default: the primary monitor.")] int? monitor = null,
        [Description("Window capture method: \"auto\" (screen copy when the window or its dialog is in the foreground, else print), \"screen\" or \"print\".")] string method = "auto",
        [Description("Wait this many milliseconds before capturing, so the app has repainted after the previous input.")] int settle_ms = 0) =>
        ctx.RunStepAsync("screenshot", ToolContext.Args(("target", target), ("ref", @ref), ("title", title), ("scale", scale), ("window", window),
            ("monitor", monitor), ("method", method), ("settle_ms", settle_ms)), async step =>
        {
            var (kind, windowRef, windowTitle) = NormalizeScreenshotArgs(target, @ref, title, window);
            var captureMethod = ParseMethod(method);
            if (settle_ms > 0) await Task.Delay(Math.Min(settle_ms, 60_000));
            WinUse.Core.Screen.Capture c;
            IReadOnlyList<MonitorInfo>? monitors = null;
            switch (kind)
            {
                case "screen":
                    c = await ctx.Screen.CaptureScreenAsync(scale, monitor);
                    monitors = await ctx.Screen.ListMonitorsAsync();
                    break;
                case "virtual":
                    c = await ctx.Screen.CaptureVirtualScreenAsync(scale);
                    monitors = await ctx.Screen.ListMonitorsAsync();
                    break;
                case "window":
                {
                    var w = await ctx.ResolveWindowAsync(windowRef, windowTitle, fallback: true);
                    if (windowRef is not null) step.Resolved = ctx.GetRef(windowRef).Locator;
                    c = await ctx.Screen.CaptureWindowAsync(w.Hwnd, scale, captureMethod);
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
                        return new { width, height, scale = 1.0, origin = (object?)null, method = "page" };
                    }
                    var rect = r.Kind switch
                    {
                        RefKind.Uia => await ctx.Uia.GetRectAsync(r.Id),
                        RefKind.Java => await ctx.Java.GetRectAsync(r.Id),
                        _ => (await ctx.ResolveWindowAsync(r.Id, null, false)).Rect,
                    };
                    c = await ctx.Screen.CaptureRectAsync(rect, scale);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown target '{target}'. Use screen, virtual, window or element.");
            }
            step.Extra.Add(ImageContentBlock.FromBytes(c.Png, "image/png"));
            return ScreenshotPayload(c, monitors);
        });

    /// <summary>Result of a screen/window/rect capture: size, scale, the virtual-screen origin of pixel (0,0), the method and (for screen captures) the monitors.</summary>
    public static object ScreenshotPayload(WinUse.Core.Screen.Capture c, IReadOnlyList<MonitorInfo>? monitors = null) =>
        new
        {
            width = c.Width,
            height = c.Height,
            scale = c.Scale,
            origin = (object?)new { x = c.OriginX, y = c.OriginY },
            method = c.Method,
            monitors = monitors?.Select(m => new { index = m.Index, rect = m.Rect, work_area = m.WorkArea, primary = m.Primary, device = m.Device }).ToList(),
        };

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

    public static CaptureMethod ParseMethod(string? method) => (method ?? "auto").ToLowerInvariant() switch
    {
        "" or "auto" => CaptureMethod.Auto,
        "screen" => CaptureMethod.Screen,
        "print" => CaptureMethod.Print,
        _ => throw new ArgumentException($"Unknown method '{method}'. Use auto, screen or print."),
    };

    private static (int Width, int Height) PngSize(byte[] png) =>
        png.Length >= 24
            ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)))
            : (0, 0);

    [McpServerTool(Name = "list_windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("List visible top-level windows (foreground first) with a window ref, title, class, process, pid, rect (GetWindowRect, includes the invisible resize borders), visible_rect (what is drawn on screen; use it to crop or record a window) and state. A window keeps its ref for the whole session.")]
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
        visible_rect = w.State == WindowState.Minimized ? w.Rect : WindowService.VisibleRectOf(w.Hwnd),
        state = w.State.ToString().ToLowerInvariant(),
    };

    [McpServerTool(Name = "get_ui_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Get the compressed UI Automation tree of a window (refs u{n}, control type, name, automationId, value, state, rect); the window object carries the window's rect, visible_rect and state too. Use the refs with the other tools. Narrow the output with control_type / name (matching nodes are hoisted to their nearest kept ancestor), compact (no null / default fields) and max_nodes. Java Swing windows are read through Java Access Bridge and return j{n} refs (menu items are listed even while their menu is closed); java_access_bridge explains why a Java window could not be read that way.")]
    public static Task<CallToolResult> GetUiTree(
        ToolContext ctx,
        [Description("Window ref (win{n}) or title regex. Default: last observed window, else the foreground window.")] string? window = null,
        [Description("Optional element ref (u{n} or j{n}) to return only that subtree.")] string? scope = null,
        [Description("Maximum depth to walk.")] int max_depth = 12,
        [Description("Keep only interactive elements and labelled text.")] bool interactive_only = true,
        [Description("Include offscreen elements.")] bool include_offscreen = false,
        [Description("Comma-separated control types to keep, e.g. \"Edit,Button\" (case-insensitive). Other nodes are dropped and their matching descendants hoisted.")] string? control_type = null,
        [Description("Regex (case-insensitive) the node name must match. Combined with control_type.")] string? name = null,
        [Description("Omit null and default-valued fields (automationId, value, checked, enabled=true, offscreen=false, focused=false, depth) and print rect as \"x,y,w,h\".")] bool compact = false,
        [Description("Keep at most this many nodes (document order); the result then reports omitted_nodes.")] int? max_nodes = null) =>
        ctx.RunStepAsync("get_ui_tree", ToolContext.Args(("window", window), ("scope", scope), ("max_depth", max_depth),
            ("interactive_only", interactive_only), ("include_offscreen", include_offscreen), ("control_type", control_type), ("name", name),
            ("compact", compact), ("max_nodes", max_nodes)), async _ =>
        {
            var nameRegex = name is null ? null : new Regex(name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var types = UiTreeShaper.ParseControlTypes(control_type);
            if (max_nodes is < 1) throw new ArgumentOutOfRangeException(nameof(max_nodes), "max_nodes must be at least 1.");

            var w = scope is not null && window is null
                ? await ctx.ResolveWindowAsync(scope, null, false)
                : await ctx.ResolveWindowAsync(null, window, fallback: true);
            ctx.LastWindow = w.Hwnd;
            var opt = new TreeOptions(scope, max_depth, interactive_only, include_offscreen);
            var isJava = scope is null ? await ctx.Java.IsJavaWindowAsync(w) : ToolContext.IsJavaRef(scope);
            Task<UiNode> Read() => isJava ? ctx.Java.GetTreeAsync(w.Hwnd, opt) : ctx.Uia.GetTreeAsync(w.Hwnd, opt);
            var tree = scope is null
                ? await UiaService.RetryIfChildlessAsync(Read, UiaService.EmptyTreeRetryDelay)
                : await Read();
            // A modal dialog that has just appeared (focused child Window without content) may not have populated yet.
            if (scope is null && UiaService.HasEmptyFocusedWindow(tree))
            {
                await Task.Delay(UiaService.EmptyTreeRetryDelay);
                tree = await Read();
            }
            tree = UiTreeShaper.Filter(tree, types, nameRegex);
            var omitted = 0;
            if (max_nodes is { } max) (tree, omitted) = UiTreeShaper.Truncate(tree, max);
            return new
            {
                window = new { @ref = ctx.RegisterWindow(w).Id, title = w.Title, process = w.ProcessName, pid = w.Pid, rect = w.Rect,
                    visible_rect = w.State == WindowState.Minimized ? w.Rect : WindowService.VisibleRectOf(w.Hwnd), state = w.State.ToString().ToLowerInvariant() },
                tree = compact ? (object)UiTreeShaper.Compact(tree) : tree,
                omitted_nodes = omitted > 0 ? omitted : (int?)null,
                java_access_bridge = !isJava && JavaService.LooksLikeJavaWindow(w) ? ctx.Java.UnavailableReason(w) : null,
            };
        });

    [McpServerTool(Name = "get_dom", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
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

    [McpServerTool(Name = "get_element", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Get details of a single element by ref (UIA properties, DOM selector/text, or window info).")]
    public static Task<CallToolResult> GetElement(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n}, w{n} or win{n}).")] string @ref) =>
        ctx.RunStepAsync("get_element", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = ctx.GetRef(@ref);
            step.Resolved = r.Locator;
            switch (r.Kind)
            {
                case RefKind.Uia:
                    return new { element = (object)await ctx.Uia.GetElementAsync(r.Id) };
                case RefKind.Java:
                    return new { element = (object)await ctx.Java.GetElementAsync(r.Id) };
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

    [McpServerTool(Name = "get_text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Get the text of an element (UIA Value/Text pattern or name; DOM input value or innerText).")]
    public static Task<CallToolResult> GetText(
        ToolContext ctx,
        [Description("Element ref (u{n}, j{n} or w{n}).")] string @ref) =>
        ctx.RunStepAsync("get_text", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = ctx.GetRef(@ref);
            step.Resolved = r.Locator;
            return new { text = await ReadTextAsync(ctx, r) };
        });

    internal static Task<string> ReadTextAsync(ToolContext ctx, ElementRef r) => r.Kind switch
    {
        RefKind.Uia => ctx.Uia.GetTextAsync(r.Id),
        RefKind.Java => ctx.Java.GetTextAsync(r.Id),
        RefKind.Dom => ctx.Web.GetTextAsync(r.Id),
        _ => throw new NotSupportedException($"Ref '{r.Id}' is not an element ref."),
    };

    [McpServerTool(Name = "get_table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read a UIA table/grid/list view as a 2-D array of cell texts (header row first when available).")]
    public static Task<CallToolResult> GetTable(
        ToolContext ctx,
        [Description("Element ref (u{n} or j{n}) of the table. Not yet supported for Java elements.")] string @ref) =>
        ctx.RunStepAsync("get_table", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = RequireElement(ctx, @ref);
            step.Resolved = r.Locator;
            return new { rows = r.Kind == RefKind.Java ? await ctx.Java.GetTableAsync(r.Id) : await ctx.Uia.GetTableAsync(r.Id) };
        });

    [McpServerTool(Name = "get_list_items", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("List the item names of a UIA or Java list, combo box, tab or tree.")]
    public static Task<CallToolResult> GetListItems(
        ToolContext ctx,
        [Description("Element ref (u{n} or j{n}) of the list or combo box.")] string @ref) =>
        ctx.RunStepAsync("get_list_items", ToolContext.Args(("ref", @ref)), async step =>
        {
            var r = RequireElement(ctx, @ref);
            step.Resolved = r.Locator;
            return new { items = r.Kind == RefKind.Java ? await ctx.Java.GetListItemsAsync(r.Id) : await ctx.Uia.GetListItemsAsync(r.Id) };
        });

    internal static ElementRef RequireElement(ToolContext ctx, string id)
    {
        var r = ctx.GetRef(id);
        return r.Kind is RefKind.Uia or RefKind.Java ? r : throw new NotSupportedException($"Ref '{id}' must be a UIA or Java element ref (u{{n}} / j{{n}}).");
    }
}

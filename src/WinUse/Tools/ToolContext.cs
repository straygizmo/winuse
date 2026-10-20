using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using WinUse.Core.Config;
using WinUse.Core.Files;
using WinUse.Core.Grounding;
using WinUse.Core.Grounding.Visual;
using WinUse.Core.Input;
using WinUse.Core.Jev;
using WinUse.Core.Processes;
using WinUse.Core.Refs;
using WinUse.Core.Scenarios;
using WinUse.Core.Screen;
using WinUse.Core.Threading;
using WinUse.Core.Uia;
using WinUse.Core.Web;
using WinUse.Core.Windows;

namespace WinUse.Tools;

/// <summary>Mutable per-step state a tool body fills in for the journal.</summary>
public sealed class ToolStep(JsonObject args)
{
    /// <summary>The journaled arguments; a tool may refine them (e.g. act records the executed kind).</summary>
    public JsonObject Args { get; } = args;
    public Locator? Resolved { get; set; }
    public bool? Passed { get; set; }
    /// <summary>Set by <c>act</c>: whether an action was actually performed.</summary>
    public bool? Executed { get; set; }
    public List<ContentBlock> Extra { get; } = [];
}

/// <summary>All services shared by the MCP tools (DI singleton). Creating it has no UIA / Playwright side effects.</summary>
public sealed class ToolContext : IObservationSource, IActionExecutor, IWindowCapture, IDisposable
{
    private static readonly Regex WebRefPattern = new(@"^w\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex UiaRefPattern = new(@"^u\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex PointRefPattern = new(@"^pt\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex AnyRefPattern = new(@"^(u|w|win|pt)\d+$", RegexOptions.CultureInvariant);

    /// <summary>Arguments that may carry a ref; their locators are journaled under <see cref="ResolvedRefsKey"/>.</summary>
    public static readonly string[] RefArgNames = ["ref", "from_ref", "to_ref", "window", "scope", "title"];

    /// <summary>Journaled-args key holding <c>{ argName: Locator }</c> for every ref argument of a call.</summary>
    public const string ResolvedRefsKey = "resolved_refs";

    private readonly AsyncLocal<StrongBox<ActKind?>?> _kindCapture = new();
    private int _disposed;

    private ToolContext(WinUseConfig cfg, ILoggerFactory lf, IJevClient? jev, IObservationSource? observation, IActionExecutor? executor)
    {
        Config = cfg;
        Log = lf.CreateLogger("WinUse.Tools");
        Dispatcher = new AutomationDispatcher();
        Refs = new RefRegistry();
        Windows = new WindowService(Dispatcher);
        Input = new InputService(Dispatcher);
        Screen = new ScreenService(Dispatcher);
        Processes = new ProcessService(cfg);
        Files = new FileService(cfg);
        Uia = new UiaService(Dispatcher, Refs, Windows);
        Web = new WebService(Refs);
        Jev = jev ?? new JevClient(new HttpClient(), cfg, lf.CreateLogger<JevClient>());
        Grounder = new Grounder(Jev, observation ?? this, new KindCapturingExecutor(executor ?? this, this), cfg, this, Log);
        Journal = new Journal();
        ActScreenshot = async () => LastWindow is { } h
            ? await Screen.CaptureWindowAsync(h, ActScreenshotScale).ConfigureAwait(false)
            : null;
    }

    public static ToolContext Create(WinUseConfig cfg, ILoggerFactory? loggerFactory = null) =>
        new(cfg, loggerFactory ?? NullLoggerFactory.Instance, null, null, null);

    /// <summary>Overload with replaceable grounding collaborators (Jev, observation, execution), mainly for tests.</summary>
    public static ToolContext Create(WinUseConfig cfg, ILoggerFactory? loggerFactory, IJevClient? jev,
        IObservationSource? observation = null, IActionExecutor? executor = null) =>
        new(cfg, loggerFactory ?? NullLoggerFactory.Instance, jev, observation, executor);

    public WinUseConfig Config { get; }
    public ILogger Log { get; }
    public AutomationDispatcher Dispatcher { get; }
    public RefRegistry Refs { get; }
    public WindowService Windows { get; }
    public InputService Input { get; }
    public ScreenService Screen { get; }
    public ProcessService Processes { get; }
    public FileService Files { get; }
    public UiaService Uia { get; }
    public WebService Web { get; }
    public IJevClient Jev { get; }
    public Grounder Grounder { get; }
    public Journal Journal { get; }

    /// <summary>The window most recently observed or operated on; the default target for observation.</summary>
    public nint? LastWindow { get; set; }

    /// <summary>Pid of the process whose WebView2 the <see cref="Web"/> session is connected to (set by process_run).</summary>
    public int? WebOwnerPid { get; set; }

    /// <summary>Process name of <see cref="WebOwnerPid"/> (set by process_run; survives a launcher process exiting).</summary>
    public string? WebOwnerProcessName { get; set; }

    /// <summary>Scale of the window capture attached to an <c>act</c> result that executed nothing.</summary>
    public const double ActScreenshotScale = 0.5;

    /// <summary>
    /// Captures the screenshot attached to a low-confidence <c>act</c> result (spec §6.2): the observed window at
    /// <see cref="ActScreenshotScale"/>, or null when no window was observed. Replaceable for tests.
    /// </summary>
    public Func<Task<WinUse.Core.Screen.Capture?>> ActScreenshot { get; set; }

    // ---- step execution -------------------------------------------------------------------------

    /// <summary>Allocates a step id, runs <paramref name="body"/>, journals the call and converts the outcome to a tool result.</summary>
    public async Task<CallToolResult> RunStepAsync(string tool, JsonObject args, Func<ToolStep, Task<object>> body)
    {
        var stepId = Journal.NextId();
        var sw = Stopwatch.StartNew();
        AttachRefLocators(args);
        var step = new ToolStep(args);
        string? json = null;
        Exception? error = null;
        try
        {
            var payload = await body(step).ConfigureAwait(false);
            json = ToolResult.Ok(payload, stepId);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        sw.Stop();
        // Exactly one journal entry per step, whether the body or the serialization failed.
        Journal.Record(new JournalEntry(stepId, tool, args, step.Resolved, step.Passed, sw.Elapsed, DateTimeOffset.Now, step.Executed));
        if (error is not null || json is null)
        {
            Log.LogWarning(error, "{StepId} {Tool} {Args} -> error in {Elapsed} ms",
                stepId, tool, args.ToJsonString(ToolResult.Json), sw.ElapsedMilliseconds);
            return ToolResult.Error(error?.Message ?? "no result", stepId);
        }
        Log.LogInformation("{StepId} {Tool} {Args} -> ok in {Elapsed} ms: {Result}",
            stepId, tool, args.ToJsonString(ToolResult.Json), sw.ElapsedMilliseconds, Truncate(json, 2000));
        return ToolResult.Text(json, step.Extra);
    }

    /// <summary>Journals the locator of every ref argument known at call time, so replay can re-resolve all of them.</summary>
    private void AttachRefLocators(JsonObject args)
    {
        JsonObject? map = null;
        foreach (var name in RefArgNames)
            if (args[name] is JsonValue v && v.TryGetValue<string>(out var id) && Refs.TryGet(id, out var r))
                (map ??= new JsonObject())[name] = JsonSerializer.SerializeToNode(r.Locator, typeof(Locator));
        if (map is not null) args[ResolvedRefsKey] = map;
    }

    public static bool LooksLikeRef(string s) => AnyRefPattern.IsMatch(s);

    /// <summary>Builds the journaled argument object, skipping null values.</summary>
    public static JsonObject Args(params (string Key, object? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values)
            if (v is not null) o[k] = JsonSerializer.SerializeToNode(v, v.GetType(), ToolResult.Json);
        return o;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- refs -----------------------------------------------------------------------------------

    public ElementRef GetRef(string id) =>
        Refs.TryGet(id, out var r)
            ? r
            : throw new KeyNotFoundException($"Unknown ref '{id}'. Observe again (get_ui_tree / get_dom / find) to get fresh refs.");

    public static bool IsWebRef(string id) => WebRefPattern.IsMatch(id);
    public static bool IsUiaRef(string id) => UiaRefPattern.IsMatch(id);
    public static bool IsPointRef(string id) => PointRefPattern.IsMatch(id);

    public static WindowLocator LocatorOf(WindowInfo w) =>
        new("^" + Regex.Escape(w.Title) + "$", w.ProcessName, w.ClassName);

    public ElementRef RegisterWindow(WindowInfo w) => Refs.Register(RefKind.Window, LocatorOf(w), w.Hwnd);

    /// <summary>Centre (screen pixels) of a UIA element ref or a window ref.</summary>
    public async Task<(int X, int Y)> CenterOfAsync(ElementRef r)
    {
        if (r.Kind == RefKind.Point) return await PointOfAsync(r).ConfigureAwait(false);
        var rect = r.Kind switch
        {
            RefKind.Uia => await Uia.GetRectAsync(r.Id).ConfigureAwait(false),
            RefKind.Window => (await FindWindowAsync(r).ConfigureAwait(false)).Rect,
            _ => throw new NotSupportedException($"Ref '{r.Id}' has no screen rectangle."),
        };
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException($"Ref '{r.Id}' has no visible area.");
        return (rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    /// <summary>The screen point of a point ref: the handle captured at registration, else the locator applied to the live window.</summary>
    private async Task<(int X, int Y)> PointOfAsync(ElementRef r)
    {
        if (r.Handle is ValueTuple<int, int> p) return p;
        var loc = (PointLocator)r.Locator;
        var w = await Windows.FindWithFallbackAsync(loc.Window).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Window for ref '{r.Id}' not found.");
        return loc.ToScreen(w.Rect);
    }

    /// <summary>Brings the window owning a UIA / window / point ref to the foreground (best effort) before coordinate input.</summary>
    public async Task ActivateOwnerAsync(ElementRef r)
    {
        var loc = r.Locator switch
        {
            UiaLocator u => u.Window,
            WindowLocator wl => wl,
            PointLocator pl => pl.Window,
            _ => null,
        };
        if (loc is null) return;
        var w = await Windows.FindWithFallbackAsync(loc).ConfigureAwait(false);
        if (w is null) return;
        await Windows.ActivateAsync(w.Hwnd).ConfigureAwait(false);
        LastWindow = w.Hwnd;
    }

    // ---- windows --------------------------------------------------------------------------------

    private async Task<WindowInfo> FindWindowAsync(ElementRef r)
    {
        var all = await Windows.ListAsync().ConfigureAwait(false);
        if (r.Handle is nint h && all.FirstOrDefault(w => w.Hwnd == h) is { } live) return live;
        var loc = r.Locator switch
        {
            WindowLocator wl => wl,
            UiaLocator u => u.Window,
            DomLocator d => d.Window,
            _ => throw new ArgumentException($"Ref '{r.Id}' does not identify a window."),
        };
        return WindowService.PickWithFallback(all, loc)
               ?? throw new InvalidOperationException($"Window for ref '{r.Id}' not found.");
    }

    /// <summary>
    /// Resolves a window from a ref (<c>win{n}</c>, or the owner of an element ref), else a title regex
    /// (a <c>win{n}</c> ref is also accepted in place of the title). With neither, falls back to
    /// <see cref="LastWindow"/> and then the foreground window when <paramref name="fallback"/> is set.
    /// </summary>
    public async Task<WindowInfo> ResolveWindowAsync(string? refId, string? title, bool fallback)
    {
        if (!string.IsNullOrEmpty(refId)) return await FindWindowAsync(GetRef(refId)).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(title))
        {
            if (Refs.TryGet(title, out var r) && r.Kind == RefKind.Window) return await FindWindowAsync(r).ConfigureAwait(false);
            return (await Windows.ListAsync(title).ConfigureAwait(false)).FirstOrDefault()
                   ?? throw new InvalidOperationException($"No window matches '{title}'.");
        }
        if (!fallback) throw new ArgumentException("Specify a window ref or title.");
        var all = await Windows.ListAsync().ConfigureAwait(false);
        if (LastWindow is { } last && all.FirstOrDefault(w => w.Hwnd == last) is { } lw) return lw;
        // EnumWindows returns top-level windows in z-order, so the first visible one is the foreground window.
        return all.FirstOrDefault() ?? throw new InvalidOperationException("No top-level window found.");
    }

    // ---- IObservationSource ---------------------------------------------------------------------

    public async Task<Observation> ObserveAsync(string? window)
    {
        var w = await ResolveWindowAsync(null, window, fallback: true).ConfigureAwait(false);
        LastWindow = w.Hwnd;
        var tree = await UiaService.RetryIfChildlessAsync(() => Uia.GetTreeAsync(w.Hwnd, new TreeOptions()), UiaService.EmptyTreeRetryDelay)
            .ConfigureAwait(false);
        IReadOnlyList<DomNode> dom = [];
        Rect? webViewRect = null;
        if (Web.IsConnected)
        {
            var (hostRect, hostPid) = await FindWebViewHostAsync(tree.Ref).ConfigureAwait(false);
            // Only use the DOM when the connected WebView belongs to this window; otherwise another app's
            // elements would be mixed into the candidates.
            if (DomBelongsToWindow(hostRect, w.Pid, hostPid is { } hp ? ProcessNameOf(hp) : null, WebOwnerPid, WebOwnerProcessName))
            {
                webViewRect = hostRect;
                try
                {
                    Web.Window = LocatorOf(w);
                    dom = await Web.GetDomAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.LogWarning(ex, "DOM collection failed; observing with UIA only");
                    dom = [];
                }
            }
        }
        var candidates = CandidateBuilder.Build(tree, dom, w.Rect, webViewRect);
        var recent = Journal.Entries.TakeLast(5)
            .Select(e => Truncate($"{e.Tool} {Summary(e.Args)}", 200))
            .ToList();
        return new Observation(w, candidates, recent);
    }

    private static string Summary(JsonObject args)
    {
        var copy = (JsonObject)args.DeepClone();
        copy.Remove(ResolvedRefsKey);
        return copy.ToJsonString(ToolResult.Json);
    }

    /// <summary>
    /// The connected WebView's DOM belongs to a window only when the window hosts a Chromium render widget AND
    /// that WebView is ours: the window is the process_run process, or the render host's process has the
    /// CDP owner's process name (e.g. msedge.exe after its launcher exited). Any Chromium-based window has a host.
    /// </summary>
    public static bool DomBelongsToWindow(Rect? hostRect, int windowPid, string? hostProcessName, int? webOwnerPid, string? webOwnerProcessName)
    {
        if (hostRect is null || webOwnerPid is null) return false;
        if (webOwnerPid == windowPid) return true;
        return !string.IsNullOrEmpty(hostProcessName) && !string.IsNullOrEmpty(webOwnerProcessName)
               && WindowService.ProcessMatches(hostProcessName, webOwnerProcessName);
    }

    private static string? ProcessNameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    /// <summary>Screen rectangle and owning pid of the WebView2 render host under the tree root, or nulls when there is none.</summary>
    private async Task<(Rect? Rect, int? Pid)> FindWebViewHostAsync(string rootRef)
    {
        if (!Refs.TryGet(rootRef, out var root) || root.Handle is not AutomationElement el) return (null, null);
        try
        {
            return await Dispatcher.RunAsync<(Rect?, int?)>(() =>
            {
                var host = el.FindFirstDescendant(cf => cf.ByClassName("Chrome_RenderWidgetHostHWND"));
                if (host is null) return (null, null);
                var r = host.Properties.BoundingRectangle.ValueOrDefault;
                Rect? rect = r.Width > 0 && r.Height > 0 ? new Rect(r.X, r.Y, r.Width, r.Height) : null;
                return (rect, WindowService.ProcessIdOf(host.Properties.NativeWindowHandle.ValueOrDefault));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "WebView2 host lookup failed");
            return (null, null);
        }
    }

    // ---- IWindowCapture -------------------------------------------------------------------------

    /// <summary>Full-resolution capture of the observed window for visual grounding (null when it cannot be captured).</summary>
    public async Task<VisualFrame?> CaptureAsync(WindowInfo window)
    {
        try
        {
            return VisualFrame.FromCapture(await Screen.CaptureWindowAsync(window.Hwnd, 1.0).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "capture of window {Hwnd} failed", window.Hwnd);
            return null;
        }
    }

    // ---- IActionExecutor ------------------------------------------------------------------------

    /// <summary>Acts at a screen point (visual grounding): the window under <see cref="LastWindow"/> is activated first.</summary>
    public async Task ExecuteAtAsync(int x, int y, ActKind kind, string? text)
    {
        if (kind is ActKind.Type or ActKind.Select && text is null)
            throw new ArgumentException($"Action '{kind}' requires text.", nameof(text));
        if (LastWindow is { } h) await Windows.ActivateAsync(h).ConfigureAwait(false);
        await Input.ClickAsync(x, y).ConfigureAwait(false);
        if (kind is ActKind.Type or ActKind.Select) await Input.TypeTextAsync(text!).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(Candidate target, ActKind kind, string? text)
    {
        if (kind is ActKind.Type or ActKind.Select && text is null)
            throw new ArgumentException($"Action '{kind}' requires text.", nameof(text));
        if (IsPointRef(target.Id))
        {
            var (x, y) = await PointOfAsync(GetRef(target.Id)).ConfigureAwait(false);
            await ExecuteAtAsync(x, y, kind, text).ConfigureAwait(false);
        }
        else if (IsWebRef(target.Id))
        {
            switch (kind)
            {
                case ActKind.Type: await Web.FillAsync(target.Id, text!).ConfigureAwait(false); break;
                case ActKind.Select: await Web.SelectOptionAsync(target.Id, text!).ConfigureAwait(false); break;
                default: await Web.ClickAsync(target.Id).ConfigureAwait(false); break; // Click, Toggle, Auto
            }
        }
        else if (IsUiaRef(target.Id))
        {
            switch (kind)
            {
                case ActKind.Type: await Uia.SetTextAsync(target.Id, text!).ConfigureAwait(false); break;
                case ActKind.Select: await Uia.SelectItemAsync(target.Id, text!).ConfigureAwait(false); break;
                case ActKind.Toggle when target.Checked is { } on:
                    await Uia.SetCheckedAsync(target.Id, !on).ConfigureAwait(false); break;
                // Click, Auto, and Toggle with unknown state (Invoke falls back to the Toggle pattern).
                default: await Uia.InvokeAsync(target.Id).ConfigureAwait(false); break;
            }
        }
        else
        {
            throw new ArgumentException($"Ref '{target.Id}' cannot be acted on.");
        }
    }

    /// <summary>Starts recording the kind of the next grounded action executed in this async flow.</summary>
    public StrongBox<ActKind?> BeginKindCapture()
    {
        var box = new StrongBox<ActKind?>();
        _kindCapture.Value = box;
        return box;
    }

    private sealed class KindCapturingExecutor(IActionExecutor inner, ToolContext owner) : IActionExecutor
    {
        public async Task ExecuteAsync(Candidate target, ActKind kind, string? text)
        {
            await inner.ExecuteAsync(target, kind, text).ConfigureAwait(false);
            if (owner._kindCapture.Value is { } box) box.Value = kind;
        }

        public async Task ExecuteAtAsync(int x, int y, ActKind kind, string? text)
        {
            await inner.ExecuteAtAsync(x, y, kind, text).ConfigureAwait(false);
            if (owner._kindCapture.Value is { } box) box.Value = kind;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Web.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        Uia.Dispose();
        Dispatcher.Dispose();
    }
}

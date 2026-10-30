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
using WinUse.Core.Java;
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
    private static readonly Regex JavaRefPattern = new(@"^j\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex AnyRefPattern = new(@"^(u|w|win|pt|j)\d+$", RegexOptions.CultureInvariant);

    /// <summary>Arguments that may carry a ref; their locators are journaled under <see cref="ResolvedRefsKey"/>.</summary>
    public static readonly string[] RefArgNames = ["ref", "from_ref", "to_ref", "window", "scope", "title"];

    /// <summary>Journaled-args key holding <c>{ argName: Locator }</c> for every ref argument of a call.</summary>
    public const string ResolvedRefsKey = "resolved_refs";

    private readonly AsyncLocal<StrongBox<ActKind?>?> _kindCapture = new();
    private int _disposed;

    private ToolContext(WinUseConfig cfg, ILoggerFactory lf, IJevClient? jev, IObservationSource? observation, IActionExecutor? executor,
        IWindowCapture? capture, IJabBridge? jab)
    {
        Config = cfg;
        Log = lf.CreateLogger("WinUse.Tools");
        Dispatcher = new AutomationDispatcher();
        Refs = new RefRegistry();
        Windows = new WindowService(Dispatcher);
        Input = new InputService(Dispatcher);
        Clipboard = new ClipboardService(Dispatcher);
        Screen = new ScreenService(Dispatcher);
        Processes = new ProcessService(cfg);
        Files = new FileService(cfg);
        Uia = new UiaService(Dispatcher, Refs, Windows);
        Web = new WebService(Refs);
        Java = new JavaService(jab ?? new JabBridge(cfg, lf.CreateLogger<JabBridge>()), Refs, Windows, Input, Log);
        Jev = jev ?? new JevClient(new HttpClient(), cfg, lf.CreateLogger<JevClient>());
        Executor = executor ?? this;
        Grounder = new Grounder(Jev, observation ?? this, new KindCapturingExecutor(Executor, this), cfg, capture ?? this, Log);
        Journal = new Journal();
        ActScreenshot = async () => LastWindow is { } h
            ? await Screen.CaptureWindowAsync(h, ActScreenshotScale).ConfigureAwait(false)
            : null;
    }

    public static ToolContext Create(WinUseConfig cfg, ILoggerFactory? loggerFactory = null) =>
        new(cfg, loggerFactory ?? NullLoggerFactory.Instance, null, null, null, null, null);

    /// <summary>The server's package version (<c>Version</c> in Directory.Build.props), reported in results so a caller can tell what the server can do.</summary>
    public static string ServerVersion { get; } =
        (typeof(ToolContext).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
         ?? typeof(ToolContext).Assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];

    /// <summary>Overload with replaceable collaborators (model, observation, execution, capture, Java Access Bridge), mainly for tests.</summary>
    public static ToolContext Create(WinUseConfig cfg, ILoggerFactory? loggerFactory, IJevClient? jev,
        IObservationSource? observation = null, IActionExecutor? executor = null, IWindowCapture? capture = null, IJabBridge? jab = null) =>
        new(cfg, loggerFactory ?? NullLoggerFactory.Instance, jev, observation, executor, capture, jab);

    public WinUseConfig Config { get; }
    public ILogger Log { get; }
    public AutomationDispatcher Dispatcher { get; }
    public RefRegistry Refs { get; }
    public WindowService Windows { get; }
    public InputService Input { get; }
    public ClipboardService Clipboard { get; }
    public ScreenService Screen { get; }
    public ProcessService Processes { get; }
    public FileService Files { get; }
    public UiaService Uia { get; }
    public WebService Web { get; }
    /// <summary>Swing windows through Java Access Bridge; <c>j{n}</c> refs.</summary>
    public JavaService Java { get; }
    public IJevClient Jev { get; }
    /// <summary>Executes grounded actions; this context itself unless a replacement was injected (tests).</summary>
    public IActionExecutor Executor { get; }
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

    /// <summary>Description of the <c>secret</c> argument of the text-entry tools.</summary>
    public const string SecretArg = "true when the text is a password or other secret: the journal, the log and saved scenarios then hold \"" + SecretPlaceholder
        + "\" instead of the text. A saved scenario step with secret=true must be given the real value as ${NAME} (scenario env or environment variable) before it can be replayed.";

    /// <summary>What the journal stores in place of a secret text argument.</summary>
    public const string SecretPlaceholder = "***";

    /// <summary>The value the journal records for a text argument: the placeholder when it is a secret.</summary>
    public static string Journaled(string text, bool secret) => secret ? SecretPlaceholder : text;

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
    public static bool IsJavaRef(string id) => JavaRefPattern.IsMatch(id);

    public static WindowLocator LocatorOf(WindowInfo w) =>
        new("^" + Regex.Escape(w.Title) + "$", w.ProcessName, w.ClassName);

    public ElementRef RegisterWindow(WindowInfo w) => Refs.Register(RefKind.Window, LocatorOf(w), w.Hwnd);

    /// <summary>
    /// Centre (screen pixels) of the on-screen part of a UIA / Java element ref, or of a window ref. A Java element that is
    /// scrolled out of its viewport or lies beyond the window's edge has no on-screen part: the error says where it is.
    /// </summary>
    public async Task<(int X, int Y)> CenterOfAsync(ElementRef r)
    {
        if (r.Kind == RefKind.Point) return await PointOfAsync(r).ConfigureAwait(false);
        Rect rect;
        switch (r.Kind)
        {
            case RefKind.Uia:
                rect = await Uia.GetRectAsync(r.Id).ConfigureAwait(false);
                break;
            case RefKind.Java:
            {
                var v = await Java.GetVisibilityAsync(r.Id).ConfigureAwait(false);
                if (v.NotClickableReason(r.Id) is { } reason) throw new InvalidOperationException(reason);
                rect = v.VisibleRect;
                break;
            }
            case RefKind.Window:
                rect = (await FindWindowAsync(r).ConfigureAwait(false)).Rect;
                break;
            default:
                throw new NotSupportedException($"Ref '{r.Id}' has no screen rectangle.");
        }
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException($"Ref '{r.Id}' has no visible area.");
        return (rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    /// <summary>
    /// Checks that a mouse click at (<paramref name="x"/>, <paramref name="y"/>) would land on a window of the process that
    /// owns the ref (a popup or dialog of the same process counts), and throws, before anything is clicked, when it would
    /// hit another process's window (the taskbar after the screen shrank, a window covering the target) or no window at all.
    /// Returns the window under the point for the result.
    /// </summary>
    public async Task<object?> EnsureClickLandsOnOwnerAsync(ElementRef r, int x, int y)
    {
        var loc = r.Locator switch
        {
            UiaLocator u => u.Window,
            JavaLocator j => j.Window,
            WindowLocator wl => wl,
            PointLocator pl => pl.Window,
            _ => null,
        };
        if (loc is null) return null;
        var owner = await Windows.FindWithFallbackAsync(loc).ConfigureAwait(false);
        if (owner is null) return null;
        var hit = await Windows.RootWindowAtAsync(x, y).ConfigureAwait(false);
        if (hit is null)
            throw new InvalidOperationException($"The click point ({x},{y}) for ref '{r.Id}' is on no window (outside every monitor?); nothing was clicked. Observe the window again: it may have moved or shrunk.");
        if (hit.Pid != owner.Pid)
            throw new InvalidOperationException(
                $"The click point ({x},{y}) for ref '{r.Id}' is on '{hit.Title}' ({hit.ProcessName}, pid {hit.Pid}), not on a window of {owner.ProcessName} (pid {owner.Pid}); "
                + "nothing was clicked. The element is covered, or its window moved or shrank (a screen resolution change, a scrolled view): observe again, "
                + "scroll it into view or move the window, then click the fresh ref. Use x,y to click a point regardless.");
        return new { title = hit.Title, process = hit.ProcessName, pid = hit.Pid };
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
            JavaLocator j => j.Window,
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
            JavaLocator j => j.Window,
            DomLocator d => d.Window,
            _ => throw new ArgumentException($"Ref '{r.Id}' does not identify a window."),
        };
        return WindowService.PickWithFallback(all, loc)
               ?? throw new InvalidOperationException($"Window for ref '{r.Id}' not found.");
    }

    /// <summary>
    /// How long a title regex that matches no window is retried before <see cref="ResolveWindowAsync"/> gives up: a
    /// page that re-titles itself while it renders (an SPA after navigation) stops matching for a moment, and the title
    /// <c>wait_for</c> just matched must still resolve in the next call.
    /// </summary>
    public static readonly TimeSpan TitleGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Resolves a window from a ref (<c>win{n}</c>, or the owner of an element ref), else a title regex
    /// (a <c>win{n}</c> ref is also accepted in place of the title; a regex that matches nothing is retried for
    /// <see cref="TitleGrace"/>). With neither, falls back to <see cref="LastWindow"/> and then the foreground window
    /// when <paramref name="fallback"/> is set.
    /// </summary>
    public async Task<WindowInfo> ResolveWindowAsync(string? refId, string? title, bool fallback)
    {
        if (!string.IsNullOrEmpty(refId)) return await FindWindowAsync(GetRef(refId)).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(title))
        {
            if (Refs.TryGet(title, out var r) && r.Kind == RefKind.Window) return await FindWindowAsync(r).ConfigureAwait(false);
            return await Windows.WaitForAsync(new WindowLocator(title, null, null), TitleGrace).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"No window matches '{title}' (retried for {TitleGrace.TotalSeconds:0.#} s). Prefer the win{{n}} ref a previous tool returned for this window.");
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
        var tree = await UiaService.RetryIfChildlessAsync(() => GetTreeAsync(w, new TreeOptions()), UiaService.EmptyTreeRetryDelay)
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
        // A Java window that yielded nothing explains itself (bridge missing, not responding), so act / find can say why.
        return new Observation(w, candidates, recent, candidates.Count == 0 ? Java.UnavailableReason(w) : null);
    }

    /// <summary>The element tree of a window: through Java Access Bridge for a Swing window the bridge can read, else UIA.</summary>
    public async Task<UiNode> GetTreeAsync(WindowInfo w, TreeOptions opt) =>
        await Java.IsJavaWindowAsync(w).ConfigureAwait(false)
            ? await Java.GetTreeAsync(w.Hwnd, opt).ConfigureAwait(false)
            : await Uia.GetTreeAsync(w.Hwnd, opt).ConfigureAwait(false);

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
        else if (IsJavaRef(target.Id))
        {
            switch (kind)
            {
                case ActKind.Type: await Java.SetTextAsync(target.Id, text!).ConfigureAwait(false); break;
                case ActKind.Select: await Java.SelectItemAsync(target.Id, text!).ConfigureAwait(false); break;
                case ActKind.Toggle when target.Checked is { } on:
                    await Java.SetCheckedAsync(target.Id, !on).ConfigureAwait(false); break;
                // Click, Auto, and Toggle with unknown state: the element's click action toggles it.
                default: await Java.InvokeAsync(target.Id).ConfigureAwait(false); break;
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
        Java.Dispose();
        Uia.Dispose();
        Dispatcher.Dispose();
    }
}

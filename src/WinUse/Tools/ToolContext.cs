using System.Diagnostics;
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
public sealed class ToolStep
{
    public Locator? Resolved { get; set; }
    public bool? Passed { get; set; }
    public List<ContentBlock> Extra { get; } = [];
}

/// <summary>All services shared by the MCP tools (DI singleton). Creating it has no UIA / Playwright side effects.</summary>
public sealed class ToolContext : IObservationSource, IActionExecutor, IDisposable
{
    private static readonly Regex WebRefPattern = new(@"^w\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex UiaRefPattern = new(@"^u\d+$", RegexOptions.CultureInvariant);
    private int _disposed;

    private ToolContext(WinUseConfig cfg, ILoggerFactory lf)
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
        Jev = new JevClient(new HttpClient(), cfg, lf.CreateLogger<JevClient>());
        Grounder = new Grounder(Jev, this, this, cfg);
        Journal = new Journal();
    }

    public static ToolContext Create(WinUseConfig cfg, ILoggerFactory? loggerFactory = null) =>
        new(cfg, loggerFactory ?? NullLoggerFactory.Instance);

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

    // ---- step execution -------------------------------------------------------------------------

    /// <summary>Allocates a step id, runs <paramref name="body"/>, journals the call and converts the outcome to a tool result.</summary>
    public async Task<CallToolResult> RunStepAsync(string tool, JsonObject args, Func<ToolStep, Task<object>> body)
    {
        var stepId = Journal.NextId();
        var step = new ToolStep();
        var sw = Stopwatch.StartNew();
        try
        {
            var payload = await body(step).ConfigureAwait(false);
            sw.Stop();
            Journal.Record(new JournalEntry(stepId, tool, args, step.Resolved, step.Passed, sw.Elapsed, DateTimeOffset.Now));
            var json = ToolResult.Ok(payload, stepId);
            Log.LogInformation("{StepId} {Tool} {Args} -> ok in {Elapsed} ms: {Result}",
                stepId, tool, args.ToJsonString(ToolResult.Json), sw.ElapsedMilliseconds, Truncate(json, 2000));
            return ToolResult.Text(json, step.Extra);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Journal.Record(new JournalEntry(stepId, tool, args, step.Resolved, step.Passed, sw.Elapsed, DateTimeOffset.Now));
            Log.LogWarning(ex, "{StepId} {Tool} {Args} -> error in {Elapsed} ms",
                stepId, tool, args.ToJsonString(ToolResult.Json), sw.ElapsedMilliseconds);
            return ToolResult.Error(ex.Message, stepId);
        }
    }

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

    public static WindowLocator LocatorOf(WindowInfo w) =>
        new("^" + Regex.Escape(w.Title) + "$", w.ProcessName, w.ClassName);

    public ElementRef RegisterWindow(WindowInfo w) => Refs.Register(RefKind.Window, LocatorOf(w), w.Hwnd);

    /// <summary>Centre (screen pixels) of a UIA element ref or a window ref.</summary>
    public async Task<(int X, int Y)> CenterOfAsync(ElementRef r)
    {
        var rect = r.Kind switch
        {
            RefKind.Uia => await Uia.GetRectAsync(r.Id).ConfigureAwait(false),
            RefKind.Window => (await FindWindowAsync(r).ConfigureAwait(false)).Rect,
            _ => throw new NotSupportedException($"Ref '{r.Id}' has no screen rectangle."),
        };
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException($"Ref '{r.Id}' has no visible area.");
        return (rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    /// <summary>Brings the window owning a UIA / window ref to the foreground (best effort) before coordinate input.</summary>
    public async Task ActivateOwnerAsync(ElementRef r)
    {
        var loc = r.Locator switch
        {
            UiaLocator u => u.Window,
            WindowLocator wl => wl,
            _ => null,
        };
        if (loc is null) return;
        var w = await Windows.FindAsync(loc).ConfigureAwait(false);
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
        return all.FirstOrDefault(w => WindowService.Matches(w, loc))
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
        var tree = await Uia.GetTreeAsync(w.Hwnd, new TreeOptions()).ConfigureAwait(false);
        IReadOnlyList<DomNode> dom = [];
        Rect? webViewRect = null;
        if (Web.IsConnected)
        {
            webViewRect = await FindWebViewRectAsync(tree.Ref).ConfigureAwait(false);
            Web.Window = LocatorOf(w);
            dom = await Web.GetDomAsync().ConfigureAwait(false);
        }
        var candidates = CandidateBuilder.Build(tree, dom, w.Rect, webViewRect);
        var recent = Journal.Entries.TakeLast(5)
            .Select(e => Truncate($"{e.Tool} {e.Args.ToJsonString(ToolResult.Json)}", 200))
            .ToList();
        return new Observation(w, candidates, recent);
    }

    /// <summary>Screen rectangle of the WebView2 render host under the tree root, or null when there is none.</summary>
    private async Task<Rect?> FindWebViewRectAsync(string rootRef)
    {
        if (!Refs.TryGet(rootRef, out var root) || root.Handle is not AutomationElement el) return null;
        try
        {
            return await Dispatcher.RunAsync<Rect?>(() =>
            {
                var host = el.FindFirstDescendant(cf => cf.ByClassName("Chrome_RenderWidgetHostHWND"));
                if (host is null) return null;
                var r = host.Properties.BoundingRectangle.ValueOrDefault;
                return r.Width > 0 && r.Height > 0 ? new Rect(r.X, r.Y, r.Width, r.Height) : null;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "WebView2 host lookup failed");
            return null;
        }
    }

    // ---- IActionExecutor ------------------------------------------------------------------------

    public async Task ExecuteAsync(Candidate target, ActKind kind, string? text)
    {
        if (kind is ActKind.Type or ActKind.Select && text is null)
            throw new ArgumentException($"Action '{kind}' requires text.", nameof(text));
        if (IsWebRef(target.Id))
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Web.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        Uia.Dispose();
        Dispatcher.Dispose();
    }
}

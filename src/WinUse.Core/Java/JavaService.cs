using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>
/// Reads and operates Swing windows through Java Access Bridge with the same public surface as <see cref="UiaService"/>.
/// Owns every Java object handle behind a <c>j{n}</c> ref and releases them two observations later; a ref whose handle
/// is gone is re-resolved through its locator, so a released handle is never touched again.
/// </summary>
public sealed partial class JavaService(IJabBridge bridge, RefRegistry refs, WindowService windows, InputService input, ILogger? log = null)
    : IDisposable
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Snapshot, JabHandle Handle)> _owned = new();
    private readonly Dictionary<nint, string?> _probeReason = new();
    private bool _everSeenJava;
    private int _disposed;

    /// <summary>Snapshots a ref's handle survives after its observation before it is released.</summary>
    public const int HandleRetentionSnapshots = 2;

    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int ProbeRetries = 10;

    // ---- test seams (defaults use the real window / input services) ---------------------------

    internal Func<nint, Task<WindowInfo?>> WindowLookup { get; set; } =
        async hwnd => (await windows.ListAsync().ConfigureAwait(false)).FirstOrDefault(w => w.Hwnd == hwnd);

    internal Func<WindowLocator, Task<WindowInfo?>> WindowFind { get; set; } = windows.FindWithFallbackAsync;

    internal Func<nint, double> DpiScale { get; set; } = hwnd =>
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    };

    internal Func<int, int, Task> ClickAt { get; set; } = (x, y) => input.ClickAsync(x, y);

    internal Func<KeyChord, Task> Press { get; set; } = input.PressAsync;

    // ---- bridge facts (doctor) ------------------------------------------------------------------

    /// <summary>Loads the bridge on first call; false when the DLL is missing, fails to load, or WINUSE_JAB=off.</summary>
    public bool BridgeAvailable => bridge.IsAvailable;
    public string? BridgeUnavailableReason => bridge.UnavailableReason;
    public string? BridgeDllPath => bridge.DllPath;

    // ---- window probing -------------------------------------------------------------------------

    /// <summary>Cheap pre-check: AWT top-level windows have class names SunAwtFrame / SunAwtDialog / SunAwtWindow.</summary>
    public static bool LooksLikeJavaWindow(WindowInfo w) => w.ClassName.StartsWith("SunAwt", StringComparison.Ordinal);

    /// <summary>
    /// Why <c>getAccessibleContextFromHWND</c> answered nothing for a window whose JVM does answer <c>isJavaWindow</c>:
    /// usually the legacy Java-side bridge (see <see cref="JavaProcessInfo.LegacyBridgeModule"/>).
    /// </summary>
    internal static string NoContextMessage(int pid)
    {
        var info = JavaProcessInfo.Inspect(pid);
        if (info.IsLegacyBridge)
            return $"Java Access Bridge returned no accessible context for the window: the JVM (pid {pid}) loaded the legacy Java-side bridge "
                   + $"({JavaProcessInfo.LegacyBridgeModule}, 32-bit object handles), which answers isJavaWindow but drops every request from the 64-bit client. "
                   + "The tree cannot be read; a JRE with JavaAccessBridge-32.dll and access-bridge-32.jar would be readable.";
        return $"Java Access Bridge returned no accessible context for the window (JVM pid {pid}, bridge module {info.LoadedBridgeModule ?? "not loaded"}).";
    }

    /// <summary>
    /// True when the window is an AWT window whose JVM answers through the bridge. Until the first JVM has ever
    /// answered, a negative answer is retried for about a second (the JVMs respond asynchronously to <c>Windows_run</c>).
    /// </summary>
    public async Task<bool> IsJavaWindowAsync(WindowInfo w)
    {
        if (!LooksLikeJavaWindow(w)) return false;
        if (!bridge.IsAvailable)
        {
            Record(w.Hwnd, bridge.UnavailableReason ?? "unavailable");
            return false;
        }
        var attempts = _everSeenJava ? 1 : ProbeRetries;
        for (var i = 0; i < attempts; i++)
        {
            if (await bridge.RunAsync(j => j.IsJavaWindow(w.Hwnd)).ConfigureAwait(false))
            {
                _everSeenJava = true;
                Record(w.Hwnd, null);
                return true;
            }
            if (i + 1 < attempts) await Task.Delay(ProbeRetryDelay).ConfigureAwait(false);
        }
        Record(w.Hwnd, "bridge not responding (accessibility not enabled when the app started? run: winuse doctor)");
        _log.LogWarning("Java window 0x{Hwnd:X} '{Title}' does not answer through Java Access Bridge; falling back to UIA", w.Hwnd, w.Title);
        return false;
    }

    /// <summary>Why the last probe of this Java window failed; null for non-Java windows and for windows the bridge reads.</summary>
    public string? UnavailableReason(WindowInfo w)
    {
        if (!LooksLikeJavaWindow(w)) return null;
        lock (_gate) return _probeReason.TryGetValue(w.Hwnd, out var r) ? r : bridge.UnavailableReason;
    }

    private void Record(nint hwnd, string? reason)
    {
        lock (_gate) _probeReason[hwnd] = reason;
    }

    // ---- tree -----------------------------------------------------------------------------------

    public async Task<UiNode> GetTreeAsync(nint hwnd, TreeOptions opt)
    {
        opt ??= new TreeOptions();
        EnsureAvailable();
        refs.BeginSnapshot();
        var snapshot = refs.CurrentSnapshot;
        await ReleaseOlderThanAsync(snapshot - HandleRetentionSnapshots).ConfigureAwait(false);

        var window = await WindowLookup(hwnd).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No window with handle 0x{hwnd:X}.");
        WindowLocator wloc;
        IReadOnlyList<JavaPathSegment> basePath = [];
        JabHandle? scope = null;
        if (opt.ScopeRef is not null)
        {
            var sr = GetRef(opt.ScopeRef);
            var sl = (JavaLocator)sr.Locator;
            wloc = sl.Window;
            basePath = sl.Path;
            scope = await LiveAsync(opt.ScopeRef).ConfigureAwait(false);
        }
        else
        {
            wloc = new WindowLocator("^" + System.Text.RegularExpressions.Regex.Escape(window.Title) + "$", window.ProcessName, window.ClassName);
        }
        var dpi = DpiScale(hwnd);

        return await bridge.RunAsync(jab =>
        {
            var windowRoot = jab.GetContextFromHwnd(hwnd)
                ?? throw new InvalidOperationException(NoContextMessage(window.Pid));
            var tx = Calibrate(jab, windowRoot, window.Rect, dpi);
            var root = scope ?? windowRoot;
            var builder = new JavaTreeBuilder(jab, refs, wloc, opt, tx, (id, h) => Own(id, h, snapshot));
            try
            {
                var tree = builder.BuildRoot(root, basePath, keepRoot: scope is null);
                if (builder.Truncated)
                    _log.LogWarning("Java tree of window 0x{Hwnd:X} truncated at {Max} nodes", hwnd, JavaTreeBuilder.MaxNodes);
                return tree;
            }
            finally
            {
                if (scope is not null) jab.Release(windowRoot);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Calibrates Java coordinates against the real window rectangle (see <see cref="JavaGeometry"/>).</summary>
    private static JavaTransform Calibrate(IJabAccess jab, JabHandle windowRoot, Rect windowRect, double dpi)
    {
        var info = jab.GetInfo(windowRoot);
        (int, int, int, int)? root = info is not null && JavaStates.Parse(info.States).Showing && info.Width > 0
            ? (info.X, info.Y, info.Width, info.Height)
            : null;
        return JavaGeometry.Calibrate(windowRect, root, dpi);
    }

    private void Own(string refId, JabHandle h, int snapshot)
    {
        lock (_gate) _owned[refId] = (snapshot, h);
    }

    private async Task ReleaseOlderThanAsync(int maxSnapshot)
    {
        List<JabHandle> stale;
        lock (_gate)
        {
            stale = _owned.Where(kv => kv.Value.Snapshot <= maxSnapshot).Select(kv => kv.Value.Handle).ToList();
            foreach (var id in _owned.Where(kv => kv.Value.Snapshot <= maxSnapshot).Select(kv => kv.Key).ToList()) _owned.Remove(id);
        }
        if (stale.Count == 0) return;
        await bridge.RunAsync(jab =>
        {
            foreach (var h in stale) jab.Release(h);
            return 0;
        }).ConfigureAwait(false);
    }

    // ---- ref resolution -------------------------------------------------------------------------

    private ElementRef GetRef(string refId) =>
        refs.TryGet(refId, out var r) && r.Kind == RefKind.Java
            ? r
            : throw new KeyNotFoundException($"Unknown Java ref '{refId}'.");

    private void EnsureAvailable()
    {
        if (!bridge.IsAvailable) throw new InvalidOperationException($"Java Access Bridge is not available: {bridge.UnavailableReason}");
    }

    /// <summary>
    /// The live handle for a ref: the owned one when the bridge still knows it, else a fresh resolution through the
    /// locator (which replaces the owned handle).
    /// </summary>
    private async Task<JabHandle> LiveAsync(string refId)
    {
        var r = GetRef(refId);
        EnsureAvailable();
        JabHandle? owned;
        lock (_gate) owned = _owned.TryGetValue(refId, out var e) ? e.Handle : null;
        if (owned is { } h && await bridge.RunAsync(j => j.GetInfo(h) is not null).ConfigureAwait(false)) return h;

        var loc = (JavaLocator)r.Locator;
        var resolved = await ResolveAsync(loc).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Element for ref '{refId}' no longer exists.");
        JabHandle? old;
        lock (_gate)
        {
            old = _owned.TryGetValue(refId, out var e) ? e.Handle : null;
            _owned[refId] = (refs.CurrentSnapshot, resolved);
        }
        if (old is { } o) await bridge.RunAsync(j => { j.Release(o); return 0; }).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>Resolves a locator to a new handle the caller owns, or null. Internal: refs own handles through <see cref="RegisterResolvedAsync"/>.</summary>
    internal async Task<JabHandle?> ResolveAsync(JavaLocator loc)
    {
        if (!bridge.IsAvailable) return null;
        var w = await WindowFind(loc.Window).ConfigureAwait(false);
        if (w is null) return null;
        return await bridge.RunAsync(jab =>
        {
            if (jab.GetContextFromHwnd(w.Hwnd) is not { } root) return (JabHandle?)null;
            var h = JavaLocatorResolver.Resolve(jab, root, loc.Path);
            if (h != root) jab.Release(root);
            return h;
        }).ConfigureAwait(false);
    }

    /// <summary>Registers a <c>j{n}</c> ref for a recorded locator; with <paramref name="lazy"/> even when it cannot be found now.</summary>
    public async Task<string?> RegisterResolvedAsync(JavaLocator loc, bool lazy)
    {
        var h = await ResolveAsync(loc).ConfigureAwait(false);
        if (h is null && !lazy) return null;
        var r = refs.Register(RefKind.Java, loc, null);
        if (h is { } hh) Own(r.Id, hh, refs.CurrentSnapshot);
        return r.Id;
    }

    private async Task<T> WithAsync<T>(string refId, Func<IJabAccess, JabHandle, T> f)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        return await bridge.RunAsync(jab => f(jab, h)).ConfigureAwait(false);
    }

    /// <summary>Coordinate transform for the window that owns a ref.</summary>
    private async Task<JavaTransform> TransformForAsync(string refId)
    {
        var loc = (JavaLocator)GetRef(refId).Locator;
        var w = await WindowFind(loc.Window).ConfigureAwait(false);
        if (w is null) return new JavaTransform(1, 0, 0);
        var dpi = DpiScale(w.Hwnd);
        return await bridge.RunAsync(jab =>
        {
            if (jab.GetContextFromHwnd(w.Hwnd) is not { } root) return new JavaTransform(dpi, 0, 0);
            try { return Calibrate(jab, root, w.Rect, dpi); }
            finally { jab.Release(root); }
        }).ConfigureAwait(false);
    }

    // ---- reads ----------------------------------------------------------------------------------

    public async Task<UiNode> GetElementAsync(string refId)
    {
        var tx = await TransformForAsync(refId).ConfigureAwait(false);
        return await WithAsync(refId, (jab, h) =>
        {
            var info = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            var ct = JavaRoleMap.ControlTypeOf(info.Role, ParentRoleOf(refId));
            var flags = JavaStates.Parse(info.States);
            var rect = flags.Showing ? tx.Apply(info.X, info.Y, info.Width, info.Height) : default;
            return JavaTreeBuilder.MakeNode(jab, h, info, refId, ct, JavaTreeBuilder.NameOf(jab, h, info, ct), 0, [], rect);
        }).ConfigureAwait(false);
    }

    public async Task<Rect> GetRectAsync(string refId) => (await GetElementAsync(refId).ConfigureAwait(false)).Rect;

    public Task<string> GetTextAsync(string refId) => WithAsync(refId, (jab, h) =>
    {
        var info = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
        if (info.HasText && jab.GetText(h, 4000) is { } t) return t;
        var ct = JavaRoleMap.ControlTypeOf(info.Role, ParentRoleOf(refId));
        return JavaTreeBuilder.ReadValue(jab, h, ct) ?? JavaTreeBuilder.NameOf(jab, h, info, ct) ?? "";
    });

    /// <summary>The JAB role of the parent, from the ref's own path (control types of list / tab / tree items depend on it).</summary>
    private string? ParentRoleOf(string refId)
    {
        var path = ((JavaLocator)GetRef(refId).Locator).Path;
        return path.Count >= 2 ? path[^2].Role : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<JabHandle> all;
        lock (_gate)
        {
            all = _owned.Values.Select(v => v.Handle).ToList();
            _owned.Clear();
        }
        try
        {
            if (all.Count > 0 && bridge.IsAvailable)
                bridge.RunAsync(jab => { foreach (var h in all) jab.Release(h); return 0; }).Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        bridge.Dispose();
    }
}

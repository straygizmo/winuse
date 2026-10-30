using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Uia;

public sealed class UiaService(AutomationDispatcher d, RefRegistry refs, WindowService windows) : IDisposable
{
    private readonly InputService _input = new(d);
    private UIA3Automation? _auto; // only touched on the dispatcher thread
    private int _disposed;

    private UIA3Automation Auto => _auto ??= new UIA3Automation();

    // ---- tree -------------------------------------------------------------------------------

    /// <summary>Wait before re-reading a window tree whose root came back without children.</summary>
    public static readonly TimeSpan EmptyTreeRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Runs <paramref name="get"/>; when the root has no children (the first UIA read of a Chromium / Office window
    /// often returns only the root) waits <paramref name="delay"/> and reads exactly once more.
    /// </summary>
    public static async Task<UiNode> RetryIfChildlessAsync(Func<Task<UiNode>> get, TimeSpan delay)
    {
        var tree = await get().ConfigureAwait(false);
        if (tree.Children.Count > 0) return tree;
        await Task.Delay(delay).ConfigureAwait(false);
        return await get().ConfigureAwait(false);
    }

    /// <summary>
    /// True when the tree holds a focused <c>Window</c> node (a modal dialog that just opened) with a visible area and no
    /// children: its content has not been populated yet and a second read is worth a short wait.
    /// </summary>
    public static bool HasEmptyFocusedWindow(UiNode tree)
    {
        foreach (var c in tree.Children)
        {
            if (c.ControlType == "Window" && c.Focused && c.Children.Count == 0 && c.Rect.Width > 0 && c.Rect.Height > 0) return true;
            if (HasEmptyFocusedWindow(c)) return true;
        }
        return false;
    }

    public async Task<UiNode> GetTreeAsync(nint hwnd, TreeOptions opt)
    {
        opt ??= new TreeOptions();
        refs.BeginSnapshot();

        WindowLocator wloc;
        IReadOnlyList<UiaPathSegment> basePath = [];
        AutomationElement? scopeEl = null;
        if (opt.ScopeRef is not null)
        {
            if (!refs.TryGet(opt.ScopeRef, out var sr) || sr.Locator is not UiaLocator sl)
                throw new KeyNotFoundException($"Unknown UIA ref '{opt.ScopeRef}'.");
            wloc = sl.Window;
            basePath = sl.Path;
            scopeEl = await LiveAsync(opt.ScopeRef).ConfigureAwait(false);
        }
        else
        {
            var info = (await windows.ListAsync().ConfigureAwait(false)).FirstOrDefault(w => w.Hwnd == hwnd)
                ?? throw new InvalidOperationException($"No window with handle 0x{hwnd:X}.");
            wloc = new WindowLocator("^" + Regex.Escape(info.Title) + "$", info.ProcessName, info.ClassName);
        }

        return await d.RunAsync(() =>
        {
            var start = scopeEl ?? Auto.FromHandle(hwnd);
            return new TreeBuilder(refs, wloc, opt).BuildRoot(start, basePath);
        }).ConfigureAwait(false);
    }

    private sealed class TreeBuilder(RefRegistry refs, WindowLocator wloc, TreeOptions opt)
    {
        public UiNode BuildRoot(AutomationElement el, IReadOnlyList<UiaPathSegment> path)
        {
            var r = refs.Register(RefKind.Uia, new UiaLocator(wloc, path), el);
            var children = Children(el, path, 0, 0);
            return MakeNode(r.Id, el, 0, children, withValue: true, readValues: opt.ReadValues);
        }

        private List<UiNode> Children(AutomationElement parent, IReadOnlyList<UiaPathSegment> parentPath, int realDepth, int outDepth)
        {
            var result = new List<UiNode>();
            if (realDepth >= opt.MaxDepth) return result;
            AutomationElement[] kids;
            try { kids = parent.FindAllChildren(); } catch { return result; }

            var counts = new Dictionary<(string, string?, string?), int>();
            foreach (var k in kids)
            {
                // Never skip a sibling here: the resolver counts every child, so indexes must agree.
                var type = UiaLocatorResolver.TypeOf(k);
                var id = UiaLocatorResolver.Id(k);
                var name = UiaLocatorResolver.NameOf(k);
                var offscreen = Safe(() => k.Properties.IsOffscreen.ValueOrDefault, false);

                var key = (type, id, name);
                counts.TryGetValue(key, out var idx);
                counts[key] = idx + 1;

                if (offscreen && !opt.IncludeOffscreen) continue;

                var path = new List<UiaPathSegment>(parentPath) { new(type, id, name, idx) };
                var keep = !opt.InteractiveOnly
                    || InteractiveFilter.IsInteractive(type)
                    || (type == "Text" && !string.IsNullOrWhiteSpace(name));
                if (keep)
                {
                    var r = refs.Register(RefKind.Uia, new UiaLocator(wloc, path), k);
                    var sub = opt.InteractiveOnly && type == "Text"
                        ? new List<UiNode>()
                        : Children(k, path, realDepth + 1, outDepth + 1);
                    result.Add(MakeNode(r.Id, k, outDepth + 1, sub, withValue: false, readValues: opt.ReadValues));
                }
                else if (type == "Pane")
                {
                    // A Pane is retained only when its subtree yields kept children.
                    var sub = Children(k, path, realDepth + 1, outDepth + 1);
                    if (sub.Count > 0)
                    {
                        var r = refs.Register(RefKind.Uia, new UiaLocator(wloc, path), k);
                        result.Add(MakeNode(r.Id, k, outDepth + 1, sub, withValue: false, readValues: opt.ReadValues));
                    }
                }
                else
                {
                    // Collapse: hoist the kept descendants to this level.
                    result.AddRange(Children(k, path, realDepth + 1, outDepth));
                }
            }
            return result;
        }
    }

    private static UiNode MakeNode(string refId, AutomationElement el, int depth, IReadOnlyList<UiNode> children, bool withValue, bool readValues = true)
    {
        var type = UiaLocatorResolver.TypeOf(el);
        var readValue = readValues && (withValue || type is "Edit" or "ComboBox" or "Slider" or "Spinner");
        string? value = null;
        if (readValue)
        {
            try
            {
                var vp = el.Patterns.Value.PatternOrDefault;
                if (vp is not null) value = Truncate(vp.Value.ValueOrDefault);
            }
            catch { }
        }
        return new UiNode(
            refId, type, UiaLocatorResolver.NameOf(el), UiaLocatorResolver.Id(el), value,
            Safe(() => el.Properties.IsEnabled.ValueOrDefault, true),
            Safe(() => el.Properties.IsOffscreen.ValueOrDefault, false),
            CheckedOf(el, type),
            Safe(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false),
            RectOf(el), depth, children);
    }

    private static string? Truncate(string? s) => s is { Length: > 500 } ? s[..500] : s;

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    private static Rect RectOf(AutomationElement el)
    {
        var r = Safe(() => el.Properties.BoundingRectangle.ValueOrDefault, default);
        return new Rect(r.X, r.Y, r.Width, r.Height);
    }

    private static bool? CheckedOf(AutomationElement el, string type)
    {
        try
        {
            var t = el.Patterns.Toggle.PatternOrDefault;
            if (t is not null)
                return t.ToggleState.ValueOrDefault switch
                {
                    ToggleState.On => true,
                    ToggleState.Off => false,
                    _ => null,
                };
            if (type is "RadioButton")
                return el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault;
        }
        catch { }
        return null;
    }

    // ---- ref resolution -----------------------------------------------------------------------

    private ElementRef GetRef(string refId) =>
        refs.TryGet(refId, out var r) && r.Kind == RefKind.Uia
            ? r
            : throw new KeyNotFoundException($"Unknown UIA ref '{refId}'.");

    private static bool IsAlive(AutomationElement el)
    {
        try { _ = el.Properties.ControlType.Value; return true; } catch { return false; }
    }

    /// <summary>Gets the live element for a ref, re-resolving through its locator when the handle went stale.</summary>
    private async Task<AutomationElement> LiveAsync(string refId)
    {
        var r = GetRef(refId);
        if (r.Handle is AutomationElement h && await d.RunAsync(() => IsAlive(h)).ConfigureAwait(false)) return h;
        return await ResolveAsync((UiaLocator)r.Locator).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Element for ref '{refId}' no longer exists.");
    }

    private async Task<T> WithAsync<T>(string refId, Func<AutomationElement, T> f)
    {
        var el = await LiveAsync(refId).ConfigureAwait(false);
        return await d.RunAsync(() => f(el)).ConfigureAwait(false);
    }

    public async Task<AutomationElement?> ResolveAsync(UiaLocator loc)
    {
        var w = await windows.FindAsync(loc.Window).ConfigureAwait(false);
        if (w is null && loc.Window.ProcessName is not null)
        {
            // Titles change (e.g. an unsaved-document marker); fall back to process + class when that is unambiguous.
            var same = (await windows.ListAsync(null, loc.Window.ProcessName).ConfigureAwait(false))
                .Where(x => loc.Window.ClassName is null
                            || string.Equals(x.ClassName, loc.Window.ClassName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (same.Count == 1) w = same[0];
        }
        if (w is null) return null;
        return await d.RunAsync(() => UiaLocatorResolver.Resolve(Auto.FromHandle(w.Hwnd), loc.Path)).ConfigureAwait(false);
    }

    // ---- reads --------------------------------------------------------------------------------

    public Task<UiNode> GetElementAsync(string refId) =>
        WithAsync(refId, el => MakeNode(refId, el, 0, [], withValue: true));

    public Task<Rect> GetRectAsync(string refId) => WithAsync(refId, RectOf);

    public Task<string> GetTextAsync(string refId) => WithAsync(refId, el =>
    {
        try
        {
            var v = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch { }
        try
        {
            var tp = el.Patterns.Text.PatternOrDefault;
            if (tp is not null) return tp.DocumentRange.GetText(-1);
        }
        catch { }
        return UiaLocatorResolver.NameOf(el) ?? "";
    });

    // ---- actions ------------------------------------------------------------------------------

    /// <summary>
    /// Sets the text of a field and reports the path taken: <see cref="TextEntryPath.ValuePattern"/> (the Value pattern took
    /// it, no key events) or <see cref="TextEntryPath.Keyboard"/> (focus, select all, type). The text itself is never reported.
    /// </summary>
    public async Task<string> SetTextAsync(string refId, string text, TextEntryMethod method = TextEntryMethod.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        var el = await LiveAsync(refId).ConfigureAwait(false);
        if (method != TextEntryMethod.Keyboard)
        {
            var done = await d.RunAsync(() =>
            {
                try
                {
                    var vp = el.Patterns.Value.PatternOrDefault;
                    if (vp is not null && !vp.IsReadOnly.ValueOrDefault)
                    {
                        vp.SetValue(text);
                        return true;
                    }
                }
                catch { }
                return false;
            }).ConfigureAwait(false);
            if (done) return TextEntryPath.ValuePattern;
            if (method == TextEntryMethod.Api)
                throw new NotSupportedException($"Ref '{refId}' has no writable Value pattern; use method \"keyboard\".");
        }

        var loc = (UiaLocator)GetRef(refId).Locator;
        var w = await windows.FindAsync(loc.Window).ConfigureAwait(false);
        if (w is not null) await windows.ActivateAsync(w.Hwnd).ConfigureAwait(false);
        await d.RunAsync(() => el.Focus()).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await _input.PressAsync(KeyChord.Parse("ctrl+a")).ConfigureAwait(false);
        if (text.Length == 0) await _input.PressAsync(KeyChord.Parse("delete")).ConfigureAwait(false);
        else await _input.TypeTextAsync(text, 0).ConfigureAwait(false);
        return TextEntryPath.Keyboard;
    }

    /// <summary>How long <see cref="SelectItemAsync"/> keeps re-reading the selection before calling it a mismatch.</summary>
    public static readonly TimeSpan SelectReadBackWindow = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SelectReadBackStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Selects the item by label (exact, then substring) and reads the container's selection back: the container's
    /// Selection pattern, the item's SelectionItem state and, for a combo box (Qt / Win32 / WPF expose the current text
    /// there), the Value pattern. A combo box item is clicked with the mouse while the popup is open, the way a user
    /// commits it (selecting it through the SelectionItem pattern and collapsing the popup leaves Win32 and Qt combo
    /// boxes on their old value); a list / tab / tree item goes through its SelectionItem pattern. When the read-back
    /// does not name the item within <see cref="SelectReadBackWindow"/> the other path is tried (unless
    /// <paramref name="singleAction"/>, which stops after the first path that delivered an input); a read-back that still
    /// names something else fails with the value seen and the paths that acted. A container with no readable selection
    /// gives <c>unknown</c>. The result counts the inputs delivered in <see cref="SelectItemResult.Actions"/>.
    /// </summary>
    public async Task<SelectItemResult> SelectItemAsync(string refId, string item, bool singleAction = false)
    {
        var el = await LiveAsync(refId).ConfigureAwait(false);
        var type = await d.RunAsync(() => UiaLocatorResolver.TypeOf(el)).ConfigureAwait(false);
        var combo = type == "ComboBox";
        string? seen = null, name = null;
        var acted = new List<string>();
        foreach (var method in combo ? new[] { SelectPath.Mouse, SelectPath.Selection } : new[] { SelectPath.Selection, SelectPath.Mouse })
        {
            if (combo && method == SelectPath.Mouse)
            {
                // The popup must be in front of everything for the click to land on it.
                var loc = (UiaLocator)GetRef(refId).Locator;
                if (await windows.FindAsync(loc.Window).ConfigureAwait(false) is { } w) await windows.ActivateAsync(w.Hwnd).ConfigureAwait(false);
            }
            var target = await FindItemAsync(refId, el, type, combo, item).ConfigureAwait(false);
            name = target.Name;
            bool done;
            try
            {
                done = method == SelectPath.Mouse
                    ? await ClickItemAsync(target.Element).ConfigureAwait(false)
                    : await d.RunAsync(() => SelectThroughPattern(target.Element)).ConfigureAwait(false);
            }
            finally
            {
                if (combo) await d.RunAsync(() => { try { el.Patterns.ExpandCollapse.PatternOrDefault?.Collapse(); } catch { } return 0; }).ConfigureAwait(false);
            }
            if (!done) continue;
            acted.Add(method);

            var deadline = DateTime.UtcNow + SelectReadBackWindow;
            while (true)
            {
                var (confirmed, value, readable) = await d.RunAsync(() => ReadSelection(el, target.Element, name, combo)).ConfigureAwait(false);
                if (confirmed) return SelectItemResult.Did(method, name, value, acted.Count);
                if (!readable)
                    return SelectItemResult.Unverified(method, name, $"the {type} exposes no Selection or Value pattern and the item no SelectionItem state", acted.Count);
                seen = value;
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(SelectReadBackStep).ConfigureAwait(false);
            }
            if (singleAction) break;
        }
        if (acted.Count == 0) throw new InvalidOperationException($"Item '{item}' in {type} (ref {refId}) can be neither selected through a pattern nor clicked; no action was delivered.");
        throw SelectItemResult.Mismatch(refId, type, name, seen, acted, singleAction);
    }

    /// <summary>Opens a combo box's popup (and lets it settle) and finds the item by label: exact first, then a case-insensitive substring.</summary>
    private async Task<(AutomationElement Element, string? Name)> FindItemAsync(string refId, AutomationElement el, string type, bool combo, string item)
    {
        if (combo)
        {
            await d.RunAsync(() => { el.Patterns.ExpandCollapse.PatternOrDefault?.Expand(); return 0; }).ConfigureAwait(false);
            await Task.Delay(ComboPopupDelay).ConfigureAwait(false);
        }
        return await d.RunAsync(() =>
        {
            var candidates = el.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Concat(el.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)))
                .Concat(el.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)))
                .ToList();
            var target = candidates.FirstOrDefault(c => UiaLocatorResolver.NameOf(c) == item)
                ?? candidates.FirstOrDefault(c => (UiaLocatorResolver.NameOf(c) ?? "").Contains(item, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                if (combo) try { el.Patterns.ExpandCollapse.PatternOrDefault?.Collapse(); } catch { }
                throw new InvalidOperationException($"Item '{item}' not found in {type} (ref {refId}).");
            }
            return (target, UiaLocatorResolver.NameOf(target));
        }).ConfigureAwait(false);
    }

    /// <summary>How long a combo box popup gets to appear after Expand before its items are read and clicked.</summary>
    public static readonly TimeSpan ComboPopupDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>SelectionItem.Select, else Invoke; false when the item supports neither.</summary>
    private static bool SelectThroughPattern(AutomationElement target)
    {
        if (target.Patterns.SelectionItem.PatternOrDefault is { } sp) { sp.Select(); return true; }
        if (target.Patterns.Invoke.PatternOrDefault is { } inv) { inv.Invoke(); return true; }
        return false;
    }

    /// <summary>Scrolls the item into view when it can be and clicks its centre; false when none of it is on screen.</summary>
    private async Task<bool> ClickItemAsync(AutomationElement target)
    {
        var rect = await d.RunAsync(() =>
        {
            if (Safe(() => target.Properties.IsOffscreen.ValueOrDefault, false))
                try { target.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
            return Safe(() => target.Properties.IsOffscreen.ValueOrDefault, false) ? default : RectOf(target);
        }).ConfigureAwait(false);
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        await _input.ClickAsync(rect.X + rect.Width / 2, rect.Y + rect.Height / 2).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// What the container says is selected. <c>confirmed</c> when some source names <paramref name="name"/> (or the very
    /// item element); <c>seen</c> is the best other answer (null: the item is reported as not selected, or nothing is
    /// readable); <c>readable</c> is false when no source answered at all.
    /// </summary>
    private static (bool Confirmed, string? Seen, bool Readable) ReadSelection(AutomationElement container, AutomationElement target, string? name, bool combo)
    {
        string? seen = null;
        var readable = false;
        try
        {
            if (container.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault is { } chosen)
            {
                var names = chosen.Select(UiaLocatorResolver.NameOf).ToList();
                if (chosen.Any(c => SameElement(c, target)) || names.Contains(name)) return (true, name, true);
                if (names.Count > 0) { seen = string.Join(", ", names.Select(n => n ?? "")); readable = true; }
            }
        }
        catch { }
        try
        {
            // Combo boxes are read through their Value pattern (Qt: the current text; Win32 / WPF too). Other containers
            // expose a Value as well on some toolkits (Qt gives every control one), so there it only counts when set.
            if (container.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault is { } value && (combo || value.Length > 0))
            {
                if (name is not null && Matches(value, name)) return (true, value, true);
                seen ??= value;
                readable = true;
            }
        }
        catch { }
        try
        {
            if (target.Patterns.SelectionItem.PatternOrDefault is { } si)
            {
                if (si.IsSelected.ValueOrDefault) return (true, name, true);
                readable = true;
            }
        }
        catch { }
        return (false, seen, readable);
    }

    private static bool Matches(string value, string name) =>
        value == name || string.Equals(value.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameElement(AutomationElement a, AutomationElement b)
    {
        try
        {
            var ra = a.Properties.RuntimeId.ValueOrDefault;
            var rb = b.Properties.RuntimeId.ValueOrDefault;
            return ra is { Length: > 0 } && rb is { Length: > 0 } && ra.SequenceEqual(rb);
        }
        catch { return false; }
    }

    /// <summary>How many toggles <see cref="SetCheckedAsync"/> delivers at most before giving up (default mode).</summary>
    public const int MaxToggles = 3;
    /// <summary>How long <see cref="SetCheckedAsync"/> keeps re-reading the state after a toggle before it counts as not taken.</summary>
    public static readonly TimeSpan CheckReadBackWindow = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan CheckReadBackStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Sets the checked state through the Toggle pattern (else SelectionItem) and reads it back. A toggle that has not
    /// taken within <see cref="CheckReadBackWindow"/> is followed by another, up to <see cref="MaxToggles"/>, after which
    /// the wrong state is an error; with <paramref name="singleAction"/> at most one toggle is delivered and a state that
    /// still differs is reported as <c>unknown</c> instead, so a caller that must not resend can decide.
    /// </summary>
    public async Task<SetCheckedResult> SetCheckedAsync(string refId, bool on, bool singleAction = false)
    {
        var el = await LiveAsync(refId).ConfigureAwait(false);
        var (hasToggle, hasSelection) = await d.RunAsync(() =>
            (el.Patterns.Toggle.PatternOrDefault is not null, el.Patterns.SelectionItem.PatternOrDefault is not null)).ConfigureAwait(false);
        if (hasToggle) return await ToggleUntilAsync(refId, el, on, singleAction ? 1 : MaxToggles, singleAction).ConfigureAwait(false);
        if (!hasSelection) throw new NotSupportedException($"Ref '{refId}' supports neither Toggle nor SelectionItem.");
        return await d.RunAsync(() =>
        {
            var s = el.Patterns.SelectionItem.Pattern;
            var before = s.IsSelected.ValueOrDefault;
            if (before == on) return SetCheckedResult.Did(CheckPath.SelectionItem, 0, before);
            if (on) s.Select(); else s.RemoveFromSelection();
            var after = Safe(() => s.IsSelected.ValueOrDefault, before);
            if (after == on) return SetCheckedResult.Did(CheckPath.SelectionItem, 1, after);
            if (singleAction) return SetCheckedResult.Unconfirmed(CheckPath.SelectionItem, on, after);
            throw SetCheckedResult.Failed(refId, on, 1, after);
        }).ConfigureAwait(false);
    }

    private async Task<SetCheckedResult> ToggleUntilAsync(string refId, AutomationElement el, bool on, int maxToggles, bool singleAction)
    {
        bool? Read() => Safe<bool?>(() => el.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault == ToggleState.On, null);
        var seen = await d.RunAsync(Read).ConfigureAwait(false);
        var toggles = 0;
        while (seen != on)
        {
            if (toggles >= maxToggles)
            {
                if (singleAction) return SetCheckedResult.Unconfirmed(CheckPath.Toggle, on, seen);
                throw SetCheckedResult.Failed(refId, on, toggles, seen);
            }
            await d.RunAsync(() => { el.Patterns.Toggle.Pattern.Toggle(); return 0; }).ConfigureAwait(false);
            toggles++;
            var deadline = DateTime.UtcNow + CheckReadBackWindow;
            while (true)
            {
                seen = await d.RunAsync(Read).ConfigureAwait(false);
                if (seen == on || DateTime.UtcNow >= deadline) break;
                await Task.Delay(CheckReadBackStep).ConfigureAwait(false);
            }
        }
        return SetCheckedResult.Did(CheckPath.Toggle, toggles, seen);
    }

    /// <summary>
    /// Invoke, Toggle or SelectionItem pattern, else a click at the element's centre; <see cref="InvokeMethod.Mouse"/> clicks
    /// right away, <see cref="InvokeMethod.Action"/> never clicks. Reports which path was taken.
    /// </summary>
    public async Task<InvokeResult> InvokeAsync(string refId, InvokeMethod method = InvokeMethod.Auto)
    {
        var el = await LiveAsync(refId).ConfigureAwait(false);
        // Fall through only when a pattern is unsupported; exceptions from a supported pattern propagate
        // so the action is never performed twice.
        var (rect, action) = await d.RunAsync(() =>
        {
            if (!IsAlive(el)) throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            if (method == InvokeMethod.Mouse) return (RectOf(el), (string?)null);
            if (el.Patterns.Invoke.PatternOrDefault is { } inv) { inv.Invoke(); return ((Rect?)null, (string?)"Invoke"); }
            if (el.Patterns.Toggle.PatternOrDefault is { } tg) { tg.Toggle(); return (null, "Toggle"); }
            if (el.Patterns.SelectionItem.PatternOrDefault is { } si) { si.Select(); return (null, "SelectionItem"); }
            if (method == InvokeMethod.Action) throw new NotSupportedException($"Ref '{refId}' supports no Invoke, Toggle or SelectionItem pattern.");
            return (RectOf(el), null);
        }).ConfigureAwait(false);
        if (rect is not { } r) return InvokeResult.Did("action", action);
        if (r.Width <= 0 || r.Height <= 0)
            throw new InvalidOperationException($"Ref '{refId}' cannot be invoked and has no clickable area.");
        await _input.ClickAsync(r.X + r.Width / 2, r.Y + r.Height / 2).ConfigureAwait(false);
        return InvokeResult.Did("mouse");
    }

    public Task FocusAsync(string refId) => WithAsync(refId, el => { el.Focus(); return true; });

    // ---- collections --------------------------------------------------------------------------

    public async Task<string?[][]> GetTableAsync(string refId) => (await GetTableAsync(refId, null).ConfigureAwait(false)).Rows;

    /// <summary>
    /// The table as rows of cell text (header first when the Table pattern lists one and <paramref name="includeHeader"/>
    /// asks for it), <paramref name="maxRows"/> data rows at most (null: all). A grid cell UIA fails to hand out is null
    /// and counted in <see cref="TableReadResult.UnreadableCells"/>.
    /// </summary>
    public Task<TableReadResult> GetTableAsync(string refId, int? maxRows, bool includeHeader = true) => WithAsync(refId, el =>
    {
        var rows = new List<string?[]>();
        var grid = el.Patterns.Grid.PatternOrDefault;
        if (grid is not null)
        {
            if (includeHeader)
            {
                try
                {
                    var headers = el.Patterns.Table.PatternOrDefault?.ColumnHeaders.ValueOrDefault;
                    if (headers is { Length: > 0 }) rows.Add(headers.Select(CellText).ToArray());
                }
                catch { }
            }
            int rc = grid.RowCount.ValueOrDefault, cc = grid.ColumnCount.ValueOrDefault;
            var limit = maxRows is { } m ? Math.Min(Math.Max(m, 0), rc) : rc;
            var unreadable = 0;
            for (var r = 0; r < limit; r++)
            {
                var row = new string?[cc];
                for (var c = 0; c < cc; c++)
                {
                    try { row[c] = CellText(grid.GetItem(r, c)); }
                    catch { row[c] = null; unreadable++; }
                }
                rows.Add(row);
            }
            return new TableReadResult(rows.ToArray(), rc, cc, rc - limit, unreadable);
        }
        // No Grid pattern: treat each child row's children as cells; the column count is the width of the first row.
        var children = el.FindAllChildren();
        var take = maxRows is { } max ? Math.Min(Math.Max(max, 0), children.Length) : children.Length;
        var columns = 0;
        foreach (var (row, i) in children.Select((r, i) => (r, i)))
        {
            var cells = row.FindAllChildren();
            if (i == 0) columns = cells.Length;
            if (i < take) rows.Add(cells.Select(CellText).ToArray());
            else break;
        }
        return new TableReadResult(rows.ToArray(), children.Length, columns, children.Length - take, 0);
    });

    private static string CellText(AutomationElement c)
    {
        try
        {
            var v = c.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch { }
        return UiaLocatorResolver.NameOf(c) ?? "";
    }

    public Task<string[]> GetListItemsAsync(string refId) => WithAsync(refId, el =>
    {
        var combo = UiaLocatorResolver.TypeOf(el) == "ComboBox";
        if (combo) el.Patterns.ExpandCollapse.PatternOrDefault?.Expand();
        try
        {
            return el.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Select(i => UiaLocatorResolver.NameOf(i) ?? "").ToArray();
        }
        finally
        {
            if (combo) try { el.Patterns.ExpandCollapse.PatternOrDefault?.Collapse(); } catch { }
        }
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { d.RunAsync(() => { _auto?.Dispose(); _auto = null; }).Wait(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }
}

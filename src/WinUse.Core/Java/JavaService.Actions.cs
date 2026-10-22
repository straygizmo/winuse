using Microsoft.Extensions.Logging;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>Actions on Swing elements (spec 4.7).</summary>
public sealed partial class JavaService
{
    /// <summary>Wait after opening a combo box popup before looking for its list.</summary>
    public static readonly TimeSpan ComboPopupDelay = TimeSpan.FromMilliseconds(100);
    private const int MaxToggleAttempts = 3;
    private const int PopupSearchLimit = 2000;

    internal Func<string, Task> TypeText { get; set; } = text => input.TypeTextAsync(text, 0);

    internal Func<nint, Task> Activate { get; set; } = async hwnd => await windows.ActivateAsync(hwnd).ConfigureAwait(false);

    // ---- invoke ---------------------------------------------------------------------------------

    /// <summary>
    /// The element's <c>click</c> action (menu items work while their menu is closed), else its first action, else a
    /// selection of its index in a parent that supports selection (tabs, list items, tree nodes), else a click at its centre.
    /// </summary>
    public async Task InvokeAsync(string refId)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        var info = await bridge.RunAsync(jab =>
        {
            var i = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            var actions = jab.GetActions(h);
            if (actions.Count == 0) return i;
            var action = actions.Contains("click") ? "click" : actions[0];
            if (!jab.DoAction(h, action)) throw new InvalidOperationException($"Action '{action}' failed on ref '{refId}'.");
            return null;
        }).ConfigureAwait(false);
        if (info is null) return;

        if (await SelectInParentAsync(refId, info.IndexInParent).ConfigureAwait(false)) return;

        var rect = await GetRectAsync(refId).ConfigureAwait(false);
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException($"Ref '{refId}' has no action and no visible area to click.");
        await ClickAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2).ConfigureAwait(false);
    }

    /// <summary>Selects child <paramref name="index"/> in the ref's parent when that parent supports selection.</summary>
    private async Task<bool> SelectInParentAsync(string refId, int index)
    {
        var loc = (JavaLocator)GetRef(refId).Locator;
        if (loc.Path.Count == 0 || index < 0) return false;
        var parent = await ResolveAsync(loc with { Path = loc.Path.Take(loc.Path.Count - 1).ToList() }).ConfigureAwait(false);
        if (parent is not { } p) return false;
        return await bridge.RunAsync(jab =>
        {
            try
            {
                if (jab.GetInfo(p) is not { HasSelection: true }) return false;
                jab.AddSelection(p, index);
                return true;
            }
            finally
            {
                jab.Release(p);
            }
        }).ConfigureAwait(false);
    }

    // ---- text / check / focus -------------------------------------------------------------------

    public async Task SetTextAsync(string refId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var h = await LiveAsync(refId).ConfigureAwait(false);
        if (await bridge.RunAsync(jab => jab.SetText(h, text)).ConfigureAwait(false)) return;

        // No AccessibleEditableText: focus the field and type, like the UIA fallback.
        var loc = (JavaLocator)GetRef(refId).Locator;
        if (await WindowFind(loc.Window).ConfigureAwait(false) is { } w) await Activate(w.Hwnd).ConfigureAwait(false);
        await bridge.RunAsync(jab => jab.RequestFocus(h)).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await Press(KeyChord.Parse("ctrl+a")).ConfigureAwait(false);
        if (text.Length == 0) await Press(KeyChord.Parse("delete")).ConfigureAwait(false);
        else await TypeText(text).ConfigureAwait(false);
    }

    public async Task SetCheckedAsync(string refId, bool on)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        var parentRole = ParentRoleOf(refId);
        for (var attempt = 0; attempt <= MaxToggleAttempts; attempt++)
        {
            var current = await bridge.RunAsync(jab =>
            {
                var i = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
                var ct = JavaRoleMap.ControlTypeOf(i.Role, parentRole);
                return JavaStates.CheckedOf(JavaStates.Parse(i.States), ct, i.Role)
                       ?? throw new NotSupportedException($"Ref '{refId}' ({i.Role}) has no checked state.");
            }).ConfigureAwait(false);
            if (current == on) return;
            if (attempt == MaxToggleAttempts) break;
            await bridge.RunAsync(jab =>
            {
                var actions = jab.GetActions(h);
                var action = actions.Contains("click") ? "click" : actions.FirstOrDefault()
                    ?? throw new NotSupportedException($"Ref '{refId}' has no action to toggle it.");
                return jab.DoAction(h, action);
            }).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Could not set ref '{refId}' to {on}.");
    }

    public async Task FocusAsync(string refId)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        await bridge.RunAsync(jab => jab.RequestFocus(h)).ConfigureAwait(false);
    }

    // ---- selection ------------------------------------------------------------------------------

    /// <summary>Selects an item by label: through AccessibleSelection for lists / tabs / trees, by opening the popup and clicking for combo boxes.</summary>
    public async Task SelectItemAsync(string refId, string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var h = await LiveAsync(refId).ConfigureAwait(false);
        var parentRole = ParentRoleOf(refId);
        var (ct, children) = await bridge.RunAsync(jab =>
        {
            var i = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            var type = JavaRoleMap.ControlTypeOf(i.Role, parentRole);
            return (type, type == "ComboBox" ? [] : ChildNames(jab, h, i));
        }).ConfigureAwait(false);

        if (ct == "ComboBox")
        {
            await SelectComboItemAsync(refId, h, item).ConfigureAwait(false);
            return;
        }
        var index = MatchIndex(children, item);
        if (index < 0) throw new InvalidOperationException($"Item '{item}' not found in {ct} (ref {refId}).");
        await bridge.RunAsync(jab =>
        {
            jab.ClearSelection(h);
            jab.AddSelection(h, index);
            return 0;
        }).ConfigureAwait(false);
    }

    private static List<string?> ChildNames(IJabAccess jab, JabHandle h, JabContextInfo info)
    {
        var names = new List<string?>(info.ChildrenCount);
        for (var i = 0; i < info.ChildrenCount; i++)
        {
            if (jab.GetChild(h, i) is not { } c) { names.Add(null); continue; }
            try { names.Add(jab.GetInfo(c) is { } ci ? JavaTreeBuilder.NameOf(jab, c, ci, JavaRoleMap.ControlTypeOf(ci.Role, info.Role)) : null); }
            finally { jab.Release(c); }
        }
        return names;
    }

    /// <summary>Exact match first, then a case-insensitive substring match; -1 when nothing matches.</summary>
    private static int MatchIndex(List<string?> names, string item)
    {
        var exact = names.IndexOf(item);
        if (exact >= 0) return exact;
        for (var i = 0; i < names.Count; i++)
            if (names[i] is { } n && n.Contains(item, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Swing's combo popup does not commit through AccessibleSelection: open it (<c>togglePopup</c>), find the now
    /// showing list item by name anywhere in the window and click its centre; Escape closes the popup on failure.
    /// </summary>
    private async Task SelectComboItemAsync(string refId, JabHandle combo, string item)
    {
        var items = await OpenComboPopupAsync(refId, combo).ConfigureAwait(false);
        var hit = items.FirstOrDefault(x => x.Name == item)
                  ?? items.FirstOrDefault(x => x.Name is { } n && n.Contains(item, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            await TogglePopupAsync(refId, combo).ConfigureAwait(false);
            throw new InvalidOperationException($"Item '{item}' not found in ComboBox (ref {refId}).");
        }
        await ClickAt(hit.Rect.X + hit.Rect.Width / 2, hit.Rect.Y + hit.Rect.Height / 2).ConfigureAwait(false);
    }

    private sealed record PopupItem(string? Name, Rect Rect);

    /// <summary>Opens the combo popup and returns the showing list items of the window (name and screen rectangle).</summary>
    /// <summary>The combo's <c>togglePopup</c> action: opens a closed popup, closes an open one. Works whatever window has the focus.</summary>
    private Task TogglePopupAsync(string refId, JabHandle combo) => bridge.RunAsync(jab =>
    {
        var actions = jab.GetActions(combo);
        var action = actions.Contains("togglePopup") ? "togglePopup" : actions.FirstOrDefault()
            ?? throw new NotSupportedException($"Ref '{refId}' has no action to open its popup.");
        return jab.DoAction(combo, action);
    });

    private async Task<List<PopupItem>> OpenComboPopupAsync(string refId, JabHandle combo)
    {
        await TogglePopupAsync(refId, combo).ConfigureAwait(false);
        await Task.Delay(ComboPopupDelay).ConfigureAwait(false);

        var loc = (JavaLocator)GetRef(refId).Locator;
        var w = await WindowFind(loc.Window).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Window for ref '{refId}' not found.");
        var dpi = DpiScale(w.Hwnd);
        return await bridge.RunAsync(jab =>
        {
            if (jab.GetContextFromHwnd(w.Hwnd) is not { } root) return [];
            try
            {
                var tx = Calibrate(jab, root, w.Rect, dpi);
                var found = new List<PopupItem>();
                var budget = PopupSearchLimit;
                CollectShowingListItems(jab, root, null, tx, found, ref budget);
                return found;
            }
            finally
            {
                jab.Release(root);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Depth-first walk over showing nodes collecting the labels / menu items inside showing lists.</summary>
    private static void CollectShowingListItems(IJabAccess jab, JabHandle h, string? parentRole, JavaTransform tx, List<PopupItem> into, ref int budget)
    {
        var info = jab.GetInfo(h);
        if (info is null || budget-- <= 0) return;
        if (!JavaStates.Parse(info.States).Showing) return;
        if (parentRole == "list" && info.Role is "label" or "menu item" or "list item")
        {
            into.Add(new PopupItem(JavaTreeBuilder.NameOf(jab, h, info, "ListItem"), tx.Apply(info.X, info.Y, info.Width, info.Height)));
            return;
        }
        for (var i = 0; i < info.ChildrenCount && budget > 0; i++)
        {
            if (jab.GetChild(h, i) is not { } c) continue;
            try { CollectShowingListItems(jab, c, info.Role, tx, into, ref budget); }
            finally { jab.Release(c); }
        }
    }

    // ---- collections ----------------------------------------------------------------------------

    public async Task<string[]> GetListItemsAsync(string refId)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        var parentRole = ParentRoleOf(refId);
        var (ct, names) = await bridge.RunAsync(jab =>
        {
            var i = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            var type = JavaRoleMap.ControlTypeOf(i.Role, parentRole);
            return (type, type == "ComboBox" ? [] : ChildNames(jab, h, i));
        }).ConfigureAwait(false);
        if (ct != "ComboBox") return names.Select(n => n ?? "").ToArray();

        var items = await OpenComboPopupAsync(refId, h).ConfigureAwait(false);
        await TogglePopupAsync(refId, h).ConfigureAwait(false);
        return items.Select(i => i.Name ?? "").ToArray();
    }

    public Task<string[][]> GetTableAsync(string refId) =>
        Task.FromException<string[][]>(new NotSupportedException("get_table is not supported for Java elements yet"));
}

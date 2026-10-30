using Microsoft.Extensions.Logging;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>Actions on Swing elements (spec 4.7).</summary>
public sealed partial class JavaService
{
    /// <summary>Wait after opening a combo box popup before looking for its list.</summary>
    public static readonly TimeSpan ComboPopupDelay = TimeSpan.FromMilliseconds(100);
    private const int MaxToggleAttempts = 3;
    private const int PopupSearchLimit = 2000;

    /// <summary>Roles whose accessible action commonly opens a modal dialog, so <see cref="InvokeMethod.Auto"/> clicks them with the mouse.</summary>
    private static readonly string[] MouseFirstRoles = ["push button"];

    internal Func<string, Task> TypeText { get; set; } = text => input.TypeTextAsync(text, 0);

    internal Func<nint, Task<bool>> Activate { get; set; } = windows.ActivateAsync;

    /// <summary>What a timed-out action means; the note travels with the <c>unknown</c> outcome.</summary>
    internal static string ActionTimedOutNote(string action, string refId) =>
        $"The JVM did not answer the '{action}' action on ref '{refId}' within the bridge timeout. The action was delivered and most likely ran: "
        + "a Swing action that opens a modal dialog returns only when the dialog closes, so expect such a dialog (foreground / new_windows show it). "
        + "Until it is closed the JVM's Java Access Bridge is blocked: read the dialog with screenshot or the UIA tree and operate it with "
        + "click / type_text / press_keys, or close it. Do not repeat the invoke. Buttons that open dialogs are best invoked with method \"mouse\".";

    // ---- invoke ---------------------------------------------------------------------------------

    /// <summary>
    /// Activates an element. <see cref="InvokeMethod.Auto"/>: a push button with an on-screen part is clicked with the mouse
    /// (its <c>click</c> action would hold the bridge hostage while a dialog it opens is up); otherwise the element's
    /// <c>click</c> action (menu items work while their menu is closed), else its first action, else a selection of its index
    /// in a parent that supports selection (tabs, list items, tree nodes), else a mouse click at its centre.
    /// </summary>
    public async Task<InvokeResult> InvokeAsync(string refId, InvokeMethod method = InvokeMethod.Auto)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        var tx = await TransformForAsync(refId).ConfigureAwait(false);
        var (info, visibility) = await bridge.RunAsync(jab =>
        {
            var i = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            return (i, VisibilityOf(jab, h, i, tx));
        }).ConfigureAwait(false);

        if (method == InvokeMethod.Mouse || (method == InvokeMethod.Auto && MouseFirstRoles.Contains(info.Role) && visibility.Visible))
        {
            var reason = await TryMouseClickAsync(refId, visibility).ConfigureAwait(false);
            if (reason is null) return InvokeResult.Did("mouse");
            if (method == InvokeMethod.Mouse) throw new InvalidOperationException(reason);
            _log.LogDebug("invoke {Ref}: mouse click not possible ({Reason}); using the accessible action", refId, reason);
        }

        var result = await bridge.RunAsync(jab =>
        {
            var actions = jab.GetActions(h);
            if (actions.Count == 0) return null;
            var action = actions.Contains("click") ? "click" : actions[0];
            return jab.DoAction(h, action) switch
            {
                JabActionOutcome.Done => InvokeResult.Did("action", action),
                JabActionOutcome.Timeout => new InvokeResult("action", action, InvokeResult.Unknown, ActionTimedOutNote(action, refId)),
                JabActionOutcome.Failed => throw new InvalidOperationException($"Action '{action}' failed on ref '{refId}' (the component refused it)."),
                _ => throw new InvalidOperationException($"Action '{action}' could not be delivered to the JVM for ref '{refId}' (the JVM is exiting or its bridge wrote no answer)."),
            };
        }).ConfigureAwait(false);
        if (result is not null) return result;

        if (await SelectInParentAsync(refId, info.IndexInParent).ConfigureAwait(false)) return InvokeResult.Did("selection");

        var fallback = await TryMouseClickAsync(refId, visibility).ConfigureAwait(false);
        if (fallback is not null) throw new InvalidOperationException($"Ref '{refId}' has no action and cannot be clicked: {fallback}");
        return InvokeResult.Did("mouse");
    }

    /// <summary>
    /// Clicks the centre of the element's visible part after bringing its window to the front, provided the point really
    /// belongs to that window's process; returns why not instead of clicking otherwise.
    /// </summary>
    private async Task<string?> TryMouseClickAsync(string refId, JavaVisibility v)
    {
        if (v.NotClickableReason(refId) is { } reason) return reason;
        var loc = (JavaLocator)GetRef(refId).Locator;
        var w = await WindowFind(loc.Window).ConfigureAwait(false);
        if (w is not null && !await Activate(w.Hwnd).ConfigureAwait(false)) return $"the window '{w.Title}' could not be brought to the foreground.";
        var x = v.VisibleRect.X + v.VisibleRect.Width / 2;
        var y = v.VisibleRect.Y + v.VisibleRect.Height / 2;
        if (w is not null && await WindowAt(x, y).ConfigureAwait(false) is { } hit && hit.Pid != w.Pid)
            return $"the point ({x},{y}) is covered by '{hit.Title}' ({hit.ProcessName}, pid {hit.Pid}), not by {w.ProcessName} (pid {w.Pid}).";
        await ClickAt(x, y).ConfigureAwait(false);
        return null;
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

    /// <summary>
    /// Sets the text of a field and reports the path taken: <see cref="TextEntryPath.AccessibleText"/> when the field's
    /// AccessibleEditableText took it (no key events reach the field), <see cref="TextEntryPath.Keyboard"/> when the field
    /// was focused and the text typed (the field sees key events, as with a user). The text itself is never reported.
    /// </summary>
    public async Task<string> SetTextAsync(string refId, string text, TextEntryMethod method = TextEntryMethod.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        var h = await LiveAsync(refId).ConfigureAwait(false);
        if (method != TextEntryMethod.Keyboard)
        {
            if (await bridge.RunAsync(jab => jab.SetText(h, text)).ConfigureAwait(false)) return TextEntryPath.AccessibleText;
            if (method == TextEntryMethod.Api)
                throw new NotSupportedException($"Ref '{refId}' did not take the text through AccessibleEditableText (no editable text interface, or the text is too long for one request); use method \"keyboard\".");
        }

        // No AccessibleEditableText (or keyboard asked for): focus the field and type, like the UIA fallback.
        var loc = (JavaLocator)GetRef(refId).Locator;
        if (await WindowFind(loc.Window).ConfigureAwait(false) is { } w) await Activate(w.Hwnd).ConfigureAwait(false);
        await bridge.RunAsync(jab => jab.RequestFocus(h)).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await Press(KeyChord.Parse("ctrl+a")).ConfigureAwait(false);
        if (text.Length == 0) await Press(KeyChord.Parse("delete")).ConfigureAwait(false);
        else await TypeText(text).ConfigureAwait(false);
        return TextEntryPath.Keyboard;
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
            var outcome = await bridge.RunAsync(jab =>
            {
                var actions = jab.GetActions(h);
                var action = actions.Contains("click") ? "click" : actions.FirstOrDefault()
                    ?? throw new NotSupportedException($"Ref '{refId}' has no action to toggle it.");
                return (action, jab.DoAction(h, action));
            }).ConfigureAwait(false);
            if (outcome.Item2 == JabActionOutcome.Timeout)
                throw new InvalidOperationException($"Could not confirm the new state of ref '{refId}': {ActionTimedOutNote(outcome.action, refId)}");
        }
        throw new InvalidOperationException($"Could not set ref '{refId}' to {on}.");
    }

    public async Task FocusAsync(string refId)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        await bridge.RunAsync(jab => jab.RequestFocus(h)).ConfigureAwait(false);
    }

    // ---- selection ------------------------------------------------------------------------------

    /// <summary>How long <see cref="SelectItemAsync"/> keeps re-reading the selection before calling it a mismatch.</summary>
    public static readonly TimeSpan SelectReadBackWindow = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SelectReadBackStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Selects an item by label: through AccessibleSelection for lists / tabs / trees, by opening the popup and clicking
    /// for combo boxes. The selection is then read back through AccessibleSelection (a combo box answers with the item
    /// of its popup list) until it names the item or <see cref="SelectReadBackWindow"/> passes, which is a mismatch
    /// error; a container without AccessibleSelection gives <c>unknown</c>.
    /// </summary>
    public async Task<SelectItemResult> SelectItemAsync(string refId, string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var h = await LiveAsync(refId).ConfigureAwait(false);
        var parentRole = ParentRoleOf(refId);
        var (ct, children, hasSelection) = await bridge.RunAsync(jab =>
        {
            var i = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            var type = JavaRoleMap.ControlTypeOf(i.Role, parentRole);
            return (type, type == "ComboBox" ? [] : ChildNames(jab, h, i), i.HasSelection);
        }).ConfigureAwait(false);

        string? name;
        var method = ct == "ComboBox" ? SelectPath.Mouse : SelectPath.Selection;
        if (ct == "ComboBox")
        {
            name = await SelectComboItemAsync(refId, h, item).ConfigureAwait(false);
        }
        else
        {
            var index = MatchIndex(children, item);
            if (index < 0) throw new InvalidOperationException($"Item '{item}' not found in {ct} (ref {refId}).");
            name = children[index];
            await bridge.RunAsync(jab =>
            {
                jab.ClearSelection(h);
                jab.AddSelection(h, index);
                return 0;
            }).ConfigureAwait(false);
        }
        if (!hasSelection)
            return SelectItemResult.Unverified(method, name, $"the {ct} has no AccessibleSelection");

        var deadline = DateTime.UtcNow + SelectReadBackWindow;
        while (true)
        {
            var selected = await bridge.RunAsync(jab => SelectedNames(jab, h)).ConfigureAwait(false);
            if (selected.Contains(name)) return SelectItemResult.Did(method, name, name);
            if (DateTime.UtcNow >= deadline)
                throw SelectItemResult.Mismatch(refId, ct, name, selected.Count == 0 ? null : string.Join(", ", selected.Select(n => n ?? "")));
            await Task.Delay(SelectReadBackStep).ConfigureAwait(false);
        }
    }

    /// <summary>Names of the children AccessibleSelection reports as selected.</summary>
    private static List<string?> SelectedNames(IJabAccess jab, JabHandle h)
    {
        var names = new List<string?>();
        var count = jab.GetSelectionCount(h);
        for (var i = 0; i < count; i++)
        {
            if (jab.GetSelection(h, i) is not { } c) { names.Add(null); continue; }
            try { names.Add(jab.GetInfo(c) is { } ci ? JavaTreeBuilder.NameOf(jab, c, ci, JavaRoleMap.ControlTypeOf(ci.Role, null)) : null); }
            finally { jab.Release(c); }
        }
        return names;
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
    private async Task<string?> SelectComboItemAsync(string refId, JabHandle combo, string item)
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
        return hit.Name;
    }

    private sealed record PopupItem(string? Name, Rect Rect);

    /// <summary>The combo's <c>togglePopup</c> action: opens a closed popup, closes an open one. Works whatever window has the focus.</summary>
    private Task<bool> TogglePopupAsync(string refId, JabHandle combo) => bridge.RunAsync(jab =>
    {
        var actions = jab.GetActions(combo);
        var action = actions.Contains("togglePopup") ? "togglePopup" : actions.FirstOrDefault()
            ?? throw new NotSupportedException($"Ref '{refId}' has no action to open its popup.");
        return jab.DoAction(combo, action) == JabActionOutcome.Done;
    });

    /// <summary>Opens the combo popup and returns the showing list items of the window (name and screen rectangle).</summary>
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

    /// <summary>
    /// Reads a Swing table (role <c>table</c>) through the table interface of the bridge: the column header as the first
    /// row when the table has one and <paramref name="includeHeader"/> asks for it (false never reads a header cell),
    /// then the text of every cell, <paramref name="maxRows"/> data rows at most (null: all). A cell the bridge cannot
    /// hand out or describe is null and counted in <see cref="TableReadResult.UnreadableCells"/>.
    /// </summary>
    public async Task<TableReadResult> GetTableAsync(string refId, int? maxRows = null, bool includeHeader = true)
    {
        var h = await LiveAsync(refId).ConfigureAwait(false);
        return await bridge.RunAsync(jab =>
        {
            var info = jab.GetInfo(h) ?? throw new InvalidOperationException($"Element for ref '{refId}' is gone.");
            var table = jab.GetTableInfo(h)
                ?? throw new NotSupportedException($"Ref '{refId}' ({info.Role}) is not a table: get_table needs the ref of a Swing table (JTable).");
            try
            {
                var rows = new List<string?[]>();
                var unreadable = 0;
                if (includeHeader && ReadHeader(jab, h, table.ColumnCount, ref unreadable) is { } header) rows.Add(header);
                var limit = maxRows is { } m ? Math.Min(Math.Max(m, 0), table.RowCount) : table.RowCount;
                for (var r = 0; r < limit; r++)
                {
                    var row = new string?[table.ColumnCount];
                    for (var c = 0; c < table.ColumnCount; c++)
                    {
                        row[c] = CellText(jab, table.Table, r, c);
                        if (row[c] is null) unreadable++;
                    }
                    rows.Add(row);
                }
                return new TableReadResult(rows.ToArray(), table.RowCount, table.ColumnCount, table.RowCount - limit, unreadable);
            }
            finally
            {
                jab.Release(table.Table);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The header cells of a table, or null when it has no column header or no header cell carries text. A header cell
    /// the bridge cannot read is null and counted in <paramref name="unreadable"/> (when the header row is kept).
    /// </summary>
    private static string?[]? ReadHeader(IJabAccess jab, JabHandle table, int columns, ref int unreadable)
    {
        if (jab.GetTableColumnHeader(table) is not { } header) return null;
        try
        {
            if (header.RowCount <= 0 || header.ColumnCount <= 0) return null;
            var n = Math.Min(columns, header.ColumnCount);
            var row = new string?[columns];
            var any = false;
            var failed = 0;
            for (var c = 0; c < n; c++)
            {
                row[c] = CellText(jab, header.Table, header.RowCount - 1, c);
                if (row[c] is null) failed++;
                any |= row[c] is { Length: > 0 };
            }
            for (var c = n; c < columns; c++) row[c] = "";
            if (!any) return null;
            unreadable += failed;
            return row;
        }
        finally
        {
            jab.Release(header.Table);
        }
    }

    /// <summary>
    /// The text of a cell: its accessible name (the rendered text), else its text, else its checked state or value; ""
    /// for a cell that carries none of these. Null when the bridge hands out no cell or no context info for it, so a
    /// caller can tell a read failure from an empty cell.
    /// </summary>
    private static string? CellText(IJabAccess jab, JabHandle table, int row, int column)
    {
        if (jab.GetTableCell(table, row, column) is not { } cell) return null;
        try
        {
            if (jab.GetInfo(cell) is not { } ci) return null;
            if (!string.IsNullOrEmpty(ci.Name)) return ci.Name;
            if (ci.HasText && jab.GetText(cell, 500) is { Length: > 0 } t) return t;
            var ct = JavaRoleMap.ControlTypeOf(ci.Role, "table");
            if (JavaStates.CheckedOf(JavaStates.Parse(ci.States), ct, ci.Role) is { } on) return on ? "true" : "false";
            return (ci.HasValue ? jab.GetValue(cell) : null) ?? ci.Description ?? "";
        }
        finally
        {
            jab.Release(cell);
        }
    }
}

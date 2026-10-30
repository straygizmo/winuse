namespace WinUse.Core.Input;

/// <summary>How <c>set_text</c> puts text into a field.</summary>
public enum TextEntryMethod
{
    /// <summary>The accessibility API when the field supports it, else keyboard input (focus, select all, type).</summary>
    Auto,
    /// <summary>The accessibility API only (UIA Value pattern, Java AccessibleEditableText); fails when the field has none.</summary>
    Api,
    /// <summary>Keyboard input only: focus the field, select all, type. The field sees key events, as with a user typing.</summary>
    Keyboard,
}

/// <summary>How <c>invoke</c> activates an element.</summary>
public enum InvokeMethod
{
    /// <summary>
    /// A visible Swing push button is clicked with the mouse (its action would block the Java Access Bridge while a dialog it
    /// opens is up); everything else goes through the accessibility action, with the usual fallbacks.
    /// </summary>
    Auto,
    /// <summary>The accessibility action only (UIA Invoke / Toggle / SelectionItem, Java <c>click</c> action), no mouse.</summary>
    Action,
    /// <summary>A mouse click at the centre of the element's visible part; fails when none of it is on screen.</summary>
    Mouse,
}

/// <summary>
/// What <c>invoke</c> did: <paramref name="Method"/> is the path taken (<c>action</c>, <c>mouse</c>, <c>selection</c>, ...),
/// <paramref name="Action"/> the accessibility action name when one was run, <paramref name="Outcome"/> <c>done</c> when the
/// target confirmed it or <c>unknown</c> when the request was delivered but never answered (a Swing action that opened a
/// modal dialog), and <paramref name="Note"/> what that means for the caller.
/// </summary>
public sealed record InvokeResult(string Method, string? Action, string Outcome, string? Note = null)
{
    public const string Done = "done";
    public const string Unknown = "unknown";

    public static InvokeResult Did(string method, string? action = null) => new(method, action, Done);
}

/// <summary>How <c>set_text</c> entered the text, as the result reports it.</summary>
public static class TextEntryPath
{
    public const string ValuePattern = "value_pattern";
    public const string AccessibleText = "accessible_text";
    public const string Keyboard = "keyboard";
    public const string DomFill = "fill";
}

/// <summary>
/// What <c>select_item</c> did: <paramref name="Method"/> is the path taken (<see cref="SelectPath"/>), <paramref name="Item"/>
/// the label of the item that was matched and acted on, <paramref name="Selected"/> what the container reported as
/// selected when it was read back afterwards (null when it gave no answer), <paramref name="Outcome"/> <c>done</c> when
/// the read-back confirmed the item or <c>unknown</c> when the container exposes no way to read its selection; a
/// read-back that names another item is an error, not a result. <paramref name="Note"/> says what an <c>unknown</c>
/// outcome means for the caller.
/// </summary>
public sealed record SelectItemResult(string Method, string Outcome, string? Item, string? Selected, string? Note = null)
{
    public const string Done = "done";
    public const string Unknown = "unknown";

    public static SelectItemResult Did(string method, string? item, string? selected) => new(method, Done, item, selected ?? item);

    public static SelectItemResult Unverified(string method, string? item, string why) =>
        new(method, Unknown, item, null, $"The selection was requested but could not be read back: {why}. Read the element (get_element / get_text) or take a screenshot to confirm it.");

    /// <summary>The error for a read-back that names something other than <paramref name="item"/>.</summary>
    public static InvalidOperationException Mismatch(string refId, string type, string? item, string? selected) =>
        new($"Selected '{item}' in {type} (ref {refId}), but afterwards it reports " +
            (selected is null ? "the item as not selected" : $"'{selected}' as selected") +
            "; the app refused or changed the selection. Read the element (get_element) to see its current state.");
}

/// <summary>How <c>select_item</c> selected the item, as the result reports it.</summary>
public static class SelectPath
{
    /// <summary>The accessibility selection API: UIA SelectionItem / Invoke pattern, Java AccessibleSelection.</summary>
    public const string Selection = "selection";
    /// <summary>A mouse click on the item (a combo box popup item, as a user commits it).</summary>
    public const string Mouse = "mouse";
    /// <summary>The DOM: the option was selected on the &lt;select&gt;.</summary>
    public const string Dom = "dom";
}

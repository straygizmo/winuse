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

namespace WinUse.Core.Java;

/// <summary>A Java object reference held through the bridge: the JVM id plus the <c>JOBJECT64</c>.</summary>
public readonly record struct JabHandle(int VmId, long Ac);

/// <summary>What <c>getAccessibleContextInfo</c> returns, with the fixed-size strings already decoded.</summary>
public sealed record JabContextInfo(
    string? Name, string? Description, string Role, string States, int IndexInParent, int ChildrenCount,
    int X, int Y, int Width, int Height, bool HasAction, bool HasSelection, bool HasText, bool HasValue);

public sealed record JabVersionInfo(string VmVersion, string JavaDllVersion, string WindowsDllVersion);

/// <summary>How <see cref="IJabAccess.DoAction"/> ended.</summary>
public enum JabActionOutcome
{
    /// <summary>The JVM ran the action and reported success.</summary>
    Done,
    /// <summary>The JVM answered that the action failed (unknown action name, or the component refused it).</summary>
    Failed,
    /// <summary>
    /// The JVM did not answer within the request timeout. The action was delivered and most likely ran; a Swing action that
    /// opens a modal dialog returns (and answers) only when the dialog closes.
    /// </summary>
    Timeout,
    /// <summary>The request could not be delivered or the JVM wrote no answer: the action did not run.</summary>
    Unreachable,
}

/// <summary>
/// What <c>getAccessibleTableInfo</c> / <c>getAccessibleTableColumnHeader</c> return: the dimensions and the
/// <c>AccessibleTable</c> object (<see cref="Table"/>) that <see cref="IJabAccess.GetTableCell"/> addresses cells through.
/// The caller owns <see cref="Table"/> and must <see cref="IJabAccess.Release"/> it.
/// </summary>
public sealed record JabTableInfo(int RowCount, int ColumnCount, JabHandle Table);

/// <summary>
/// The subset of the Java Access Bridge API winuse uses. Every call must run on the bridge's dispatcher thread.
/// Handles returned by <see cref="GetContextFromHwnd"/>, <see cref="GetChild"/> and <see cref="GetSelection"/> are owned
/// by the caller and must be passed to <see cref="Release"/> exactly once; a released handle must never be used again.
/// </summary>
public interface IJabAccess
{
    bool IsJavaWindow(nint hwnd);
    JabHandle? GetContextFromHwnd(nint hwnd);
    JabContextInfo? GetInfo(JabHandle h);
    JabHandle? GetChild(JabHandle h, int index);
    /// <summary>The parent context (owned by the caller), or null for a top-level window's context.</summary>
    JabHandle? GetParent(JabHandle h);
    void Release(JabHandle h);
    IReadOnlyList<string> GetActions(JabHandle h);
    /// <summary>
    /// Runs one accessible action. The JVM runs it on its event thread and answers when it returns, so an action that opens a
    /// modal dialog answers only when the dialog closes: the call then ends in <see cref="JabActionOutcome.Timeout"/> although
    /// the action did run (and the JVM's bridge stays blocked until the dialog is gone).
    /// </summary>
    JabActionOutcome DoAction(JabHandle h, string action);
    /// <returns>null when the object has no AccessibleText</returns>
    string? GetText(JabHandle h, int maxChars);
    string? GetValue(JabHandle h);
    bool SetText(JabHandle h, string text);
    bool RequestFocus(JabHandle h);
    void AddSelection(JabHandle h, int index);
    void ClearSelection(JabHandle h);
    JabHandle? GetSelection(JabHandle h, int index);
    int GetSelectionCount(JabHandle h);
    string? GetVirtualName(JabHandle h);
    JabVersionInfo? GetVersion(int vmId);
    /// <summary>
    /// Version of the JVM that owns <paramref name="hwnd"/>, found through packets that carry no object handle, so it still
    /// answers when the JVM's wire format was guessed wrong; null when no connected JVM claims the window.
    /// </summary>
    JabVersionInfo? GetVersionForWindow(nint hwnd);
    /// <summary>The wire format the client sends to the JVM that owns <paramref name="hwnd"/>; null when no connected JVM claims it.</summary>
    JabLayout? LayoutForWindow(nint hwnd);
    /// <summary>
    /// Why the window owned by process <paramref name="pid"/> cannot be read: how far the conversation with that JVM got
    /// (hello, handshake, answering requests) and what the connected JVMs are. Only facts, no window or element data.
    /// </summary>
    JabWindowDiagnosis Diagnose(nint hwnd, int pid);
    /// <summary>The table behind a context (role <c>table</c>), or null when it has no AccessibleTable.</summary>
    JabTableInfo? GetTableInfo(JabHandle h);
    /// <summary>The column header of a table as a table of its own (one row of header cells), or null when it has none.</summary>
    JabTableInfo? GetTableColumnHeader(JabHandle h);
    /// <summary>The context of one cell (owned by the caller), or null when the table has no such cell.</summary>
    JabHandle? GetTableCell(JabHandle table, int row, int column);
}

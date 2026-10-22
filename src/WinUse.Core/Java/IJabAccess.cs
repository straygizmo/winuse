namespace WinUse.Core.Java;

/// <summary>A Java object reference held through the bridge: the JVM id plus the <c>JOBJECT64</c>.</summary>
public readonly record struct JabHandle(int VmId, long Ac);

/// <summary>What <c>getAccessibleContextInfo</c> returns, with the fixed-size strings already decoded.</summary>
public sealed record JabContextInfo(
    string? Name, string? Description, string Role, string States, int IndexInParent, int ChildrenCount,
    int X, int Y, int Width, int Height, bool HasAction, bool HasSelection, bool HasText, bool HasValue);

public sealed record JabVersionInfo(string VmVersion, string JavaDllVersion, string WindowsDllVersion);

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
    void Release(JabHandle h);
    IReadOnlyList<string> GetActions(JabHandle h);
    /// <returns>false when the bridge reports that the action failed</returns>
    bool DoAction(JabHandle h, string action);
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
}

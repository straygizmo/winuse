using WinUse.Core.Windows;

namespace WinUse.Core.Screen;

/// <summary>One display: <see cref="Index"/> is stable for the session (primary first, then by position).</summary>
public sealed record MonitorInfo(int Index, Rect Rect, Rect WorkArea, bool Primary, string Device);

/// <summary>How a window is captured.</summary>
public enum CaptureMethod
{
    /// <summary>Screen copy when the window (or a dialog it owns) is in the foreground, else PrintWindow.</summary>
    Auto,
    /// <summary>Copy the window rectangle from the screen: exactly what is displayed, including owned dialogs and overlays, but other windows on top are copied too.</summary>
    Screen,
    /// <summary>PrintWindow: the window content even when covered, but owned dialogs are missing and some apps (Chromium) hand out a stale frame.</summary>
    Print,
}

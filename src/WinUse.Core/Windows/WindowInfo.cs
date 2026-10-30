namespace WinUse.Core.Windows;

public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    /// <summary>True when the rectangle covers no pixel.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>The overlap of two rectangles; an empty rectangle (zero size, positioned at the first one's origin) when they do not meet.</summary>
    public Rect Intersect(Rect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new Rect(x, y, right - x, bottom - y) : new Rect(X, Y, 0, 0);
    }

    public bool Contains(int px, int py) => px >= X && py >= Y && px < Right && py < Bottom;

    public override string ToString() => $"{X},{Y},{Width},{Height}";
}

public enum WindowState { Normal, Minimized, Maximized }

public sealed record WindowInfo(nint Hwnd, string Title, string ClassName, string ProcessName, int Pid, Rect Rect, WindowState State);

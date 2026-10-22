using WinUse.Core.Windows;

namespace WinUse.Core.Java;

/// <summary>Maps Java screen coordinates (possibly DPI-virtualized) onto physical screen pixels.</summary>
public readonly record struct JavaTransform(double Scale, double OffsetX, double OffsetY)
{
    public Rect Apply(int x, int y, int width, int height) => new(
        (int)Math.Round(x * Scale + OffsetX),
        (int)Math.Round(y * Scale + OffsetY),
        (int)Math.Round(width * Scale),
        (int)Math.Round(height * Scale));
}

public static class JavaGeometry
{
    private const double MinScale = 0.5, MaxScale = 4.0;

    /// <summary>
    /// Self-calibrates per window: the frame's rectangle as Java reports it against the same window's rectangle from
    /// Windows. A DPI-unaware JVM gets virtualized coordinates, a DPI-aware one gets scaled ones; both fit
    /// <c>screen = java * scale + offset</c>. Without a usable root the DPI scale alone is used.
    /// </summary>
    public static JavaTransform Calibrate(Rect window, (int X, int Y, int W, int H)? root, double dpiScale)
    {
        if (root is not { } r) return new JavaTransform(dpiScale, 0, 0);
        double scale;
        if (r.W > 0) scale = window.Width / (double)r.W;
        else if (r.H > 0) scale = window.Height / (double)r.H;
        else scale = 1;
        if (scale < MinScale || scale > MaxScale) return new JavaTransform(dpiScale, 0, 0);
        return new JavaTransform(scale, window.X - r.X * scale, window.Y - r.Y * scale);
    }
}

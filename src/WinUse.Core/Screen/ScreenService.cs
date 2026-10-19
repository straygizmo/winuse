using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Screen;

/// <summary>
/// A PNG capture. <see cref="OriginX"/>/<see cref="OriginY"/> are the virtual-screen physical-pixel coordinates of image
/// pixel (0,0): <c>screen_x = OriginX + pixel_x / Scale</c> (the virtual screen origin can be negative on multi-monitor setups).
/// </summary>
public sealed record Capture(byte[] Png, int Width, int Height, double Scale, int OriginX = 0, int OriginY = 0);

/// <summary>Captures the screen, a window or a rectangle as PNG in physical pixels (the process must be per-monitor DPI aware).</summary>
public sealed class ScreenService(AutomationDispatcher d)
{
    public const double MinScale = 0.25, MaxScale = 1.0;

    public Task<Capture> CaptureScreenAsync(double scale = 1.0)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() => CaptureArea(VirtualScreen(), scale));
    }

    public Task<Capture> CaptureRectAsync(Rect r, double scale = 1.0)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() => CaptureArea(r, scale));
    }

    public Task<Capture> CaptureWindowAsync(nint hwnd, double scale = 1.0)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() =>
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var wr))
                throw new ArgumentException($"Window {hwnd} not found.", nameof(hwnd));
            var rect = new Rect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            if (!NativeMethods.IsIconic(hwnd) && rect.Width > 0 && rect.Height > 0)
            {
                using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
                bool ok;
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (ok) return Encode(bmp, scale, rect);
            }
            return CaptureArea(rect, scale);
        });
    }

    internal static double ClampScale(double scale) =>
        double.IsNaN(scale) ? MaxScale : Math.Clamp(scale, MinScale, MaxScale);

    private static Rect VirtualScreen() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    private static Capture CaptureArea(Rect r, double scale)
    {
        if (r.Width <= 0 || r.Height <= 0)
            throw new ArgumentException("Capture area must have positive width and height.", nameof(r));
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
        return Encode(bmp, scale, r);
    }

    private static Capture Encode(Bitmap src, double scale, Rect area)
    {
        if (scale >= MaxScale) return ToCapture(src, scale, area);
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }
        return ToCapture(dst, scale, area);
    }

    private static Capture ToCapture(Bitmap bmp, double scale, Rect area)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new Capture(ms.ToArray(), bmp.Width, bmp.Height, scale, area.X, area.Y);
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Screen;

/// <summary>
/// A PNG capture. <see cref="OriginX"/>/<see cref="OriginY"/> are the virtual-screen physical-pixel coordinates of image
/// pixel (0,0): <c>screen_x = OriginX + pixel_x / Scale</c> (the virtual screen origin can be negative on multi-monitor setups).
/// <see cref="Method"/> is <c>screen</c> (copied from the screen) or <c>print</c> (PrintWindow).
/// </summary>
public sealed record Capture(byte[] Png, int Width, int Height, double Scale, int OriginX = 0, int OriginY = 0, string Method = "screen");

/// <summary>Captures the screen, a window or a rectangle as PNG in physical pixels (the process must be per-monitor DPI aware).</summary>
public sealed class ScreenService(AutomationDispatcher d)
{
    public const double MinScale = 0.25, MaxScale = 1.0;

    /// <summary>The displays, primary first (index 0), then top-to-bottom, left-to-right.</summary>
    public Task<IReadOnlyList<MonitorInfo>> ListMonitorsAsync() => d.RunAsync(ListMonitors);

    public static IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var found = new List<(Rect Rect, Rect Work, bool Primary, string Device)>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeMethods.RECT _, nint _) =>
        {
            var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
            if (NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                string device;
                unsafe { device = new string(info.szDevice).TrimEnd((char)0); }
                found.Add((ToRect(info.rcMonitor), ToRect(info.rcWork), (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, device));
            }
            return true;
        }, 0);
        return found
            .OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.Y).ThenBy(m => m.Rect.X)
            .Select((m, i) => new MonitorInfo(i, m.Rect, m.Work, m.Primary, m.Device))
            .ToList();
    }

    /// <summary>The primary monitor (origin 0,0), or the monitor with the given index from <see cref="ListMonitors"/>.</summary>
    public Task<Capture> CaptureScreenAsync(double scale = 1.0, int? monitor = null)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() =>
        {
            var monitors = ListMonitors();
            var m = monitor is { } i
                ? (i >= 0 && i < monitors.Count ? monitors[i] : throw new ArgumentOutOfRangeException(nameof(monitor), $"No monitor {i}; there are {monitors.Count} (0-{monitors.Count - 1})."))
                : monitors.FirstOrDefault(x => x.Primary) ?? monitors.FirstOrDefault();
            return CaptureArea(m?.Rect ?? VirtualScreen(), scale);
        });
    }

    /// <summary>All monitors as one image (the virtual screen; its origin can be negative).</summary>
    public Task<Capture> CaptureVirtualScreenAsync(double scale = 1.0)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() => CaptureArea(VirtualScreen(), scale));
    }

    public Task<Capture> CaptureRectAsync(Rect r, double scale = 1.0)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() => CaptureArea(r, scale));
    }

    public Task<Capture> CaptureWindowAsync(nint hwnd, double scale = 1.0, CaptureMethod method = CaptureMethod.Auto)
    {
        scale = ClampScale(scale);
        return d.RunAsync(() =>
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var wr))
                throw new ArgumentException($"Window {hwnd} not found.", nameof(hwnd));
            var rect = new Rect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            var fromScreen = method switch
            {
                CaptureMethod.Screen => true,
                CaptureMethod.Print => false,
                _ => IsInForeground(hwnd),
            };
            if (!fromScreen && !NativeMethods.IsIconic(hwnd) && rect.Width > 0 && rect.Height > 0)
            {
                using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
                bool ok;
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (ok) return Encode(bmp, scale, rect, "print");
            }
            return CaptureArea(rect, scale);
        });
    }

    /// <summary>
    /// True when the window itself, or a window it owns (a modal dialog), is the foreground window: the screen then shows
    /// it unobscured, so a screen copy is the complete, current picture.
    /// </summary>
    public static bool IsInForeground(nint hwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        return fg != 0 && (fg == hwnd || NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOTOWNER) == hwnd);
    }

    internal static double ClampScale(double scale) =>
        double.IsNaN(scale) ? MaxScale : Math.Clamp(scale, MinScale, MaxScale);

    private static Rect ToRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

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
        return Encode(bmp, scale, r, "screen");
    }

    private static Capture Encode(Bitmap src, double scale, Rect area, string method)
    {
        if (scale >= MaxScale) return ToCapture(src, scale, area, method);
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
        return ToCapture(dst, scale, area, method);
    }

    private static Capture ToCapture(Bitmap bmp, double scale, Rect area, string method)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new Capture(ms.ToArray(), bmp.Width, bmp.Height, scale, area.X, area.Y, method);
    }
}

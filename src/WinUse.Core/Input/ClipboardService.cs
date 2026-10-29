using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinUse.Core.Threading;
using static WinUse.Core.Windows.NativeMethods;

namespace WinUse.Core.Input;

/// <summary>What the clipboard holds: its text (null when none) and whether it holds an image.</summary>
public sealed record ClipboardContent(string? Text, bool HasImage);

/// <summary>The Windows clipboard (text and images) through the Win32 API on the STA dispatcher thread.</summary>
public sealed class ClipboardService(AutomationDispatcher d)
{
    private const int OpenAttempts = 20;
    private const int OpenRetryMs = 50;

    public Task<ClipboardContent> GetAsync() => d.RunAsync(() => WithClipboard(() =>
        new ClipboardContent(ReadText(), IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIBV5))));

    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return d.RunAsync(() => WithClipboard(() =>
        {
            if (!EmptyClipboard()) throw new InvalidOperationException("EmptyClipboard failed.");
            Put(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + (char)0));
            return true;
        }));
    }

    /// <summary>Puts an image (any format System.Drawing reads: PNG, JPEG, BMP, GIF ...) on the clipboard as CF_DIB plus a PNG entry.</summary>
    public Task SetImageAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return d.RunAsync(() =>
        {
            using var ms = new MemoryStream(image);
            using var bmp = new Bitmap(ms);
            var dib = ToDib(bmp);
            byte[] png;
            using (var pngStream = new MemoryStream())
            {
                bmp.Save(pngStream, ImageFormat.Png);
                png = pngStream.ToArray();
            }
            return WithClipboard(() =>
            {
                if (!EmptyClipboard()) throw new InvalidOperationException("EmptyClipboard failed.");
                Put(CF_DIB, dib);
                Put(RegisterClipboardFormatW("PNG"), png);
                return true;
            });
        });
    }

    private static T WithClipboard<T>(Func<T> body)
    {
        var opened = false;
        for (var i = 0; i < OpenAttempts && !opened; i++)
        {
            opened = OpenClipboard(0);
            if (!opened) Thread.Sleep(OpenRetryMs);
        }
        if (!opened) throw new InvalidOperationException("The clipboard is held by another application.");
        try { return body(); }
        finally { CloseClipboard(); }
    }

    private static string? ReadText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == 0) return null;
        var p = GlobalLock(h);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { GlobalUnlock(h); }
    }

    /// <summary>Copies <paramref name="bytes"/> into a movable global block and hands it to the clipboard (which then owns it).</summary>
    private static void Put(uint format, byte[] bytes)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (h == 0) throw new OutOfMemoryException("GlobalAlloc failed.");
        var p = GlobalLock(h);
        if (p == 0) { GlobalFree(h); throw new InvalidOperationException("GlobalLock failed."); }
        try { Marshal.Copy(bytes, 0, p, bytes.Length); }
        finally { GlobalUnlock(h); }
        if (SetClipboardData(format, h) == 0)
        {
            GlobalFree(h);
            throw new InvalidOperationException($"SetClipboardData({format}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>A packed 32-bit bottom-up DIB (BITMAPINFOHEADER + pixels), the form every app accepts as CF_DIB.</summary>
    internal static byte[] ToDib(Bitmap src)
    {
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(src, 0, 0);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            const int headerSize = 40;
            var rowBytes = bmp.Width * 4;
            var result = new byte[headerSize + rowBytes * bmp.Height];
            var span = result.AsSpan();
            BitConverter.TryWriteBytes(span[0..], headerSize);
            BitConverter.TryWriteBytes(span[4..], bmp.Width);
            BitConverter.TryWriteBytes(span[8..], bmp.Height);      // positive height: bottom-up
            BitConverter.TryWriteBytes(span[12..], (short)1);       // planes
            BitConverter.TryWriteBytes(span[14..], (short)32);      // bits per pixel
            BitConverter.TryWriteBytes(span[16..], 0);              // BI_RGB
            BitConverter.TryWriteBytes(span[20..], rowBytes * bmp.Height);
            var offset = headerSize;
            for (var y = bmp.Height - 1; y >= 0; y--)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, result, offset, rowBytes);
                offset += rowBytes;
            }
            return result;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}

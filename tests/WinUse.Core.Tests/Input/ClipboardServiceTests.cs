using System.Drawing;
using System.Drawing.Imaging;
using WinUse.Core.Input;
using WinUse.Core.Threading;

namespace WinUse.Core.Tests.Input;

[Collection("Desktop")]
public class ClipboardServiceTests
{
    private static byte[] Png(int w, int h, Color fill)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(fill);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Text_RoundTrips_AndReplacesAnImage()
    {
        using var d = new AutomationDispatcher();
        var cb = new ClipboardService(d);
        await cb.SetImageAsync(Png(4, 3, Color.Red));
        var withImage = await cb.GetAsync();
        Assert.True(withImage.HasImage);
        Assert.Null(withImage.Text);

        await cb.SetTextAsync("こんにちは winuse\r\nline 2");
        var c = await cb.GetAsync();
        Assert.Equal("こんにちは winuse\r\nline 2", c.Text);
        Assert.False(c.HasImage);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Image_IsReadableAsDib()
    {
        using var d = new AutomationDispatcher();
        var cb = new ClipboardService(d);
        await cb.SetImageAsync(Png(5, 7, Color.Blue));
        var (w, h) = await d.RunAsync(() =>
        {
            Assert.True(ClipboardNative.OpenClipboard(0));
            try
            {
                var hd = ClipboardNative.GetClipboardData(8 /* CF_DIB */);
                Assert.NotEqual(0, hd);
                var p = ClipboardNative.GlobalLock(hd);
                try { return (System.Runtime.InteropServices.Marshal.ReadInt32(p, 4), System.Runtime.InteropServices.Marshal.ReadInt32(p, 8)); }
                finally { ClipboardNative.GlobalUnlock(hd); }
            }
            finally { ClipboardNative.CloseClipboard(); }
        });
        Assert.Equal((5, 7), (w, h));
        await cb.SetTextAsync(""); // leave the clipboard without an image for other tests
    }

    [Fact]
    public void ToDib_WritesA32BitBottomUpHeader()
    {
        using var bmp = new Bitmap(2, 2, PixelFormat.Format24bppRgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 10, 20, 30)); // top-left
        bmp.SetPixel(1, 1, Color.FromArgb(255, 40, 50, 60)); // bottom-right
        var dib = ClipboardService.ToDib(bmp);
        Assert.Equal(40 + 2 * 2 * 4, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        Assert.Equal(0, BitConverter.ToInt32(dib, 16));
        // Bottom-up: the first stored row is the bottom row; pixels are BGRA.
        Assert.Equal(new byte[] { 60, 50, 40, 255 }, dib[(40 + 4)..(40 + 8)]);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, dib[(40 + 8)..(40 + 12)]);
    }
}

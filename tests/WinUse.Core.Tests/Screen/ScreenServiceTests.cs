using WinUse.Core.Screen;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Screen;

[Collection("Desktop")]
public class ScreenServiceTests
{
    [Fact, Trait("Category", "Integration")]
    public void ListMonitors_PrimaryFirstAtOrigin()
    {
        var monitors = ScreenService.ListMonitors();
        Assert.NotEmpty(monitors);
        Assert.Equal(Enumerable.Range(0, monitors.Count), monitors.Select(m => m.Index));
        var primary = Assert.Single(monitors, m => m.Primary);
        Assert.Equal(0, primary.Index);
        Assert.Equal((0, 0), (primary.Rect.X, primary.Rect.Y));
        Assert.True(primary.Rect.Width > 0 && primary.Rect.Height > 0);
        Assert.All(monitors, m => Assert.False(string.IsNullOrEmpty(m.Device)));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureScreen_DefaultsToThePrimaryMonitor()
    {
        using var d = new AutomationDispatcher();
        var cap = await new ScreenService(d).CaptureScreenAsync();
        var primary = ScreenService.ListMonitors().Single(m => m.Primary);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, cap.Png[..4]);
        Assert.Equal((primary.Rect.Width, primary.Rect.Height), (cap.Width, cap.Height));
        Assert.Equal((0, 0), (cap.OriginX, cap.OriginY));
        Assert.Equal(1.0, cap.Scale);
        Assert.Equal("screen", cap.Method);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureScreen_ByMonitorIndex_UsesThatMonitor()
    {
        using var d = new AutomationDispatcher();
        var svc = new ScreenService(d);
        var monitors = ScreenService.ListMonitors();
        var last = monitors[^1];
        var cap = await svc.CaptureScreenAsync(1.0, last.Index);
        Assert.Equal((last.Rect.X, last.Rect.Y, last.Rect.Width, last.Rect.Height), (cap.OriginX, cap.OriginY, cap.Width, cap.Height));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.CaptureScreenAsync(1.0, monitors.Count));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureVirtualScreen_CoversAllMonitors()
    {
        using var d = new AutomationDispatcher();
        var cap = await new ScreenService(d).CaptureVirtualScreenAsync();
        Assert.Equal(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN), cap.Width);
        Assert.Equal(NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN), cap.Height);
        Assert.Equal(NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN), cap.OriginX);
        Assert.Equal(NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN), cap.OriginY);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureRect_HasRectOrigin()
    {
        using var d = new AutomationDispatcher();
        var cap = await new ScreenService(d).CaptureRectAsync(new Rect(100, 50, 200, 120), 0.5);
        Assert.Equal((100, 50), (cap.OriginX, cap.OriginY));
        Assert.Equal((100, 60), (cap.Width, cap.Height));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureScreen_WithHalfScale_HalvesDimensions()
    {
        using var d = new AutomationDispatcher();
        var svc = new ScreenService(d);
        var full = await svc.CaptureScreenAsync();
        var half = await svc.CaptureScreenAsync(0.5);
        Assert.InRange(half.Width, full.Width / 2 - 1, full.Width / 2 + 1);
        Assert.InRange(half.Height, full.Height / 2 - 1, full.Height / 2 + 1);
        Assert.Equal(0.5, half.Scale);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureWindow_OfNotepad_HasWindowSize()
    {
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        using var np = await NotepadFixture.LaunchAsync(windows);
        Assert.True(NativeMethods.GetWindowRect(np.Window.Hwnd, out var r));
        var cap = await new ScreenService(d).CaptureWindowAsync(np.Window.Hwnd);
        Assert.Equal(r.Right - r.Left, cap.Width);
        Assert.Equal(r.Bottom - r.Top, cap.Height);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, cap.Png[..4]);

        using var img = new System.Drawing.Bitmap(new MemoryStream(cap.Png));
        var colors = new HashSet<int>();
        for (var y = 0; y < img.Height; y += 16)
            for (var x = 0; x < img.Width; x += 16)
            {
                var c = img.GetPixel(x, y);
                Assert.Equal(255, c.A);
                colors.Add(c.ToArgb());
            }
        Assert.True(colors.Count >= 2, "Window capture is blank (single colour).");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CaptureWindow_UsesScreenCopyInForeground_AndPrintWindowOnRequest()
    {
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        using var np = await NotepadFixture.LaunchAsync(windows);
        var svc = new ScreenService(d);
        Assert.True(await windows.ActivateAsync(np.Window.Hwnd));
        Assert.True(ScreenService.IsInForeground(np.Window.Hwnd));
        Assert.Equal("screen", (await svc.CaptureWindowAsync(np.Window.Hwnd)).Method);
        Assert.Equal("print", (await svc.CaptureWindowAsync(np.Window.Hwnd, 1.0, CaptureMethod.Print)).Method);
        Assert.Equal("screen", (await svc.CaptureWindowAsync(np.Window.Hwnd, 1.0, CaptureMethod.Screen)).Method);

        // Minimized: not in the foreground, so auto falls back to PrintWindow.
        Assert.True(await windows.SetStateAsync(np.Window.Hwnd, WindowState.Minimized));
        await Task.Delay(300);
        Assert.False(ScreenService.IsInForeground(np.Window.Hwnd));
    }
}

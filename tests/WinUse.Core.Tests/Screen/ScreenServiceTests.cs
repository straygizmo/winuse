using WinUse.Core.Screen;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Screen;

[Collection("Desktop")]
public class ScreenServiceTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task CaptureScreen_ReturnsPngOfVirtualScreenInPhysicalPixels()
    {
        using var d = new AutomationDispatcher();
        var cap = await new ScreenService(d).CaptureScreenAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, cap.Png[..4]);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        Assert.Equal(vw, cap.Width);
        Assert.Equal(vh, cap.Height);
        Assert.Equal(1.0, cap.Scale);
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
    }
}

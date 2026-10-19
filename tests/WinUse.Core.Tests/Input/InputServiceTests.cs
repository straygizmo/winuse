using System.Runtime.InteropServices;
using WinUse.Core.Input;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Input;

internal static class ClipboardNative
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalLock(nint h);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint h);
}

[Collection("Desktop")]
public class InputServiceTests
{
    private const uint CF_UNICODETEXT = 13;

    private static string? ReadClipboardText()
    {
        for (var i = 0; i < 20; i++)
        {
            if (ClipboardNative.OpenClipboard(0))
            {
                try
                {
                    var h = ClipboardNative.GetClipboardData(CF_UNICODETEXT);
                    if (h == 0) return null;
                    var p = ClipboardNative.GlobalLock(h);
                    if (p == 0) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { ClipboardNative.GlobalUnlock(h); }
                }
                finally { ClipboardNative.CloseClipboard(); }
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private static void ClearClipboard()
    {
        for (var i = 0; i < 20; i++)
        {
            if (ClipboardNative.OpenClipboard(0))
            {
                ClipboardNative.EmptyClipboard();
                ClipboardNative.CloseClipboard();
                return;
            }
            Thread.Sleep(50);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TypeText_IntoNotepad_AppearsInClipboardViaSelectAll()
    {
        using var d = new AutomationDispatcher();
        var windows = new WindowService(d);
        var input = new InputService(d);
        using var np = await NotepadFixture.LaunchAsync(windows);

        await d.RunAsync(ClearClipboard);
        Assert.True(await windows.ActivateAsync(np.Window.Hwnd));
        var focused = false;
        for (var k = 0; k < 10 && !focused; k++)
        {
            await Task.Delay(300);
            focused = await d.RunAsync(ClipboardNative.GetForegroundWindow) == np.Window.Hwnd;
            if (!focused) await windows.ActivateAsync(np.Window.Hwnd);
        }
        Assert.True(focused, "notepad never became the foreground window.");
        await input.TypeTextAsync("winuse テスト");
        await Task.Delay(300);
        await input.PressAsync(KeyChord.Parse("ctrl+a"));
        await Task.Delay(200);
        await input.PressAsync(KeyChord.Parse("ctrl+c"));
        await Task.Delay(500);

        var text = await d.RunAsync(ReadClipboardText);
        // Do not echo the clipboard on failure: it may hold unrelated user data.
        Assert.True(text is not null && text.Contains("winuse テスト"), $"Clipboard {(text is null ? "was empty" : $"had {text.Length} chars: {string.Join(" ", text.Select(c => ((int)c).ToString("X4")))}")}.");
        // Dispose kills only the notepad processes this test created (a save prompt may appear).
    }
}

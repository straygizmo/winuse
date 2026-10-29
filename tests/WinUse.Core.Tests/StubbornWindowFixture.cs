using System.Runtime.InteropServices;

namespace WinUse.Core.Tests;

/// <summary>
/// A visible, titled top-level window on its own message loop, optionally with a multi-line EDIT control that takes
/// the keyboard focus. By default it ignores WM_CLOSE, the way an app with a pending save prompt keeps its window
/// open. Unlike Notepad it leaves no restored tabs behind. <see cref="Dispose"/> destroys it.
/// </summary>
internal sealed class StubbornWindowFixture : IDisposable
{
    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WM_APP_DESTROY = 0x8000 + 7;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000;
    private const uint ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder text, int max);

    /// <summary>The current text of the EDIT control.</summary>
    public string EditText
    {
        get
        {
            var sb = new System.Text.StringBuilder(4096);
            GetWindowTextW(Edit, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    private static int _n;
    private readonly bool _refuseClose, _withEdit;
    private readonly WndProc _proc; // kept alive for the window's lifetime
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    public nint Hwnd { get; private set; }
    /// <summary>The EDIT control, when the fixture was created with one.</summary>
    public nint Edit { get; private set; }
    public string Title { get; }

    /// <param name="refuseClose">true: WM_CLOSE is ignored (a save prompt keeps the window); false: WM_CLOSE destroys the window like a plain app.</param>
    /// <param name="withEdit">Add a multi-line EDIT control that receives the focus whenever the window is activated.</param>
    public StubbornWindowFixture(bool refuseClose = true, bool withEdit = false)
    {
        _refuseClose = refuseClose;
        _withEdit = withEdit;
        Title = $"winuse stubborn {Environment.ProcessId}-{Interlocked.Increment(ref _n)}";
        _proc = Proc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "stubborn-window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || Hwnd == 0) throw new InvalidOperationException("stubborn window did not open");
    }

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLOSE when _refuseClose: return 0;  // refuse to close, like an app showing a save prompt
            case WM_SETFOCUS when Edit != 0: SetFocus(Edit); return 0;
            case WM_ACTIVATE when Edit != 0 && (wParam & 0xFFFF) != 0: SetFocus(Edit); return 0;
            case WM_APP_DESTROY: DestroyWindow(hwnd); return 0;
            case WM_DESTROY: PostQuitMessage(0); return 0;
            default: return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private void Loop()
    {
        // One class per window: the class carries the WndProc, so sharing it would route every window to the first instance.
        var cls = new WNDCLASSW { lpfnWndProc = _proc, lpszClassName = "WinUseStubbornWindow" + Title.GetHashCode(), hbrBackground = 6 /* COLOR_WINDOW + 1 */ };
        RegisterClassW(ref cls);
        Hwnd = CreateWindowExW(0, cls.lpszClassName, Title, WS_OVERLAPPEDWINDOW | WS_VISIBLE, 200, 200, 300, 200, 0, 0, 0, 0);
        if (_withEdit && Hwnd != 0)
        {
            Edit = CreateWindowExW(0, "EDIT", "", WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_AUTOVSCROLL, 10, 10, 260, 120, Hwnd, 0, 0, 0);
            SetFocus(Edit);
        }
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        if (Hwnd != 0) PostMessageW(Hwnd, WM_APP_DESTROY, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
        Hwnd = 0;
    }
}

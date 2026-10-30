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
    private const uint CBS_DROPDOWNLIST = 0x0003, WS_VSCROLL = 0x00200000, CB_ADDSTRING = 0x0143, CB_GETCURSEL = 0x0147, CB_SETCURSEL = 0x014E;
    private const uint BS_CHECKBOX = 0x0002, BS_AUTOCHECKBOX = 0x0003, BM_GETCHECK = 0x00F0, WM_COMMAND = 0x0111;
    private const int BN_CLICKED = 0;

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, string lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

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

    /// <summary>The items of the combo box, when the fixture was created with one.</summary>
    public static readonly string[] ComboItems = ["One", "Two", "Three"];

    /// <summary>The index of the combo box's current selection (-1: none).</summary>
    public int ComboSelection => (int)SendMessageW(Combo, CB_GETCURSEL, 0, 0);

    /// <summary>Whether the check box is checked (BM_GETCHECK).</summary>
    public bool IsChecked(nint button) => SendMessageW(button, BM_GETCHECK, 0, 0) != 0;

    private int _autoClicks, _manualClicks;
    /// <summary>How many BN_CLICKED notifications the auto check box has sent.</summary>
    public int AutoCheckClicks => Volatile.Read(ref _autoClicks);
    /// <summary>How many BN_CLICKED notifications the manual check box has sent (its state never changes by itself).</summary>
    public int ManualCheckClicks => Volatile.Read(ref _manualClicks);

    private static int _n;
    private readonly bool _refuseClose, _withEdit, _withCombo, _withChecks;
    private readonly WndProc _proc; // kept alive for the window's lifetime
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    public nint Hwnd { get; private set; }
    /// <summary>The EDIT control, when the fixture was created with one.</summary>
    public nint Edit { get; private set; }
    /// <summary>The drop-down list COMBOBOX control, when the fixture was created with one.</summary>
    public nint Combo { get; private set; }
    /// <summary>A BS_AUTOCHECKBOX button labelled "Auto" that toggles itself when clicked, when the fixture was created with check boxes.</summary>
    public nint AutoCheck { get; private set; }
    /// <summary>A BS_CHECKBOX button labelled "Manual" whose state the owner would have to set: a click never changes it.</summary>
    public nint ManualCheck { get; private set; }
    public string Title { get; }

    /// <param name="refuseClose">true: WM_CLOSE is ignored (a save prompt keeps the window); false: WM_CLOSE destroys the window like a plain app.</param>
    /// <param name="withEdit">Add a multi-line EDIT control that receives the focus whenever the window is activated.</param>
    /// <param name="withCombo">Add a drop-down list COMBOBOX holding <see cref="ComboItems"/>, the first one selected.</param>
    /// <param name="withChecks">Add the <see cref="AutoCheck"/> and <see cref="ManualCheck"/> check boxes, both unchecked.</param>
    public StubbornWindowFixture(bool refuseClose = true, bool withEdit = false, bool withCombo = false, bool withChecks = false)
    {
        _refuseClose = refuseClose;
        _withEdit = withEdit;
        _withCombo = withCombo;
        _withChecks = withChecks;
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
            case WM_COMMAND when ((wParam >> 16) & 0xFFFF) == BN_CLICKED && lParam == AutoCheck && AutoCheck != 0: Interlocked.Increment(ref _autoClicks); return 0;
            case WM_COMMAND when ((wParam >> 16) & 0xFFFF) == BN_CLICKED && lParam == ManualCheck && ManualCheck != 0: Interlocked.Increment(ref _manualClicks); return 0;
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
        if (_withCombo && Hwnd != 0)
        {
            Combo = CreateWindowExW(0, "COMBOBOX", "", WS_CHILD | WS_VISIBLE | WS_VSCROLL | CBS_DROPDOWNLIST, 10, _withEdit ? 140 : 10, 200, 150, Hwnd, 0, 0, 0);
            foreach (var item in ComboItems) SendMessageW(Combo, CB_ADDSTRING, 0, item);
            SendMessageW(Combo, CB_SETCURSEL, 0, 0);
        }
        if (_withChecks && Hwnd != 0)
        {
            var top = (_withEdit ? 140 : 10) + (_withCombo ? 30 : 0);
            AutoCheck = CreateWindowExW(0, "BUTTON", "Auto", WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX, 10, top, 120, 24, Hwnd, 0, 0, 0);
            ManualCheck = CreateWindowExW(0, "BUTTON", "Manual", WS_CHILD | WS_VISIBLE | BS_CHECKBOX, 140, top, 120, 24, Hwnd, 0, 0, 0);
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

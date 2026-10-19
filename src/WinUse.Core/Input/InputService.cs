using System.Runtime.InteropServices;
using WinUse.Core.Threading;
using static WinUse.Core.Windows.NativeMethods;

namespace WinUse.Core.Input;

public sealed class InputService(AutomationDispatcher d)
{
    private const int WheelDelta = 120;
    private const int StepSleepMs = 20;

    public Task ClickAsync(int x, int y, MouseButton b = MouseButton.Left, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        return d.RunAsync(() =>
        {
            var (down, up) = ButtonFlags(b);
            Send(MoveInput(x, y));
            for (var i = 0; i < count; i++)
                Send(MouseButtonInput(down), MouseButtonInput(up));
        });
    }

    public Task MoveToAsync(int x, int y) => d.RunAsync(() => Send(MoveInput(x, y)));

    public Task DragAsync(int x1, int y1, int x2, int y2) => d.RunAsync(() =>
    {
        const int steps = 5;
        Send(MoveInput(x1, y1));
        Thread.Sleep(StepSleepMs);
        Send(MouseButtonInput(MOUSEEVENTF_LEFTDOWN));
        Thread.Sleep(StepSleepMs);
        try
        {
            for (var i = 1; i <= steps; i++)
            {
                Send(MoveInput(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps));
                Thread.Sleep(StepSleepMs);
            }
        }
        finally
        {
            Send(MouseButtonInput(MOUSEEVENTF_LEFTUP));
        }
    });

    public Task ScrollAsync(int x, int y, int deltaNotches) => d.RunAsync(() =>
    {
        Send(MoveInput(x, y));
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = { mouseData = unchecked((uint)(deltaNotches * WheelDelta)), dwFlags = MOUSEEVENTF_WHEEL, dwExtraInfo = (nuint)GetMessageExtraInfo() } },
        });
    });

    public Task TypeTextAsync(string text, int perCharDelayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        return d.RunAsync(() =>
        {
            // The IME is closed while typing and restored afterwards, so packets are not composed.
            var restore = SuspendIme();
            try
            {
                // One SendInput per character (surrogate pairs kept together), then a round trip to the
                // focused window. Queuing KEYEVENTF_UNICODE events faster than the target thread
                // translates them delivered the latest character for all of them ("テスト" -> "トトト").
                foreach (var unit in SplitUnits(text))
                {
                    Send(BuildTextInputs(unit).ToArray());
                    WaitForTarget();
                    if (perCharDelayMs > 0) Thread.Sleep(perCharDelayMs);
                }
            }
            finally
            {
                restore?.Invoke();
            }
        });
    }

    private static nint FocusedWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return 0;
        var gi = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(fg, out _), ref gi) && gi.hwndFocus != 0 ? gi.hwndFocus : fg;
    }

    private static void WaitForTarget()
    {
        var target = FocusedWindow();
        if (target != 0) SendMessageTimeoutW(target, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, 200, out _);
    }

    /// <summary>Closes the IME of the focused window if it is open; returns an action that reopens it.</summary>
    private static Action? SuspendIme()
    {
        var ime = ImmGetDefaultIMEWnd(FocusedWindow());
        if (ime == 0) return null;
        if (SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0, SMTO_ABORTIFHUNG, 200, out var open) == 0 || open == 0)
            return null;
        SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_SETOPENSTATUS, 0, SMTO_ABORTIFHUNG, 200, out _);
        return () => SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_SETOPENSTATUS, 1, SMTO_ABORTIFHUNG, 200, out _);
    }

    public Task PressAsync(KeyChord chord) => d.RunAsync(() =>
    {
        var inputs = new List<INPUT>();
        foreach (var m in chord.Modifiers) inputs.Add(KeyInput(m, false));
        inputs.Add(KeyInput(chord.Key, false));
        inputs.Add(KeyInput(chord.Key, true));
        for (var i = chord.Modifiers.Count - 1; i >= 0; i--) inputs.Add(KeyInput(chord.Modifiers[i], true));
        Send(inputs.ToArray());
    });

    private static IEnumerable<string> SplitUnits(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            yield return text.Substring(i, len);
            i += len - 1;
        }
    }

    private static IEnumerable<INPUT> BuildTextInputs(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                yield return KeyInput(0x0D, false);
                yield return KeyInput(0x0D, true);
                continue;
            }
            yield return UnicodeInput(ch, false);
            yield return UnicodeInput(ch, true);
        }
    }

    private static bool IsExtended(ushort vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B;

    private static INPUT KeyInput(ushort vk, bool up)
    {
        var flags = (up ? KEYEVENTF_KEYUP : 0u) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0u);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = { ki = { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, 0), dwFlags = flags, dwExtraInfo = (nuint)GetMessageExtraInfo() } },
        };
    }

    private static INPUT UnicodeInput(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = { ki = { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0u), dwExtraInfo = (nuint)GetMessageExtraInfo() } },
    };

    private static (uint down, uint up) ButtonFlags(MouseButton b) => b switch
    {
        MouseButton.Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };

    private static INPUT MouseButtonInput(uint flags) => new()
    {
        type = INPUT_MOUSE,
        u = { mi = { dwFlags = flags, dwExtraInfo = (nuint)GetMessageExtraInfo() } },
    };

    private static INPUT MoveInput(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 1), vh = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 1);
        var ax = (int)Math.Clamp((long)Math.Round((x - vx) * 65535.0 / Math.Max(vw - 1, 1)), 0, 65535);
        var ay = (int)Math.Clamp((long)Math.Round((y - vy) * 65535.0 / Math.Max(vh - 1, 1)), 0, 65535);
        return new INPUT
        {
            type = INPUT_MOUSE,
            u = { mi = { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, dwExtraInfo = (nuint)GetMessageExtraInfo() } },
        };
    }

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent} of {inputs.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
    }
}

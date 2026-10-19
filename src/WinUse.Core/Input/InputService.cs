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

    public async Task TypeTextAsync(string text, int perCharDelayMs = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (perCharDelayMs <= 0)
        {
            // One SendInput call per character: large batches of KEYEVENTF_UNICODE events get
            // reordered/duplicated by some text controls (notably packaged Notepad with an IME active).
            await d.RunAsync(() =>
            {
                foreach (var unit in SplitUnits(text)) Send(BuildTextInputs(unit).ToArray());
            });
            return;
        }
        // Keep surrogate pairs together so the receiver never sees half a character.
        foreach (var piece in SplitUnits(text))
        {
            await d.RunAsync(() => Send(BuildTextInputs(piece).ToArray()));
            await Task.Delay(perCharDelayMs);
        }
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

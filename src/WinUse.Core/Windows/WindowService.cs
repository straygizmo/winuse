using System.Diagnostics;
using System.Text.RegularExpressions;
using WinUse.Core.Refs;
using WinUse.Core.Threading;

namespace WinUse.Core.Windows;

public sealed class WindowService(AutomationDispatcher d)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public Task<IReadOnlyList<WindowInfo>> ListAsync(string? titleRegex = null, string? processName = null) =>
        d.RunAsync<IReadOnlyList<WindowInfo>>(() =>
        {
            var re = titleRegex is null ? null : BuildRegex(titleRegex);
            return Enumerate()
                .Where(w => re is null || re.IsMatch(w.Title))
                .Where(w => processName is null || ProcessMatches(w.ProcessName, processName))
                .ToList();
        });

    public Task<WindowInfo?> FindAsync(WindowLocator loc) =>
        d.RunAsync<WindowInfo?>(() => Enumerate().FirstOrDefault(w => Matches(w, loc)));

    public Task<bool> ExistsAsync(WindowLocator loc) =>
        d.RunAsync(() => Enumerate().Any(w => Matches(w, loc)));

    /// <summary>The foreground window, even when it is untitled (a modal dialog), or null when there is none.</summary>
    public Task<WindowInfo?> ForegroundAsync() => d.RunAsync<WindowInfo?>(() =>
    {
        var fg = NativeMethods.GetForegroundWindow();
        return fg == 0 ? null : InfoOf(fg);
    });

    /// <summary>
    /// The top-level window under a screen point (the root of whatever child window is hit, so a WebView2 or UWP content
    /// window resolves to the frame that hosts it), or null when no window is there.
    /// </summary>
    public Task<WindowInfo?> RootWindowAtAsync(int x, int y) => d.RunAsync<WindowInfo?>(() =>
    {
        var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (hit == 0) return null;
        var root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
        return InfoOf(root == 0 ? hit : root);
    });

    /// <summary>True while <paramref name="hwnd"/> is an existing, visible window.</summary>
    public Task<bool> IsAliveAsync(nint hwnd) => d.RunAsync(() => NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd));

    public async Task<bool> ActivateAsync(nint hwnd)
    {
        // A freshly launched window may still be taking foreground itself; retry briefly.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await d.RunAsync(() => TryActivate(hwnd)).ConfigureAwait(false)) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }

    private static bool TryActivate(nint hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        // SetForegroundWindow can report success without the window actually taking the foreground
        // (foreground lock); only trust it when the foreground really changed.
        if (NativeMethods.SetForegroundWindow(hwnd) && NativeMethods.GetForegroundWindow() == hwnd) return true;

        var current = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        // Attach to the target window's thread and to the current foreground thread so the
        // foreground lock does not reject us; detach afterwards.
        var attachedTarget = targetThread != 0 && targetThread != current
            && NativeMethods.AttachThreadInput(current, targetThread, true);
        var attachedFg = fgThread != 0 && fgThread != current && fgThread != targetThread
            && NativeMethods.AttachThreadInput(current, fgThread, true);
        try { return NativeMethods.SetForegroundWindow(hwnd); }
        finally
        {
            if (attachedFg) NativeMethods.AttachThreadInput(current, fgThread, false);
            if (attachedTarget) NativeMethods.AttachThreadInput(current, targetThread, false);
        }
    }

    public Task<bool> CloseAsync(nint hwnd) =>
        d.RunAsync(() => NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, 0, 0));

    /// <summary>Waits until <paramref name="hwnd"/> is destroyed or hidden; false when it is still there after <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitGoneAsync(nint hwnd, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (!await IsAliveAsync(hwnd).ConfigureAwait(false)) return true;
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    public Task<bool> SetStateAsync(nint hwnd, WindowState s) => d.RunAsync(() =>
    {
        var cmd = s switch
        {
            WindowState.Minimized => NativeMethods.SW_MINIMIZE,
            WindowState.Maximized => NativeMethods.SW_MAXIMIZE,
            _ => NativeMethods.SW_RESTORE,
        };
        // ShowWindow returns the previous visibility, not success.
        NativeMethods.ShowWindow(hwnd, cmd);
        return true;
    });

    public Task<bool> MoveAsync(nint hwnd, Rect r) =>
        d.RunAsync(() => NativeMethods.MoveWindow(hwnd, r.X, r.Y, r.Width, r.Height, true));

    public async Task<WindowInfo?> WaitForAsync(WindowLocator loc, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var w = await FindAsync(loc).ConfigureAwait(false);
            if (w is not null) return w;
            if (sw.Elapsed >= timeout) return null;
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    public async Task<bool> WaitClosedAsync(WindowLocator loc, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (!await ExistsAsync(loc).ConfigureAwait(false)) return true;
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the window for a locator; when no title matches (titles change, e.g. an unsaved marker) falls back
    /// to the single window with the same process (and class, when recorded). Ambiguity yields null.
    /// </summary>
    public async Task<WindowInfo?> FindWithFallbackAsync(WindowLocator loc) =>
        PickWithFallback(await ListAsync().ConfigureAwait(false), loc);

    /// <summary>Pid of the process that owns <paramref name="hwnd"/> (any window, not only top-level), or null.</summary>
    public static int? ProcessIdOf(nint hwnd) =>
        hwnd != 0 && NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) != 0 ? (int)pid : null;

    public static WindowInfo? PickWithFallback(IReadOnlyList<WindowInfo> windows, WindowLocator loc)
    {
        var exact = windows.FirstOrDefault(w => Matches(w, loc));
        if (exact is not null || loc.ProcessName is null) return exact;
        var same = windows
            .Where(w => ProcessMatches(w.ProcessName, loc.ProcessName))
            .Where(w => loc.ClassName is null || string.Equals(w.ClassName, loc.ClassName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return same.Count == 1 ? same[0] : null;
    }

    public static bool Matches(WindowInfo w, WindowLocator loc)
    {
        if (!BuildRegex(loc.TitleRegex).IsMatch(w.Title)) return false;
        if (loc.ProcessName is not null && !ProcessMatches(w.ProcessName, loc.ProcessName)) return false;
        if (loc.ClassName is not null && !string.Equals(w.ClassName, loc.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static Regex BuildRegex(string pattern)
    {
        const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try { return new Regex(pattern, opts); }
        catch (ArgumentException) { return new Regex(Regex.Escape(pattern), opts); }
    }

    private static string StripExe(string n) =>
        n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

    /// <summary>Process names compare case-insensitively, with or without <c>.exe</c>.</summary>
    public static bool ProcessMatches(string actual, string wanted) =>
        string.Equals(StripExe(actual), StripExe(wanted), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The window's visible frame (DWM extended frame bounds, physical pixels): what the user sees, without the
    /// invisible resize borders that <c>GetWindowRect</c> includes on Windows 10/11. Falls back to the raw rect.
    /// </summary>
    public static Rect VisibleRectOf(nint hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var fr, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && fr.Right > fr.Left && fr.Bottom > fr.Top)
            return new Rect(fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
        NativeMethods.GetWindowRect(hwnd, out var rc);
        return new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    public Task<Rect> VisibleRectAsync(nint hwnd) => d.RunAsync(() => VisibleRectOf(hwnd));

    private static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindowTextLengthW(hwnd) == 0) return true;
            if ((NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            var info = InfoOf(hwnd);
            if (info.Title.Length == 0) return true;
            result.Add(info);
            return true;
        }, 0);
        return result;
    }

    /// <summary>Title, class, process, rect and state of any window handle (no visibility or title filtering).</summary>
    private static WindowInfo InfoOf(nint hwnd)
    {
        var title = ReadText(hwnd, NativeMethods.GetWindowTextW);
        var cls = ReadText(hwnd, NativeMethods.GetClassNameW);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        NativeMethods.GetWindowRect(hwnd, out var rc);
        var state = NativeMethods.IsIconic(hwnd) ? WindowState.Minimized
            : NativeMethods.IsZoomed(hwnd) ? WindowState.Maximized : WindowState.Normal;
        return new WindowInfo(hwnd, title, cls, ProcessNameOf((int)pid), (int)pid,
            new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top), state);
    }

    private static string ReadText(nint hwnd, Func<nint, char[], int, int> read)
    {
        var buf = new char[512];
        var n = read(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    private static string ProcessNameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }
}

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

    public Task<bool> ActivateAsync(nint hwnd) => d.RunAsync(() =>
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (NativeMethods.SetForegroundWindow(hwnd)) return true;

        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var current = NativeMethods.GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != current
            && NativeMethods.AttachThreadInput(current, targetThread, true);
        try { return NativeMethods.SetForegroundWindow(hwnd); }
        finally { if (attached) NativeMethods.AttachThreadInput(current, targetThread, false); }
    });

    public Task<bool> CloseAsync(nint hwnd) =>
        d.RunAsync(() => NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, 0, 0));

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

    private static bool ProcessMatches(string actual, string wanted) =>
        string.Equals(StripExe(actual), StripExe(wanted), StringComparison.OrdinalIgnoreCase);

    private static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindowTextLengthW(hwnd) == 0) return true;
            if ((NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            var title = ReadText(hwnd, NativeMethods.GetWindowTextW);
            if (title.Length == 0) return true;
            var cls = ReadText(hwnd, NativeMethods.GetClassNameW);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            NativeMethods.GetWindowRect(hwnd, out var rc);
            var state = NativeMethods.IsIconic(hwnd) ? WindowState.Minimized
                : NativeMethods.IsZoomed(hwnd) ? WindowState.Maximized : WindowState.Normal;
            result.Add(new WindowInfo(hwnd, title, cls, ProcessNameOf((int)pid), (int)pid,
                new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top), state));
            return true;
        }, 0);
        return result;
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

using System.Diagnostics;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests;

/// <summary>Launches a notepad that did not exist before and cleans up only processes it created.</summary>
/// Tests using it must share the "Desktop" collection: cleanup kills every notepad this fixture did not
/// see at launch, which would otherwise race with other tests that run in parallel.
internal sealed class NotepadFixture : IDisposable
{
    private readonly HashSet<int> _preexisting;
    private Process? _proc;

    public WindowInfo Window { get; private set; } = null!;

    private NotepadFixture(HashSet<int> pre) => _preexisting = pre;

    public static async Task<NotepadFixture> LaunchAsync(WindowService svc)
    {
        var pre = Process.GetProcessesByName("notepad").Select(x => { var id = x.Id; x.Dispose(); return id; }).ToHashSet();
        var f = new NotepadFixture(pre) { _proc = Process.Start("notepad.exe") };
        try
        {
            WindowInfo? w = null;
            var sw = Stopwatch.StartNew();
            while (w is null && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                w = (await svc.ListAsync(null, "notepad")).FirstOrDefault(x => !pre.Contains(x.Pid));
                if (w is null) await Task.Delay(200);
            }
            Assert.True(w is not null, "No new notepad window appeared within 10 s.");
            await Task.Delay(500);
            f.Window = (await svc.ListAsync(null, "notepad")).FirstOrDefault(x => x.Hwnd == w.Hwnd) ?? w;
            return f;
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var n in Process.GetProcessesByName("notepad"))
        {
            try { if (!_preexisting.Contains(n.Id)) n.Kill(); } catch { }
            n.Dispose();
        }
        _proc?.Dispose();
        _proc = null;
    }
}

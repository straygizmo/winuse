using System.Diagnostics;
using WinUse.Core.Refs;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Windows;

[Collection("Desktop")]
public class WindowServiceTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task Notepad_CanBeFoundActivatedAndClosed()
    {
        using var d = new AutomationDispatcher();
        var svc = new WindowService(d);
        var preexisting = Process.GetProcessesByName("notepad").Select(x => { var id = x.Id; x.Dispose(); return id; }).ToHashSet();
        using var p = Process.Start("notepad.exe");
        try
        {
            // Only ever touch a notepad window that did not exist before the test started.
            WindowInfo? w = null;
            var sw = Stopwatch.StartNew();
            while (w is null && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                w = (await svc.ListAsync(null, "notepad")).FirstOrDefault(x => !preexisting.Contains(x.Pid));
                if (w is null) await Task.Delay(200);
            }
            Assert.True(w is not null, "No new notepad window appeared within 10 s; an existing single-instance notepad may have prevented an isolated window.");
            Assert.DoesNotContain(w.Pid, preexisting);
            // Packaged notepad may finish initialising (and re-title) just after first appearing; let it settle.
            await Task.Delay(500);
            w = (await svc.ListAsync(null, "notepad")).FirstOrDefault(x => x.Hwnd == w.Hwnd) ?? w;
            // Locator scoped to our window: exact title + class (pre-existing windows are excluded by the Pid check below).
            var loc = new WindowLocator("^" + System.Text.RegularExpressions.Regex.Escape(w.Title) + "$", "notepad", w.ClassName);
            Assert.True(await svc.ActivateAsync(w.Hwnd));
            Assert.True(await svc.SetStateAsync(w.Hwnd, WindowState.Maximized));
            Assert.True(await svc.ExistsAsync(loc));
            Assert.True(await svc.CloseAsync(w.Hwnd));
            // Wait for our specific hwnd to vanish (a pre-existing notepad could still match the locator).
            var closed = false;
            sw.Restart();
            while (!closed && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                closed = (await svc.ListAsync(null, "notepad")).All(x => x.Hwnd != w.Hwnd);
                if (!closed) await Task.Delay(200);
            }
            Assert.True(closed);
        }
        finally
        {
            foreach (var n in Process.GetProcessesByName("notepad"))
            {
                try { if (!preexisting.Contains(n.Id)) n.Kill(); } catch { }
                n.Dispose();
            }
        }
    }
}

using System.Diagnostics;
using WinUse.Core.Refs;
using WinUse.Core.Threading;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Windows;

public class WindowServiceTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task Notepad_CanBeFoundActivatedAndClosed()
    {
        using var d = new AutomationDispatcher();
        var svc = new WindowService(d);
        var preexisting = Process.GetProcessesByName("notepad").Select(x => { var id = x.Id; x.Dispose(); return id; }).ToHashSet();
        using var p = Process.Start("notepad.exe");
        var loc = new WindowLocator(".", "notepad", null);
        try
        {
            var w = await svc.WaitForAsync(loc, TimeSpan.FromSeconds(10));
            Assert.NotNull(w);
            Assert.True(await svc.ActivateAsync(w.Hwnd));
            Assert.True(await svc.SetStateAsync(w.Hwnd, WindowState.Maximized));
            Assert.True(await svc.ExistsAsync(loc));
            Assert.True(await svc.CloseAsync(w.Hwnd));
            Assert.True(await svc.WaitClosedAsync(loc, TimeSpan.FromSeconds(10)));
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

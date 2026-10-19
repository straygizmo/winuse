using System.Diagnostics;
using WinUse.Core.Refs;
using WinUse.Core.Input;
using WinUse.Core.Threading;
using WinUse.Core.Uia;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Uia;

[Collection("Desktop")]
[Trait("Category", "Integration")]
public class UiaServiceTests
{
    private static UiNode? Find(UiNode n, Func<UiNode, bool> p)
    {
        if (p(n)) return n;
        foreach (var c in n.Children) { var r = Find(c, p); if (r is not null) return r; }
        return null;
    }

    [Fact]
    public async Task Notepad_TreeContainsDocument_AndSetTextRoundTrips()
    {
        using var d = new AutomationDispatcher();
        var refs = new RefRegistry();
        var windows = new WindowService(d);
        using var uia = new UiaService(d, refs, windows);
        using var np = await NotepadFixture.LaunchAsync(windows);

        var tree = await uia.GetTreeAsync(np.Window.Hwnd, default!);
        var doc = Find(tree, n => n.ControlType is "Document" or "Edit");
        Assert.NotNull(doc);

        try
        {
            await uia.SetTextAsync(doc!.Ref, "hello uia");
            var text = await uia.GetTextAsync(doc.Ref);
            Assert.Equal("hello uia", text.TrimEnd('\r', '\n'));

            Assert.True(refs.TryGet(doc.Ref, out var er));
            var el = await uia.ResolveAsync((UiaLocator)er.Locator);
            Assert.NotNull(el);
            var orig = (FlaUI.Core.AutomationElements.AutomationElement)er.Handle!;
            var (a, b) = await d.RunAsync(() => (orig.Properties.RuntimeId.ValueOrDefault, el!.Properties.RuntimeId.ValueOrDefault));
            Assert.Equal(a, b);
            Assert.NotNull(Find(tree, n => n.ControlType == "Pane" && n.Children.Count > 0));
        }
        finally
        {
            // Notepad restores unsaved tabs on next launch; leave it empty so later tests are unaffected.
            await uia.SetTextAsync(doc!.Ref, "");
            await Task.Delay(500);
        }
    }

    [Fact]
    public async Task Calculator_InvokeButtons_ComputesSum()
    {
        using var d = new AutomationDispatcher();
        var refs = new RefRegistry();
        var windows = new WindowService(d);
        using var uia = new UiaService(d, refs, windows);

        // Isolated like NotepadFixture: never touch a Calculator the user already has open.
        static bool IsCalc(WindowInfo x) =>
            x.ProcessName.Contains("Calculator", StringComparison.OrdinalIgnoreCase) || x.Title is "Calculator" or "電卓";
        var prePids = CalculatorPids();
        var preWindows = (await windows.ListAsync()).Where(IsCalc).Select(x => x.Hwnd).ToHashSet();

        using var proc = Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
        WindowInfo? w = null;
        var sw = Stopwatch.StartNew();
        while (w is null && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var newPids = CalculatorPids().Except(prePids).ToHashSet();
            // A window of a new CalculatorApp process, or a new calculator window (it may be hosted by
            // ApplicationFrameHost, whose pid is shared); never one that existed before the test.
            w = (await windows.ListAsync()).FirstOrDefault(x => !preWindows.Contains(x.Hwnd) && (newPids.Contains(x.Pid) || IsCalc(x)));
            if (w is null) await Task.Delay(300);
        }
        try
        {
            Assert.True(w is not null, "No new Windows Calculator window appeared within 20 s (is Calculator installed?).");
            await Task.Delay(1000);
            var tree = await uia.GetTreeAsync(w!.Hwnd, new TreeOptions(InteractiveOnly: false, MaxDepth: 20));
            string RefOf(string id)
            {
                var n = Find(tree, x => x.AutomationId == id);
                Assert.True(n is not null, $"Calculator element '{id}' not found.");
                return n!.Ref;
            }
            await uia.InvokeAsync(RefOf("num1Button"));
            await uia.InvokeAsync(RefOf("plusButton"));
            await uia.InvokeAsync(RefOf("num2Button"));
            await uia.InvokeAsync(RefOf("equalButton"));
            await Task.Delay(300);
            var res = await uia.GetElementAsync(RefOf("CalculatorResults"));
            Assert.Contains("3", res.Name);
        }
        finally
        {
            if (w is not null) await windows.CloseAsync(w.Hwnd); // only the window this test opened
            await Task.Delay(500);
            foreach (var pid in CalculatorPids().Except(prePids))
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { }
            }
        }
    }

    private static HashSet<int> CalculatorPids() =>
        Process.GetProcessesByName("CalculatorApp").Select(x => { var id = x.Id; x.Dispose(); return id; }).ToHashSet();
}

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

        await uia.SetTextAsync(doc!.Ref, "hello uia");
        var text = await uia.GetTextAsync(doc.Ref);
        Assert.Equal("hello uia", text.TrimEnd('\r', '\n'));

        Assert.True(refs.TryGet(doc.Ref, out var er));
        var el = await uia.ResolveAsync((UiaLocator)er.Locator);
        Assert.NotNull(el);
    }

    [Fact]
    public async Task Calculator_InvokeButtons_ComputesSum()
    {
        using var d = new AutomationDispatcher();
        var refs = new RefRegistry();
        var windows = new WindowService(d);
        using var uia = new UiaService(d, refs, windows);

        using var proc = Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
        WindowInfo? w = null;
        var sw = Stopwatch.StartNew();
        while (w is null && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            w = (await windows.ListAsync()).FirstOrDefault(x =>
                x.ProcessName.Contains("Calculator", StringComparison.OrdinalIgnoreCase)
                || x.Title is "Calculator" or "電卓");
            if (w is null) await Task.Delay(300);
        }
        Assert.True(w is not null, "Windows Calculator window did not appear within 20 s (is Calculator installed?).");
        try
        {
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
        finally { await windows.CloseAsync(w!.Hwnd); }
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using WinUse.Core.Windows;
using WinUse.Tools;

namespace WinUse.Core.Tests.Tools;

/// <summary>
/// Tools driven end to end against real windows (Notepad, a stubborn test window). The tool methods are called the way
/// the MCP server calls them; the dispatcher is not used because it refuses ref ids that carry no recorded locator.
/// </summary>
[Collection("Desktop")]
public sealed class DesktopToolTests : IDisposable
{
    private readonly ToolContext ctx = ToolContext.Create(TestConfig.WithRoots(Path.GetTempPath()));

    public void Dispose() => ctx.Dispose();

    private sealed record Outcome(bool Ok, JsonObject Result, string? Error)
    {
        public bool? Passed => Result["passed"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    private static async Task<Outcome> Run(Task<CallToolResult> call)
    {
        var r = await call;
        var text = r.Content.OfType<TextContentBlock>().Last().Text;
        var o = JsonNode.Parse(text)!.AsObject();
        return r.IsError == true ? new(false, o, o["error"]?.GetValue<string>()) : new(true, o, null);
    }

    private static string Exact(string title) => "^" + Regex.Escape(title) + "$";

    [Fact]
    public async Task Sleep_WaitsAndReportsElapsed()
    {
        var sw = Stopwatch.StartNew();
        var o = await Run(WaitTools.Sleep(ctx, 250));
        Assert.True(o.Ok, o.Error);
        Assert.InRange(sw.ElapsedMilliseconds, 230, 5000);
        Assert.InRange(o.Result["slept_ms"]!.GetValue<long>(), 230, 5000);
        Assert.Null(o.Passed);
        Assert.False((await Run(WaitTools.Sleep(ctx, -1))).Ok);
        Assert.Equal(["sleep", "sleep"], ctx.Journal.Entries.Select(e => e.Tool));
    }

    [Fact]
    public async Task WaitFor_Time_PassesAfterTheTimeout()
    {
        var sw = Stopwatch.StartNew();
        var o = await Run(WaitTools.WaitFor(ctx, "time", 200));
        Assert.True(o.Ok, o.Error);
        Assert.True(o.Passed);
        Assert.InRange(sw.ElapsedMilliseconds, 180, 5000);
        Assert.Null(o.Result["reason"]);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ResolveWindow_ByTitle_RetriesForTheGracePeriodBeforeFailing()
    {
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ResolveWindowAsync(null, "^zz-never-matches-zz$", false));
        Assert.Contains("No window matches", ex.Message);
        Assert.InRange(sw.Elapsed, ToolContext.TitleGrace - TimeSpan.FromMilliseconds(100), ToolContext.TitleGrace + TimeSpan.FromSeconds(5));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ResolveWindow_ByTitle_FindsAWindowThatAppearsDuringTheGrace()
    {
        StubbornWindowFixture? win = null;
        try
        {
            var prefix = $"winuse stubborn {Environment.ProcessId}-";
            var resolving = ctx.ResolveWindowAsync(null, "^" + Regex.Escape(prefix), false);
            await Task.Delay(400);
            win = new StubbornWindowFixture();
            var w = await resolving;
            Assert.Equal(win.Hwnd, w.Hwnd);
        }
        finally
        {
            win?.Dispose();
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task WindowClose_ReportsStillOpen_WhenTheWindowRefusesToClose()
    {
        using var win = new StubbornWindowFixture();
        var o = await Run(WindowTools.WindowClose(ctx, title: Exact(win.Title)));
        Assert.True(o.Ok, o.Error);
        var result = o.Result["result"]!;
        Assert.False(result["closed"]!.GetValue<bool>());
        Assert.Equal("still_open", result["reason"]!.GetValue<string>());
        Assert.NotNull(result["foreground"]);
        Assert.True(await ctx.Windows.IsAliveAsync(win.Hwnd));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task WindowClose_ReportsClosed_WhenTheWindowGoesAway()
    {
        // Not Notepad: it restores the (dirty) tabs of earlier sessions and then rightly keeps its window open.
        using var win = new StubbornWindowFixture(refuseClose: false);
        var o = await Run(WindowTools.WindowClose(ctx, title: Exact(win.Title)));
        Assert.True(o.Ok, o.Error);
        Assert.True(o.Result["result"]!["closed"]!.GetValue<bool>(), o.Result.ToJsonString());
        Assert.Null(o.Result["result"]!["reason"]);
        Assert.False(await ctx.Windows.IsAliveAsync(win.Hwnd));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task TypeText_WithWindow_ActivatesItFirst()
    {
        using var decoy = new StubbornWindowFixture();
        using var target = new StubbornWindowFixture(withEdit: true);
        Assert.True(await ctx.Windows.ActivateAsync(decoy.Hwnd));
        var info = (await ctx.Windows.ListAsync()).Single(w => w.Hwnd == target.Hwnd);
        var refId = ctx.RegisterWindow(info).Id;

        var o = await Run(InputTools.TypeText(ctx, "ab", window: refId));
        Assert.True(o.Ok, o.Error);
        Assert.Equal(2, o.Result["typed"]!.GetValue<int>());
        Assert.Equal(refId, o.Result["window"]!["ref"]!.GetValue<string>());
        Assert.Equal(target.Hwnd, (await ctx.Windows.ForegroundAsync())?.Hwnd);
        await Task.Delay(200);
        Assert.Equal("ab", target.EditText);
        // The window argument is journaled with its locator, so a scenario can replay it.
        Assert.NotNull(ctx.Journal.Entries.Last().Args[ToolContext.ResolvedRefsKey]?["window"]);

        var keys = await Run(InputTools.PressKeys(ctx, "ctrl+a", window: Exact(target.Title)));
        Assert.True(keys.Ok, keys.Error);
        Assert.Equal(target.Title, keys.Result["window"]!["title"]!.GetValue<string>());

        var missing = await Run(InputTools.PressKeys(ctx, "esc", window: "^zz-no-such-window-zz$"));
        Assert.False(missing.Ok);
        Assert.Contains("No window matches", missing.Error);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Click_WithWindow_TakesWindowRelativeCoordinates()
    {
        using var np = await NotepadFixture.LaunchAsync(ctx.Windows);
        var refId = ctx.RegisterWindow(np.Window).Id;
        var rect = (await ctx.Windows.ListAsync()).Single(w => w.Hwnd == np.Window.Hwnd).Rect;
        // The centre of the window is always safe to click and hover.
        int x = rect.Width / 2, y = rect.Height / 2;

        var click = await Run(InputTools.Click(ctx, x: x, y: y, window: refId));
        Assert.True(click.Ok, click.Error);
        Assert.Equal((rect.X + x, rect.Y + y), (click.Result["x"]!.GetValue<int>(), click.Result["y"]!.GetValue<int>()));
        Assert.Equal(rect.X, click.Result["window"]!["origin"]!["x"]!.GetValue<int>());

        var hover = await Run(InputTools.Hover(ctx, x: x, y: y, window: refId));
        Assert.True(hover.Ok, hover.Error);
        Assert.Equal(rect.X + x, hover.Result["x"]!.GetValue<int>());

        var scroll = await Run(InputTools.Scroll(ctx, x: x, y: y, amount: 1, window: refId));
        Assert.True(scroll.Ok, scroll.Error);
        Assert.Equal(rect.Y + y, scroll.Result["y"]!.GetValue<int>());

        var plain = await Run(InputTools.Hover(ctx, x: rect.X + x, y: rect.Y + y));
        Assert.True(plain.Ok, plain.Error);
        Assert.Null(plain.Result["window"]);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Screenshot_ScreenReportsMonitors_AndWindowReportsMethod()
    {
        var screen = await Run(ObserveTools.Screenshot(ctx, target: "screen", scale: 0.25));
        Assert.True(screen.Ok, screen.Error);
        Assert.Equal("screen", screen.Result["method"]!.GetValue<string>());
        var monitors = screen.Result["monitors"]!.AsArray();
        Assert.NotEmpty(monitors);
        Assert.True(monitors[0]!["primary"]!.GetValue<bool>());
        Assert.Equal((0, 0), (screen.Result["origin"]!["x"]!.GetValue<int>(), screen.Result["origin"]!["y"]!.GetValue<int>()));

        var all = await Run(ObserveTools.Screenshot(ctx, target: "virtual", scale: 0.25));
        Assert.True(all.Ok, all.Error);
        Assert.NotNull(all.Result["monitors"]);

        using var np = await NotepadFixture.LaunchAsync(ctx.Windows);
        var refId = ctx.RegisterWindow(np.Window).Id;
        Assert.True(await ctx.Windows.ActivateAsync(np.Window.Hwnd));
        var win = await Run(ObserveTools.Screenshot(ctx, window: refId, scale: 0.25, settle_ms: 50));
        Assert.True(win.Ok, win.Error);
        Assert.Equal("screen", win.Result["method"]!.GetValue<string>());
        Assert.Null(win.Result["monitors"]);
        var print = await Run(ObserveTools.Screenshot(ctx, window: refId, scale: 0.25, method: "print"));
        Assert.True(print.Ok, print.Error);
        Assert.Equal("print", print.Result["method"]!.GetValue<string>());
        Assert.False((await Run(ObserveTools.Screenshot(ctx, window: refId, method: "bogus"))).Ok);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task GetUiTree_FiltersCompactsAndTruncates()
    {
        using var np = await NotepadFixture.LaunchAsync(ctx.Windows);
        var refId = ctx.RegisterWindow(np.Window).Id;

        var buttons = await Run(ObserveTools.GetUiTree(ctx, window: refId, control_type: "button", compact: true));
        Assert.True(buttons.Ok, buttons.Error);
        var tree = buttons.Result["tree"]!.AsObject();
        Assert.Equal("Window", tree["controlType"]!.GetValue<string>());
        Assert.Null(tree["depth"]);
        var kids = tree["children"]!.AsArray();
        Assert.NotEmpty(kids);
        Assert.All(kids, k => Assert.Equal("Button", k!["controlType"]!.GetValue<string>()));
        Assert.All(kids, k => Assert.Matches(@"^-?\d+,-?\d+,\d+,\d+$", k!["rect"]!.GetValue<string>()));
        Assert.Null(buttons.Result["omitted_nodes"]);

        var two = await Run(ObserveTools.GetUiTree(ctx, window: refId, max_nodes: 2));
        Assert.True(two.Ok, two.Error);
        Assert.Single(two.Result["tree"]!["children"]!.AsArray());
        Assert.True(two.Result["omitted_nodes"]!.GetValue<int>() > 0);

        var none = await Run(ObserveTools.GetUiTree(ctx, window: refId, name: "^zz-no-such-name-zz$"));
        Assert.True(none.Ok, none.Error);
        Assert.Empty(none.Result["tree"]!["children"]!.AsArray());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Clipboard_SetGetAndPasteText_IntoAnEditControl()
    {
        using var win = new StubbornWindowFixture(withEdit: true);
        var refId = ctx.RegisterWindow((await ctx.Windows.ListAsync()).Single(w => w.Hwnd == win.Hwnd)).Id;
        Assert.True((await Run(ClipboardTools.ClipboardSet(ctx, "before"))).Ok);

        var paste = await Run(ClipboardTools.PasteText(ctx, "pasted ✓", window: refId));
        Assert.True(paste.Ok, paste.Error);
        Assert.True(paste.Result["restored"]!.GetValue<bool>());
        Assert.Equal(refId, paste.Result["window"]!["ref"]!.GetValue<string>());

        var get = await Run(ClipboardTools.ClipboardGet(ctx));
        Assert.True(get.Ok, get.Error);
        Assert.Equal("before", get.Result["text"]!.GetValue<string>());
        Assert.False(get.Result["has_image"]!.GetValue<bool>());

        Assert.Equal("pasted ✓", win.EditText);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ClipboardSetImage_RequiresAnAllowedPath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"winuse-clip-{Guid.NewGuid():N}.png");
        using (var bmp = new System.Drawing.Bitmap(3, 3))
        {
            bmp.SetPixel(1, 1, System.Drawing.Color.Green);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        try
        {
            var o = await Run(ClipboardTools.ClipboardSetImage(ctx, file));
            Assert.True(o.Ok, o.Error);
            Assert.True((await Run(ClipboardTools.ClipboardGet(ctx))).Result["has_image"]!.GetValue<bool>());
            var outside = await Run(ClipboardTools.ClipboardSetImage(ctx, @"C:\Windows\explorer.exe"));
            Assert.False(outside.Ok);
            Assert.Contains("outside allowed roots", outside.Error);
        }
        finally
        {
            await ctx.Clipboard.SetTextAsync("");
            File.Delete(file);
        }
    }
}

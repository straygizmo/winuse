using System.Text.Json;
using WinUse.Core.Config;
using WinUse.Core.Grounding;
using WinUse.Core.Jev;
using WinUse.Core.Tests.Grounding.Visual;
using static WinUse.Core.Tests.Grounding.G;

namespace WinUse.Core.Tests.Grounding;

/// <summary>act / find / check on a window that exposes no UIA / DOM candidates (VirtualBox, emulators, VNC).</summary>
public class VisualGroundingTests
{
    static Grounder Visual(FakeJev jev, FakeExecutor exec, WinUseConfig? cfg = null, FakeCapture? capture = null, params Candidate[] cands) =>
        new(jev, new FakeObservation(cands), exec, cfg ?? TestConfig.WithJev("k"), capture ?? new FakeCapture());

    [Fact]
    public async Task Act_NoCandidates_Grid_ClicksTheLocatedPoint_AndVerifiesWithAScreenshot()
    {
        var jev = new FakeJev(cell: "A1", noul: 0.9);
        var exec = new FakeExecutor();
        // 1600x900 needs all three grid stages before a cell is 24 px or smaller.
        var r = await Visual(jev, exec, capture: new FakeCapture(1600, 900)).ActAsync("tap OK", null, ActKind.Click, null, null);

        Assert.True(r.Executed);
        Assert.NotNull(exec.LastPoint);
        Assert.InRange(exec.LastPoint!.Value.X, 0, 40); Assert.InRange(exec.LastPoint.Value.Y, 0, 40);
        Assert.Equal(exec.LastPoint, r.Point);
        Assert.Equal(ActKind.Click, exec.LastKind);
        Assert.True(r.Verified);
        Assert.Equal("point", r.Target!.Role);
        Assert.Equal("top-left", r.Target.Region);

        // 3 grid stages, then the satisfied question; every request carried the screenshot.
        Assert.Equal(4, jev.Calls.Count);
        var first = Assert.IsType<ChoiceQuestion>(jev.Calls[0]["cell"]);
        Assert.Equal(49, first.Criteria.Count);
        Assert.All(jev.Images, imgs => Assert.Single(imgs!));
        Assert.Contains("\"screenshot\":\"screenshot\"", JsonSerializer.Serialize(jev.LastState));
        Assert.IsType<NoulQuestion>(jev.Calls[3]["satisfied"]);
    }

    [Fact]
    public async Task Act_NoCandidates_Box_OnJev_UsesTheBoxCentre()
    {
        var jev = new FakeJev(box: new BoxAnswer(0.4, 0.6, 0.4, 0.6, 0.9), noul: 0.9);
        var exec = new FakeExecutor();
        var cfg = TestConfig.WithJev("k") with { VisualLocator = VisualLocatorKind.Box };
        var r = await Visual(jev, exec, cfg).ActAsync("tap OK", null, ActKind.Click, null, null);

        Assert.True(r.Executed);
        Assert.Equal((400, 300), exec.LastPoint);
        Assert.Equal(2, jev.Calls.Count);
        var q = Assert.IsType<BoxQuestion>(jev.Calls[0]["target"]);
        Assert.Contains("`screenshot`", q.Instructions);
    }

    [Fact]
    public async Task Act_NoCandidates_Box_OnClef_FallsBackToGrid()
    {
        var jev = new FakeJev(cell: "A1", provider: DecisionProvider.Clef);
        var cfg = TestConfig.WithClef() with { VisualLocator = VisualLocatorKind.Box };
        var r = await Visual(jev, new FakeExecutor(), cfg).ActAsync("tap OK", null, ActKind.Click, null, null);
        Assert.True(r.Executed);
        Assert.IsType<ChoiceQuestion>(jev.Calls[0]["cell"]);
        Assert.DoesNotContain(jev.Calls, c => c.Values.Any(q => q is BoxQuestion));
    }

    [Fact]
    public async Task Act_NoCandidates_ScreenshotsOff_FailsWithoutAskingTheModel()
    {
        var jev = new FakeJev(cell: "A1");
        var cfg = TestConfig.WithJev("k") with { Screenshots = ScreenshotPolicy.Off };
        var r = await Visual(jev, new FakeExecutor(), cfg).ActAsync("tap OK", null, ActKind.Click, null, null);
        Assert.False(r.Executed);
        Assert.Equal("no elements (screenshots off)", r.Reason);
        Assert.Empty(jev.Calls);
    }

    [Fact]
    public async Task Act_NoCandidates_NoCaptureSource_Fails()
    {
        var g = new Grounder(new FakeJev(cell: "A1"), new FakeObservation(), new FakeExecutor(), TestConfig.WithJev("k"));
        var r = await g.ActAsync("tap OK", null, ActKind.Click, null, null);
        Assert.False(r.Executed);
        Assert.Equal("no elements", r.Reason);
    }

    [Fact]
    public async Task Act_NoCandidates_None_IsLowConfidence()
    {
        var exec = new FakeExecutor();
        var r = await Visual(new FakeJev(cell: "none"), exec).ActAsync("tap OK", null, ActKind.Click, null, null);
        Assert.False(r.Executed); Assert.Equal("low confidence", r.Reason); Assert.Null(exec.LastPoint);
    }

    [Fact]
    public async Task Act_NoCandidates_MinConfidenceOverride_Applies()
    {
        var exec = new FakeExecutor();
        var r = await Visual(new FakeJev(cell: "A1"), exec).ActAsync("tap OK", null, ActKind.Click, null, 0.95);
        Assert.False(r.Executed); Assert.Equal("low confidence", r.Reason); Assert.Null(exec.LastPoint);
    }

    [Fact]
    public async Task Act_NoCandidates_AutoKind_TypesWhenTextIsGiven()
    {
        var exec = new FakeExecutor();
        var r = await Visual(new FakeJev(cell: "A1"), exec).ActAsync("enter the name", null, ActKind.Auto, "hello", null);
        Assert.True(r.Executed);
        Assert.Equal(ActKind.Type, exec.LastKind); Assert.Equal("hello", exec.LastText);
    }

    [Fact]
    public async Task Act_NoCandidates_ModelUnavailable_IsReported()
    {
        var r = await Visual(new FakeJev(throwUnavailable: true), new FakeExecutor()).ActAsync("tap OK", null, ActKind.Click, null, null);
        Assert.False(r.Executed); Assert.Equal("model unavailable", r.Reason);
    }

    [Fact]
    public async Task Find_NoCandidates_ReturnsThePoint()
    {
        var r = await Visual(new FakeJev(cell: "A1"), new FakeExecutor()).FindAsync("the OK button", null);
        Assert.True(r.Grounded);
        Assert.NotNull(r.Point);
        var (c, p) = Assert.Single(r.Ranked);
        Assert.Equal("point", c.Role); Assert.Equal(0.9, p);
        Assert.Equal($"{r.Point!.Value.X},{r.Point.Value.Y}", c.Label);
    }

    [Fact]
    public async Task Find_NoCandidates_ScreenshotsOff_IsUngrounded()
    {
        var cfg = TestConfig.WithJev("k") with { Screenshots = ScreenshotPolicy.Off };
        var r = await Visual(new FakeJev(cell: "A1"), new FakeExecutor(), cfg).FindAsync("x", null);
        Assert.False(r.Grounded); Assert.Empty(r.Ranked); Assert.Null(r.Point);
    }

    [Fact]
    public async Task Check_NoCandidates_AttachesTheScreenshot()
    {
        var jev = new FakeJev(noul: 0.8);
        var r = await Visual(jev, new FakeExecutor()).CheckAsync("is the home screen shown?", null);
        Assert.True(r.Available); Assert.Equal(0.8, r.Probability);
        Assert.Single(Assert.Single(jev.Images)!);
        Assert.Contains("\"screenshot\":\"screenshot\"", JsonSerializer.Serialize(jev.LastState));
    }

    [Fact]
    public async Task Check_WithCandidates_Auto_SendsNoScreenshot()
    {
        var jev = new FakeJev(noul: 0.8);
        await Visual(jev, new FakeExecutor(), cands: Cand("u1")).CheckAsync("done?", null);
        Assert.Null(Assert.Single(jev.Images));
        Assert.DoesNotContain("screenshot", JsonSerializer.Serialize(jev.LastState));
    }

    [Fact]
    public async Task Check_WithCandidates_Always_SendsTheScreenshot()
    {
        var jev = new FakeJev(noul: 0.8);
        var cfg = TestConfig.WithJev("k") with { Screenshots = ScreenshotPolicy.Always };
        await Visual(jev, new FakeExecutor(), cfg, cands: Cand("u1")).CheckAsync("done?", null);
        Assert.Single(Assert.Single(jev.Images)!);
    }

    [Fact]
    public async Task Act_WithCandidates_Always_SendsTheScreenshotWithTheTargetQuestion()
    {
        var jev = new FakeJev(choice: ("u1", 0.9, 0.9), noul: 0.9);
        var cfg = TestConfig.WithJev("k") with { Screenshots = ScreenshotPolicy.Always };
        var exec = new FakeExecutor();
        var r = await Visual(jev, exec, cfg, cands: Cand("u1", "OK")).ActAsync("click OK", null, ActKind.Click, null, null);
        Assert.True(r.Executed); Assert.Equal("u1", exec.Last!.Id); Assert.Null(exec.LastPoint);
        Assert.All(jev.Images, imgs => Assert.Single(imgs!));
    }
}

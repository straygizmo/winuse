using System.Drawing;
using System.Drawing.Imaging;
using WinUse.Core.Grounding.Visual;
using WinUse.Core.Jev;

namespace WinUse.Core.Tests.Grounding.Visual;

public class GridLocatorTests
{
    internal static VisualFrame Blank(int w, int h, int originX = 0, int originY = 0, double scale = 1.0, Color? fill = null)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(fill ?? Color.White);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new VisualFrame(ms.ToArray(), w, h, originX, originY, scale);
    }

    static Color PixelAt(VisualFrame f, int x, int y)
    {
        using var bmp = new Bitmap(new MemoryStream(f.Png));
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void ToScreen_AppliesOriginAndScale()
    {
        var f = new VisualFrame([], 100, 100, 100, 50, 0.5);
        Assert.Equal((140, 70), f.ToScreen(20, 10));
    }

    [Fact]
    public void Fit_ShrinksLargeFrames_ToMaxSide()
    {
        var f = GridLocator.Fit(Blank(2560, 1440, 10, 20));
        Assert.Equal(1280, f.Width); Assert.Equal(720, f.Height);
        Assert.Equal(0.5, f.Scale, 3);
        Assert.Equal((10, 20), f.ToScreen(0, 0));
        Assert.Equal((2570, 1460), f.ToScreen(1280, 720));
    }

    [Fact]
    public void Fit_EnlargesSmallFrames_ToMinSide()
    {
        var f = GridLocator.Fit(Blank(200, 100));
        Assert.Equal(280, f.Height);
        Assert.Equal(560, f.Width);
        Assert.Equal(2.8, f.Scale, 3);
    }

    [Fact]
    public void Fit_LeavesMediumFramesAlone()
    {
        var src = Blank(800, 600);
        Assert.Same(src, GridLocator.Fit(src));
    }

    [Fact]
    public void CellIds_RoundTrip()
    {
        var g = new GridSpec(8, 6);
        Assert.Equal("A1", GridLocator.CellId(0, 0));
        Assert.Equal("H6", GridLocator.CellId(7, 5));
        Assert.True(GridLocator.TryParseCell("C4", g, out var col, out var row));
        Assert.Equal((2, 3), (col, row));
        Assert.False(GridLocator.TryParseCell("Z9", g, out _, out _));
        Assert.False(GridLocator.TryParseCell("none", g, out _, out _));
    }

    [Fact]
    public void Criteria_ListsEveryCellAndNone()
    {
        var c = GridLocator.Criteria(new GridSpec(8, 6));
        Assert.Equal(49, c.Count);
        Assert.Contains("A1", c.Keys); Assert.Contains("H6", c.Keys); Assert.Contains("none", c.Keys);
        Assert.Contains("leftmost", c["A1"]); Assert.Contains("top", c["A1"]);
        Assert.Contains("rightmost", c["H6"]); Assert.Contains("bottom", c["H6"]);
    }

    [Fact]
    public void CellRect_DividesTheFrameEvenly()
    {
        var r = GridLocator.CellRect(new GridSpec(8, 6), 800, 600, 2, 3);
        Assert.Equal((200, 300, 100, 100), (r.X, r.Y, r.Width, r.Height));
    }

    [Fact]
    public void DrawGrid_KeepsGeometry_AndDrawsLines()
    {
        var src = Blank(800, 600, 5, 7, 1.0);
        var drawn = GridLocator.DrawGrid(src, new GridSpec(8, 6));
        Assert.Equal((800, 600, 5, 7, 1.0), (drawn.Width, drawn.Height, drawn.OriginX, drawn.OriginY, drawn.Scale));
        Assert.NotEqual(Color.White.ToArgb(), PixelAt(drawn, 100, 50).ToArgb()); // vertical line between A and B
        Assert.Equal(Color.White.ToArgb(), PixelAt(drawn, 150, 50).ToArgb());    // middle of a cell stays untouched
    }

    [Fact]
    public void Crop_ExpandsTheCell_AndMapsBackToScreen()
    {
        var src = Blank(800, 600, 1000, 2000, 1.0);
        var crop = GridLocator.Crop(src, new GridSpec(8, 6), 2, 3);
        // cell = (200,300,100,100); 25 % margin each side -> (175,275,150,150), resized to a 640 px square.
        Assert.Equal((640, 640), (crop.Width, crop.Height));
        Assert.Equal((1250, 2350), crop.ToScreen(320, 320)); // the cell centre
        Assert.Equal((1175, 2275), crop.ToScreen(0, 0));
    }

    [Fact]
    public void Crop_ClampsAtTheFrameEdge()
    {
        var src = Blank(800, 600);
        var crop = GridLocator.Crop(src, new GridSpec(8, 6), 0, 0);
        Assert.Equal((0, 0), crop.ToScreen(0, 0));
    }

    [Fact]
    public void CellScreenSize_IsTheSmallerSideInScreenPixels()
    {
        var f = new VisualFrame([], 640, 640, 0, 0, 4.0); // 160 screen px wide
        Assert.Equal(160.0 / 6, GridLocator.CellScreenSize(f, new GridSpec(6, 4)), 3); // 26.7
    }

    [Fact]
    public async Task LocateAsync_RefinesUntilTheCellIsSmall_ThenReturnsTheCentre()
    {
        var full = Blank(1600, 900);
        var seen = new List<(GridSpec Grid, int W, int H)>();
        var r = await GridLocator.LocateAsync(full, (frame, grid) =>
        {
            seen.Add((grid, frame.Width, frame.Height));
            return Task.FromResult(new ChoiceAnswer("A1", new Dictionary<string, double> { ["A1"] = 0.9 }, 0.8));
        }, minProbability: 0.55, minConfidence: 0.35);

        Assert.NotNull(r);
        Assert.Equal(3, seen.Count);
        Assert.Equal(new GridSpec(8, 6), seen[0].Grid);
        Assert.Equal(new GridSpec(6, 4), seen[1].Grid);
        Assert.Equal((1280, 720), (seen[0].W, seen[0].H)); // fitted to MaxSide
        // Always A1: the final cell sits in the top-left corner of the window.
        Assert.InRange(r.Value.X, 0, 40); Assert.InRange(r.Value.Y, 0, 40);
        Assert.Equal(0.9, r.Value.Probability); Assert.Equal(0.8, r.Value.Confidence);
    }

    [Fact]
    public async Task LocateAsync_None_ReturnsNull()
    {
        var calls = 0;
        var r = await GridLocator.LocateAsync(Blank(800, 600), (_, _) =>
        {
            calls++;
            return Task.FromResult(new ChoiceAnswer("none", new Dictionary<string, double> { ["none"] = 0.7 }, 0.9));
        }, 0.55, 0.35);
        Assert.Null(r);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LocateAsync_LowProbability_ReturnsNull()
    {
        var r = await GridLocator.LocateAsync(Blank(800, 600), (_, _) =>
            Task.FromResult(new ChoiceAnswer("B2", new Dictionary<string, double> { ["B2"] = 0.3 }, 0.9)), 0.55, 0.35);
        Assert.Null(r);
    }

    [Fact]
    public void BoxCentre_MapsFractionsToScreen()
    {
        var f = new VisualFrame([], 1000, 500, 100, 200, 0.5);
        var p = GridLocator.BoxCentre(f, new BoxAnswer(0.2, 0.4, 0.1, 0.3, 0.9));
        // centre = (0.2 * 1000, 0.3 * 500) = (200, 150) px -> screen (100 + 400, 200 + 300)
        Assert.Equal((500, 500), p);
    }
}

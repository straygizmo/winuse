using WinUse.Core.Java;
using WinUse.Core.Windows;

namespace WinUse.Core.Tests.Java;

public class JavaGeometryTests
{
    [Fact]
    public void Calibrate_VirtualizedCoordinates_ScalesAndOffsets()
    {
        // DPI-unaware Java 8 at 150%: Java sees the frame at (200,100) 640x480, Windows reports it at (300,150) 960x720.
        var tx = JavaGeometry.Calibrate(new Rect(300, 150, 960, 720), (200, 100, 640, 480), 1.5);
        Assert.Equal(1.5, tx.Scale, 3);
        Assert.Equal(new Rect(315, 165, 150, 30), tx.Apply(210, 110, 100, 20));
    }

    [Fact]
    public void Calibrate_SameRects_IsIdentity()
    {
        var tx = JavaGeometry.Calibrate(new Rect(100, 100, 640, 480), (100, 100, 640, 480), 1.0);
        Assert.Equal(new Rect(110, 120, 50, 20), tx.Apply(110, 120, 50, 20));
    }

    [Fact]
    public void Calibrate_WithoutRoot_FallsBackToDpiScale()
    {
        var tx = JavaGeometry.Calibrate(new Rect(0, 0, 100, 100), null, 1.25);
        Assert.Equal(1.25, tx.Scale, 3);
        Assert.Equal(0, tx.OffsetX, 3);
        Assert.Equal(0, tx.OffsetY, 3);
    }

    [Fact]
    public void Calibrate_ImplausibleScale_FallsBackToDpiScale()
    {
        var tx = JavaGeometry.Calibrate(new Rect(0, 0, 640, 480), (0, 0, 1, 480), 1.0);
        Assert.Equal(1.0, tx.Scale, 3);
        Assert.Equal(0, tx.OffsetX, 3);
    }

    [Fact]
    public void Calibrate_ZeroWidthRoot_UsesHeight()
    {
        var tx = JavaGeometry.Calibrate(new Rect(0, 0, 0, 720), (0, 0, 0, 480), 1.0);
        Assert.Equal(1.5, tx.Scale, 3);
    }
}

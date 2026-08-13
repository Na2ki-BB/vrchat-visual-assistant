using System.Windows;

namespace VrcVa.Windows.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void Fit_ShrinksTallWindowInsideWorkArea()
    {
        Rect fitted = WindowPlacement.Fit(
            new Rect(0, 0, 1366, 728),
            desiredWidth: 780,
            desiredHeight: 900,
            margin: 16);

        Assert.Equal(new Rect(293, 16, 780, 696), fitted);
    }

    [Fact]
    public void Fit_HandlesNegativeMonitorCoordinates()
    {
        Rect workArea = new(-1920, -120, 1920, 1040);

        Rect fitted = WindowPlacement.Fit(workArea, 780, 900, 16);

        Assert.True(fitted.Left >= workArea.Left);
        Assert.True(fitted.Top >= workArea.Top);
        Assert.True(fitted.Right <= workArea.Right);
        Assert.True(fitted.Bottom <= workArea.Bottom);
    }
}

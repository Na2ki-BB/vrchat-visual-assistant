using System.Windows;

namespace VrcVa.Windows;

internal static class WindowPlacement
{
    internal static Rect Fit(
        Rect workArea,
        double desiredWidth,
        double desiredHeight,
        double margin)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        double horizontalMargin = Math.Min(margin, Math.Max(0, (workArea.Width - 1) / 2));
        double verticalMargin = Math.Min(margin, Math.Max(0, (workArea.Height - 1) / 2));
        double width = Math.Min(desiredWidth, workArea.Width - (horizontalMargin * 2));
        double height = Math.Min(desiredHeight, workArea.Height - (verticalMargin * 2));
        return new Rect(
            workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2),
            width,
            height);
    }
}

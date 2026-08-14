using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace VrcVa.Windows.OpenVr;

internal enum WristLauncherAction
{
    None,
    Expand,
    Translate,
    Calibrate,
    CloseMenu,
}

internal sealed class WristLauncherTexture
{
    public const int PixelWidth = 800;
    public const int PixelHeight = 400;
    public const int AtlasColumns = 4;
    public const int AtlasRows = 2;
    public const int AtlasPixelWidth = PixelWidth * AtlasColumns;
    public const int AtlasPixelHeight = PixelHeight * AtlasRows;

    internal static readonly Rect ChipBounds = new(240, 130, 320, 140);
    internal static readonly Rect TranslationButtonBounds = new(72, 120, 315, 208);
    internal static readonly Rect CalibrationButtonBounds = new(413, 120, 315, 208);
    internal static readonly Rect CloseMenuButtonBounds = new(686, 24, 78, 68);

    private readonly Typeface _typeface = new("Yu Gothic UI");

    public WristLauncherAction HitTest(WristLauncherView view, float x, float y)
    {
        Point point = new(x, y);
        return view switch
        {
            WristLauncherView.DimChip when ChipBounds.Contains(point) =>
                WristLauncherAction.Expand,
            WristLauncherView.ArmedChip when ChipBounds.Contains(point) =>
                WristLauncherAction.Expand,
            WristLauncherView.Menu when TranslationButtonBounds.Contains(point) =>
                WristLauncherAction.Translate,
            WristLauncherView.Menu when CalibrationButtonBounds.Contains(point) =>
                WristLauncherAction.Calibrate,
            WristLauncherView.Menu when CloseMenuButtonBounds.Contains(point) =>
                WristLauncherAction.CloseMenu,
            _ => WristLauncherAction.None,
        };
    }

    public int GetAtlasCell(WristLauncherView view, WristLauncherAction hoveredAction) =>
        (view, hoveredAction) switch
        {
            (WristLauncherView.DimChip, WristLauncherAction.Expand) => 2,
            (WristLauncherView.DimChip, _) => 0,
            (WristLauncherView.ArmedChip, WristLauncherAction.Expand) => 2,
            (WristLauncherView.ArmedChip, _) => 1,
            (WristLauncherView.Menu, WristLauncherAction.Translate) => 4,
            (WristLauncherView.Menu, WristLauncherAction.Calibrate) => 5,
            (WristLauncherView.Menu, WristLauncherAction.CloseMenu) => 6,
            (WristLauncherView.Menu, _) => 3,
            _ => 0,
        };

    public byte[] RenderAtlasRgba()
    {
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            for (int cell = 0; cell < AtlasColumns * AtlasRows; cell++)
            {
                drawing.PushTransform(new TranslateTransform(
                    (cell % AtlasColumns) * PixelWidth,
                    (cell / AtlasColumns) * PixelHeight));
                switch (cell)
                {
                    case 0:
                        DrawChip(drawing, armed: false, WristLauncherAction.None);
                        break;
                    case 1:
                        DrawChip(drawing, armed: true, WristLauncherAction.None);
                        break;
                    case 2:
                        DrawChip(drawing, armed: true, WristLauncherAction.Expand);
                        break;
                    case 3:
                        DrawMenu(drawing, WristLauncherAction.None);
                        break;
                    case 4:
                        DrawMenu(drawing, WristLauncherAction.Translate);
                        break;
                    case 5:
                        DrawMenu(drawing, WristLauncherAction.Calibrate);
                        break;
                    case 6:
                        DrawMenu(drawing, WristLauncherAction.CloseMenu);
                        break;
                    case 7:
                        DrawMenu(drawing, WristLauncherAction.None);
                        break;
                }

                drawing.Pop();
            }
        }

        RenderTargetBitmap bitmap = new(
            AtlasPixelWidth,
            AtlasPixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] bgra = new byte[AtlasPixelWidth * AtlasPixelHeight * 4];
        bitmap.CopyPixels(bgra, AtlasPixelWidth * 4, 0);
        byte[] rgba = new byte[bgra.Length];
        for (int index = 0; index < bgra.Length; index += 4)
        {
            rgba[index] = bgra[index + 2];
            rgba[index + 1] = bgra[index + 1];
            rgba[index + 2] = bgra[index];
            rgba[index + 3] = bgra[index + 3];
        }

        return rgba;
    }

    private void DrawChip(
        DrawingContext drawing,
        bool armed,
        WristLauncherAction hoveredAction)
    {
        Color fill = armed
            ? Color.FromRgb(24, 112, 161)
            : Color.FromRgb(48, 57, 70);
        if (hoveredAction == WristLauncherAction.Expand)
        {
            fill = Color.FromRgb(40, 152, 211);
        }

        DrawButton(drawing, ChipBounds, fill, "VRCVA", 42);
        FormattedText hint = CreateText(
            armed ? "右トリガーで開く" : "手首を顔へ向ける",
            22,
            FontWeights.SemiBold,
            armed ? Brushes.White : new SolidColorBrush(Color.FromRgb(164, 177, 194)),
            ChipBounds.Width);
        hint.TextAlignment = TextAlignment.Center;
        drawing.DrawText(hint, new Point(ChipBounds.Left, ChipBounds.Bottom + 12));
    }

    private void DrawMenu(DrawingContext drawing, WristLauncherAction hoveredAction)
    {
        Rect panel = new(30, 16, 740, 368);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(245, 18, 23, 32)),
            new Pen(new SolidColorBrush(Color.FromRgb(91, 119, 153)), 3),
            panel,
            24,
            24);
        FormattedText title = CreateText(
            "VRCVA",
            31,
            FontWeights.SemiBold,
            Brushes.White,
            560);
        drawing.DrawText(title, new Point(67, 39));

        Color translateFill = hoveredAction == WristLauncherAction.Translate
            ? Color.FromRgb(37, 151, 209)
            : Color.FromRgb(24, 112, 161);
        DrawButton(drawing, TranslationButtonBounds, translateFill, "翻訳 SCAN", 46);

        Color calibrationFill = hoveredAction == WristLauncherAction.Calibrate
            ? Color.FromRgb(61, 139, 177)
            : Color.FromRgb(45, 88, 119);
        DrawButton(drawing, CalibrationButtonBounds, calibrationFill, "位置調整", 42);

        Color closeFill = hoveredAction == WristLauncherAction.CloseMenu
            ? Color.FromRgb(124, 83, 91)
            : Color.FromRgb(79, 91, 109);
        DrawButton(drawing, CloseMenuButtonBounds, closeFill, "×", 38);
    }

    private void DrawButton(
        DrawingContext drawing,
        Rect bounds,
        Color fill,
        string label,
        double fontSize)
    {
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(fill),
            new Pen(new SolidColorBrush(Color.FromRgb(124, 161, 203)), 3),
            bounds,
            22,
            22);
        FormattedText text = CreateText(
            label,
            fontSize,
            FontWeights.SemiBold,
            Brushes.White,
            bounds.Width);
        text.TextAlignment = TextAlignment.Center;
        drawing.DrawText(
            text,
            new Point(bounds.Left, bounds.Top + ((bounds.Height - text.Height) / 2) - 2));
    }

    private FormattedText CreateText(
        string text,
        double size,
        FontWeight weight,
        Brush brush,
        double width)
    {
        FormattedText formatted = new(
            text,
            CultureInfo.GetCultureInfo("ja-JP"),
            FlowDirection.LeftToRight,
            _typeface,
            size,
            brush,
            1)
        {
            MaxTextWidth = width,
        };
        formatted.SetFontWeight(weight);
        return formatted;
    }
}

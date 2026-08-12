using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace VrcVa.Windows.OpenVr;

internal sealed class ResultPanelTexture
{
    public const int PixelWidth = 1280;
    public const int PixelHeight = 720;
    private const double BodyTop = 116;
    private const double BodyBottom = 668;
    private const double BodyLeft = 54;
    private const double BodyRight = 1214;
    private const double ScrollStep = 150;

    private readonly Typeface _bodyTypeface = new("Yu Gothic UI");
    private string _title = string.Empty;
    private string _body = string.Empty;
    private double _scrollOffset;
    private double _maximumScrollOffset;

    public void SetContent(string title, string body)
    {
        _title = title;
        _body = body;
        _scrollOffset = 0;
    }

    public bool Scroll(float delta)
    {
        if (Math.Abs(delta) < float.Epsilon)
        {
            return false;
        }

        double previous = _scrollOffset;
        _scrollOffset = Math.Clamp(
            _scrollOffset - (delta * ScrollStep),
            0,
            _maximumScrollOffset);
        return Math.Abs(previous - _scrollOffset) > 0.1;
    }

    public bool IsCloseButton(float x, float y)
    {
        bool horizontalMatch = x is >= 1150 and <= 1260;
        bool topOriginMatch = y is >= 22 and <= 102;
        bool bottomOriginMatch = y is >= 618 and <= 698;
        return horizontalMatch && (topOriginMatch || bottomOriginMatch);
    }

    public byte[] RenderRgba()
    {
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(18, 23, 32)),
                null,
                new Rect(0, 0, PixelWidth, PixelHeight));
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(35, 45, 60)),
                null,
                new Rect(0, 0, PixelWidth, 108));

            DrawHeader(drawing);
            DrawBody(drawing);
            DrawScrollIndicator(drawing);
        }

        RenderTargetBitmap bitmap = new(
            PixelWidth,
            PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        byte[] bgra = new byte[PixelWidth * PixelHeight * 4];
        bitmap.CopyPixels(bgra, PixelWidth * 4, 0);
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

    private void DrawHeader(DrawingContext drawing)
    {
        FormattedText title = CreateText(
            _title,
            34,
            FontWeights.SemiBold,
            Brushes.White,
            1030);
        drawing.DrawText(title, new Point(48, 31));

        Rect closeButton = new(1150, 22, 110, 80);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(79, 91, 109)),
            null,
            closeButton,
            14,
            14);
        FormattedText closeText = CreateText(
            "閉じる",
            25,
            FontWeights.SemiBold,
            Brushes.White,
            90);
        drawing.DrawText(closeText, new Point(1168, 43));
    }

    private void DrawBody(DrawingContext drawing)
    {
        double viewportHeight = BodyBottom - BodyTop;
        FormattedText body = CreateText(
            _body,
            30,
            FontWeights.Normal,
            Brushes.White,
            BodyRight - BodyLeft - 26);
        body.LineHeight = 43;
        _maximumScrollOffset = Math.Max(0, body.Height - viewportHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maximumScrollOffset);

        drawing.PushClip(new RectangleGeometry(
            new Rect(BodyLeft, BodyTop, BodyRight - BodyLeft, viewportHeight)));
        drawing.DrawText(body, new Point(BodyLeft, BodyTop - _scrollOffset));
        drawing.Pop();

        if (_maximumScrollOffset > 0)
        {
            FormattedText hint = CreateText(
                "上下にスクロールできます",
                20,
                FontWeights.Normal,
                new SolidColorBrush(Color.FromRgb(177, 191, 211)),
                400);
            drawing.DrawText(hint, new Point(48, 680));
        }
    }

    private void DrawScrollIndicator(DrawingContext drawing)
    {
        if (_maximumScrollOffset <= 0)
        {
            return;
        }

        double trackHeight = BodyBottom - BodyTop;
        double contentRatio = trackHeight / (trackHeight + _maximumScrollOffset);
        double thumbHeight = Math.Max(56, trackHeight * contentRatio);
        double travel = trackHeight - thumbHeight;
        double position = _maximumScrollOffset == 0
            ? 0
            : travel * (_scrollOffset / _maximumScrollOffset);

        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(55, 66, 82)),
            null,
            new Rect(1230, BodyTop, 14, trackHeight),
            7,
            7);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(115, 171, 255)),
            null,
            new Rect(1230, BodyTop + position, 14, thumbHeight),
            7,
            7);
    }

    private FormattedText CreateText(
        string value,
        double size,
        FontWeight weight,
        System.Windows.Media.Brush brush,
        double maximumWidth)
    {
        FormattedText text = new(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _bodyTypeface,
            size,
            brush,
            1);
        text.SetFontWeight(weight);
        text.MaxTextWidth = maximumWidth;
        return text;
    }
}

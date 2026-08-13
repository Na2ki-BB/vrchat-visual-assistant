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
    public const int AtlasColumns = 2;
    public const int AtlasRows = 3;
    public const int AtlasPixelWidth = PixelWidth * AtlasColumns;
    public const int AtlasPixelHeight = PixelHeight * AtlasRows;
    public const int WaitingCell = 0;
    public const int CapturingCell = 1;
    public const int ProcessingCell = 2;
    private const int FirstResultCell = 3;
    public const int CalibrationCell = FirstResultCell;
    private const int MaximumResultPages = 3;
    private const double BodyTop = 116;
    private const double BodyBottom = 668;
    private const double BodyLeft = 54;
    private const double BodyRight = 1000;
    private const double ScrollTrackLeft = 1218;
    private const double ScrollTrackRight = 1252;
    private const double ScrollHitLeft = 1198;
    private const double ScrollHitRight = 1268;
    private const double MinimumThumbHeight = 64;
    private const float CloseLeft = 1020;
    private const float CloseRight = 1188;
    private const float CloseTop = (float)BodyTop;
    private const float CloseBottom = (float)BodyBottom;

    private readonly Typeface _bodyTypeface = new("Yu Gothic UI");
    private string _title = string.Empty;
    private string _body = string.Empty;
    private int _resultPage;
    private int _resultPageCount = 1;
    private bool _resultTruncated;
    private bool _calibrationMode;

    public int CurrentResultCell => FirstResultCell + _resultPage;

    internal int CurrentResultPage => _resultPage;

    internal int ResultPageCount => _resultPageCount;

    internal bool ResultTruncated => _resultTruncated;

    public void SetContent(string title, string body)
    {
        _calibrationMode = false;
        _title = title;
        _body = body;
        _resultPage = 0;
    }

    public void SetCalibration()
    {
        _calibrationMode = true;
        _resultPage = 0;
        _resultPageCount = 1;
        _resultTruncated = false;
    }

    public bool Scroll(float delta)
    {
        if (Math.Abs(delta) < float.Epsilon || _resultPageCount <= 1)
        {
            return false;
        }

        int previous = _resultPage;
        _resultPage = Math.Clamp(
            _resultPage - Math.Sign(delta),
            0,
            _resultPageCount - 1);
        return previous != _resultPage;
    }

    public bool IsCloseButton(float x, float y) =>
        x is >= CloseLeft and <= CloseRight;

    internal static (float X, float Y) MapOpenVrPointer(
        float openVrX,
        float openVrY,
        int atlasCell)
    {
        if (atlasCell < 0 || atlasCell >= AtlasColumns * AtlasRows)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasCell));
        }

        int column = atlasCell % AtlasColumns;
        int row = atlasCell / AtlasColumns;
        float localX = (openVrX * AtlasColumns) - (column * PixelWidth);
        float localY = ((PixelHeight - openVrY) * AtlasRows) - (row * PixelHeight);
        return (localX, localY);
    }

    public bool BeginScrollbarInteraction(float x, float y)
    {
        if (_resultPageCount <= 1
            || x < ScrollHitLeft
            || x > ScrollHitRight
            || y < BodyTop
            || y > BodyBottom)
        {
            return false;
        }

        (_, double thumbHeight, double travel) = GetScrollbarMetrics();
        double thumbTop = Math.Clamp(
            y - (thumbHeight / 2),
            BodyTop,
            BodyBottom - thumbHeight);
        double ratio = travel <= 0 ? 0 : (thumbTop - BodyTop) / travel;
        int nextPage = Math.Clamp(
            (int)Math.Round(ratio * (_resultPageCount - 1)),
            0,
            _resultPageCount - 1);
        int previous = _resultPage;
        _resultPage = nextPage;
        return previous != _resultPage;
    }

    public static ResultPanelCalibrationAction HitTestCalibration(float x, float y)
    {
        foreach ((ResultPanelCalibrationAction action, Rect bounds, _) in CalibrationButtons)
        {
            if (bounds.Contains(x, y))
            {
                return action;
            }
        }

        return ResultPanelCalibrationAction.None;
    }

    public byte[] RenderRgba()
    {
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            DrawStatusCell(
                drawing,
                WaitingCell,
                "SCANを受け付けました",
                "Action Menuを閉じてください。\n1秒後に撮影します。");
            DrawStatusCell(
                drawing,
                CapturingCell,
                "撮影を開始します",
                "表示を消して画面を取得します…");
            DrawStatusCell(
                drawing,
                ProcessingCell,
                "文字を処理中…",
                "OCRを実行し、設定時は\n日本語へ翻訳しています。");

            if (_calibrationMode)
            {
                DrawCalibrationCell(drawing);
                DrawEmptyCell(drawing, FirstResultCell + 1);
                DrawEmptyCell(drawing, FirstResultCell + 2);
            }
            else
            {
                FormattedText body = CreateBodyText();
                double viewportHeight = BodyBottom - BodyTop;
                int requiredPages = Math.Max(1, (int)Math.Ceiling(body.Height / viewportHeight));
                _resultPageCount = Math.Min(MaximumResultPages, requiredPages);
                _resultTruncated = requiredPages > MaximumResultPages;
                _resultPage = Math.Clamp(_resultPage, 0, _resultPageCount - 1);
                for (int page = 0; page < MaximumResultPages; page++)
                {
                    DrawResultCell(drawing, body, page, viewportHeight);
                }
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

    private void DrawStatusCell(
        DrawingContext drawing,
        int cell,
        string title,
        string message)
    {
        drawing.PushTransform(GetCellTransform(cell));
        DrawBackground(drawing);
        FormattedText heading = CreateText(
            title,
            42,
            FontWeights.SemiBold,
            Brushes.White,
            1120);
        drawing.DrawText(heading, new Point(72, 225));
        FormattedText detail = CreateText(
            message,
            30,
            FontWeights.Normal,
            new SolidColorBrush(Color.FromRgb(205, 217, 234)),
            1120);
        detail.LineHeight = 48;
        drawing.DrawText(detail, new Point(72, 310));
        drawing.Pop();
    }

    private void DrawResultCell(
        DrawingContext drawing,
        FormattedText body,
        int page,
        double viewportHeight)
    {
        drawing.PushTransform(GetCellTransform(FirstResultCell + page));
        DrawBackground(drawing);
        DrawHeader(drawing);

        if (page < _resultPageCount)
        {
            drawing.PushClip(new RectangleGeometry(
                new Rect(BodyLeft, BodyTop, BodyRight - BodyLeft, viewportHeight)));
            drawing.DrawText(body, new Point(BodyLeft, BodyTop - (page * viewportHeight)));
            drawing.Pop();

            string pageLabel = $"{page + 1} / {_resultPageCount} ページ";
            if (_resultTruncated && page == _resultPageCount - 1)
            {
                pageLabel += "（続きはPC画面）";
            }

            FormattedText hint = CreateText(
                pageLabel,
                20,
                FontWeights.Normal,
                new SolidColorBrush(Color.FromRgb(177, 191, 211)),
                500);
            drawing.DrawText(hint, new Point(48, 680));
            DrawScrollIndicator(drawing, page);
        }

        drawing.Pop();
    }

    private void DrawCalibrationCell(DrawingContext drawing)
    {
        drawing.PushTransform(GetCellTransform(CalibrationCell));
        DrawBackground(drawing);
        FormattedText title = CreateText(
            "VR結果パネルの位置調整",
            34,
            FontWeights.SemiBold,
            Brushes.White,
            1180);
        drawing.DrawText(title, new Point(48, 25));
        FormattedText hint = CreateText(
            "レーザーで選択すると、この画面がその場で動きます",
            22,
            FontWeights.Normal,
            new SolidColorBrush(Color.FromRgb(190, 205, 225)),
            1180);
        drawing.DrawText(hint, new Point(48, 68));

        foreach ((ResultPanelCalibrationAction action, Rect bounds, string label) in CalibrationButtons)
        {
            Color color = action switch
            {
                ResultPanelCalibrationAction.Save => Color.FromRgb(22, 115, 154),
                ResultPanelCalibrationAction.Cancel => Color.FromRgb(92, 74, 80),
                ResultPanelCalibrationAction.Reset => Color.FromRgb(67, 79, 96),
                _ => Color.FromRgb(53, 72, 96),
            };
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(color),
                new System.Windows.Media.Pen(
                    new SolidColorBrush(Color.FromRgb(113, 143, 181)),
                    2),
                bounds,
                14,
                14);
            FormattedText text = CreateText(
                label,
                action is ResultPanelCalibrationAction.Save
                    or ResultPanelCalibrationAction.Cancel
                    or ResultPanelCalibrationAction.Reset
                    ? 27
                    : 31,
                FontWeights.SemiBold,
                Brushes.White,
                bounds.Width - 20);
            text.TextAlignment = TextAlignment.Center;
            drawing.DrawText(
                text,
                new Point(bounds.Left + 10, bounds.Top + ((bounds.Height - text.Height) / 2)));
        }

        drawing.Pop();
    }

    private static void DrawEmptyCell(DrawingContext drawing, int cell)
    {
        drawing.PushTransform(GetCellTransform(cell));
        DrawBackground(drawing);
        drawing.Pop();
    }

    private static readonly (ResultPanelCalibrationAction Action, Rect Bounds, string Label)[] CalibrationButtons =
    [
        (ResultPanelCalibrationAction.MoveLeft, new Rect(48, 140, 250, 125), "←  左へ"),
        (ResultPanelCalibrationAction.MoveRight, new Rect(359, 140, 250, 125), "右へ  →"),
        (ResultPanelCalibrationAction.MoveUp, new Rect(670, 140, 250, 125), "↑  上へ"),
        (ResultPanelCalibrationAction.MoveDown, new Rect(981, 140, 250, 125), "↓  下へ"),
        (ResultPanelCalibrationAction.MoveNear, new Rect(48, 300, 250, 125), "近く"),
        (ResultPanelCalibrationAction.MoveFar, new Rect(359, 300, 250, 125), "遠く"),
        (ResultPanelCalibrationAction.MakeSmaller, new Rect(670, 300, 250, 125), "小さく"),
        (ResultPanelCalibrationAction.MakeLarger, new Rect(981, 300, 250, 125), "大きく"),
        (ResultPanelCalibrationAction.Reset, new Rect(48, 500, 280, 120), "初期値"),
        (ResultPanelCalibrationAction.Cancel, new Rect(370, 500, 280, 120), "中止"),
        (ResultPanelCalibrationAction.Save, new Rect(692, 500, 539, 120), "保存して閉じる"),
    ];

    private static TranslateTransform GetCellTransform(int cell) => new(
        (cell % AtlasColumns) * PixelWidth,
        (cell / AtlasColumns) * PixelHeight);

    private static void DrawBackground(DrawingContext drawing)
    {
        drawing.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(18, 23, 32)),
            null,
            new Rect(0, 0, PixelWidth, PixelHeight));
        drawing.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(35, 45, 60)),
            null,
            new Rect(0, 0, PixelWidth, 108));
    }

    private void DrawHeader(DrawingContext drawing)
    {
        FormattedText title = CreateText(
            _title,
            34,
            FontWeights.SemiBold,
            Brushes.White,
            1130);
        drawing.DrawText(title, new Point(48, 31));

        Rect closeButton = new(CloseLeft, CloseTop, CloseRight - CloseLeft, CloseBottom - CloseTop);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(79, 91, 109)),
            null,
            closeButton,
            14,
            14);
        FormattedText closeText = CreateText(
            "閉\nじ\nる\n\nC\nL\nO\nS\nE",
            28,
            FontWeights.SemiBold,
            Brushes.White,
            150);
        closeText.TextAlignment = TextAlignment.Center;
        closeText.LineHeight = 43;
        drawing.DrawText(closeText, new Point(1029, 170));
    }

    private FormattedText CreateBodyText()
    {
        FormattedText body = CreateText(
            _body,
            30,
            FontWeights.Normal,
            Brushes.White,
            BodyRight - BodyLeft - 20);
        body.LineHeight = 43;
        return body;
    }

    private void DrawScrollIndicator(DrawingContext drawing, int page)
    {
        if (_resultPageCount <= 1)
        {
            return;
        }

        (double thumbTop, double thumbHeight, _) = GetScrollbarMetrics(page);
        double trackHeight = BodyBottom - BodyTop;
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(55, 66, 82)),
            null,
            new Rect(ScrollTrackLeft, BodyTop, ScrollTrackRight - ScrollTrackLeft, trackHeight),
            17,
            17);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(115, 171, 255)),
            null,
            new Rect(ScrollTrackLeft, thumbTop, ScrollTrackRight - ScrollTrackLeft, thumbHeight),
            17,
            17);
    }

    private (double Top, double Height, double Travel) GetScrollbarMetrics() =>
        GetScrollbarMetrics(_resultPage);

    private (double Top, double Height, double Travel) GetScrollbarMetrics(int page)
    {
        double trackHeight = BodyBottom - BodyTop;
        double thumbHeight = Math.Max(MinimumThumbHeight, trackHeight / _resultPageCount);
        double travel = trackHeight - thumbHeight;
        double position = _resultPageCount <= 1
            ? 0
            : travel * page / (_resultPageCount - 1);
        return (BodyTop + position, thumbHeight, travel);
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

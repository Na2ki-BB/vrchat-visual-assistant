using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace VrcVa.Windows.OpenVr;

internal enum ResultPanelAction
{
    None,
    PreviousPage,
    NextPage,
    Close,
}

internal readonly record struct WristLauncherCalibrationButton(
    WristLauncherCalibrationAction Action,
    Rect Bounds,
    string Label);

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
    private const int MaximumResultPages = 3;
    internal const double HeaderHeight = 108;
    internal const double BodyTop = 116;
    internal const double BodyTextBottom = 568;
    internal const double BodyBottom = 668;
    internal const double BodyLeft = 54;
    internal const double BodyRight = 1180;
    internal const double ScrollTrackLeft = 1198;
    internal const double ScrollTrackRight = 1268;
    private const double MinimumThumbHeight = 64;
    // Keep navigation in a fixed rail below the result text. Rendering and hit
    // testing share these exact rectangles so the visible controls and their
    // actionable areas cannot drift apart.
    internal static readonly Rect PreviousPageButtonBounds = new(748, 580, 136, 80);
    internal static readonly Rect NextPageButtonBounds = new(884, 580, 136, 80);
    internal static readonly Rect CloseButtonBounds = new(1020, 580, 160, 80);

    private readonly Typeface _bodyTypeface = new("Yu Gothic UI");
    private string _title = string.Empty;
    private string _body = string.Empty;
    private int _resultPage;
    private int _resultPageCount = 1;
    private bool _resultTruncated;

    public int CurrentResultCell => FirstResultCell + _resultPage;

    internal int CurrentResultPage => _resultPage;

    internal int ResultPageCount => _resultPageCount;

    internal bool ResultTruncated => _resultTruncated;

    public void SetContent(string title, string body)
    {
        _title = title;
        _body = body;
        _resultPage = 0;
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
        CloseButtonBounds.Contains(x, y);

    public ResultPanelAction HitTestResult(float x, float y)
    {
        Point point = new(x, y);
        if (CloseButtonBounds.Contains(point))
        {
            return ResultPanelAction.Close;
        }

        // Rail buttons are painted Previous -> Next -> Close. Test in the
        // reverse visual Z order so their shared boundary belongs to the
        // control the user actually sees. A disabled top control owns its
        // visible area and must not fall through to a button drawn below it.
        if (NextPageButtonBounds.Contains(point))
        {
            return _resultPage < _resultPageCount - 1
                ? ResultPanelAction.NextPage
                : ResultPanelAction.None;
        }

        if (PreviousPageButtonBounds.Contains(point))
        {
            return _resultPage > 0
                ? ResultPanelAction.PreviousPage
                : ResultPanelAction.None;
        }

        return ResultPanelAction.None;
    }

    public bool Apply(ResultPanelAction action)
    {
        int previous = _resultPage;
        _resultPage = action switch
        {
            ResultPanelAction.PreviousPage => Math.Max(0, _resultPage - 1),
            ResultPanelAction.NextPage => Math.Min(_resultPageCount - 1, _resultPage + 1),
            _ => _resultPage,
        };
        return previous != _resultPage;
    }

    public bool BeginScrollbarInteraction(float x, float y)
    {
        if (!IsScrollbar(x, y))
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

    public bool IsScrollbar(float x, float y) =>
        _resultPageCount > 1
        && x >= ScrollTrackLeft
        && x <= ScrollTrackRight
        && y >= BodyTop
        && y <= BodyBottom;

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

    public static WristLauncherCalibrationAction HitTestWristLauncherCalibration(
        float x,
        float y)
    {
        foreach (WristLauncherCalibrationButton button in WristLauncherCalibrationButtons)
        {
            if (button.Bounds.Contains(x, y))
            {
                return button.Action;
            }
        }

        return WristLauncherCalibrationAction.None;
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

            (FormattedText body, double viewportHeight) = PrepareResultLayout();
            for (int page = 0; page < MaximumResultPages; page++)
            {
                DrawResultCell(drawing, body, page, viewportHeight);
            }
        }

        return RenderVisualRgba(visual, AtlasPixelWidth, AtlasPixelHeight);
    }

    public byte[] RenderCurrentResultRgba()
    {
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            (FormattedText body, double viewportHeight) = PrepareResultLayout();
            DrawResultPage(drawing, body, _resultPage, viewportHeight);
        }

        return RenderVisualRgba(visual, PixelWidth, PixelHeight);
    }

    public byte[] RenderCalibrationRgba()
    {
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            DrawCalibrationSurface(drawing);
        }

        return RenderVisualRgba(visual, PixelWidth, PixelHeight);
    }

    public byte[] RenderWristLauncherCalibrationRgba()
    {
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            DrawWristLauncherCalibrationSurface(drawing);
        }

        return RenderVisualRgba(visual, PixelWidth, PixelHeight);
    }

    private static byte[] RenderVisualRgba(
        DrawingVisual visual,
        int pixelWidth,
        int pixelHeight)
    {
        RenderTargetBitmap bitmap = new(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        byte[] bgra = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(bgra, pixelWidth * 4, 0);
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
        DrawResultPage(drawing, body, page, viewportHeight);
        drawing.Pop();
    }

    private void DrawResultPage(
        DrawingContext drawing,
        FormattedText body,
        int page,
        double viewportHeight)
    {
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

        DrawResultControls(drawing, page);
    }

    private void DrawCalibrationSurface(DrawingContext drawing)
    {
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
    }

    private void DrawWristLauncherCalibrationSurface(DrawingContext drawing)
    {
        DrawBackground(drawing);
        FormattedText title = CreateText(
            "手首ランチャーの位置調整",
            34,
            FontWeights.SemiBold,
            Brushes.White,
            1180);
        drawing.DrawText(title, new Point(44, 21));
        FormattedText hint = CreateText(
            "左手のランチャーを見ながら調整（1回：位置1 cm / 向き5° / サイズ5%）",
            22,
            FontWeights.Normal,
            new SolidColorBrush(Color.FromRgb(190, 205, 225)),
            1180);
        drawing.DrawText(hint, new Point(44, 66));

        DrawCalibrationGroupHeading(drawing, "腕の表裏", 44, 109, 362);
        DrawCalibrationGroupHeading(drawing, "上下", 437, 109, 362);
        DrawCalibrationGroupHeading(drawing, "腕に沿って", 830, 109, 362);
        DrawCalibrationGroupHeading(drawing, "縦の向き", 44, 262, 362);
        DrawCalibrationGroupHeading(drawing, "横の向き", 437, 262, 362);
        DrawCalibrationGroupHeading(drawing, "傾き", 830, 262, 362);
        DrawCalibrationGroupHeading(drawing, "大きさ", 437, 416, 362);

        foreach (WristLauncherCalibrationButton button in WristLauncherCalibrationButtons)
        {
            Color color = button.Action switch
            {
                WristLauncherCalibrationAction.Save => Color.FromRgb(22, 115, 154),
                WristLauncherCalibrationAction.Cancel => Color.FromRgb(92, 74, 80),
                WristLauncherCalibrationAction.Reset => Color.FromRgb(67, 79, 96),
                _ => Color.FromRgb(53, 72, 96),
            };
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(color),
                new System.Windows.Media.Pen(
                    new SolidColorBrush(Color.FromRgb(113, 143, 181)),
                    2),
                button.Bounds,
                14,
                14);
            FormattedText text = CreateText(
                button.Label,
                button.Action is WristLauncherCalibrationAction.Save
                    or WristLauncherCalibrationAction.Cancel
                    or WristLauncherCalibrationAction.Reset
                    ? 27
                    : 25,
                FontWeights.SemiBold,
                Brushes.White,
                button.Bounds.Width - 16);
            text.TextAlignment = TextAlignment.Center;
            drawing.DrawText(
                text,
                new Point(
                    button.Bounds.Left + 8,
                    button.Bounds.Top + ((button.Bounds.Height - text.Height) / 2)));
        }
    }

    private void DrawCalibrationGroupHeading(
        DrawingContext drawing,
        string label,
        double left,
        double top,
        double width)
    {
        FormattedText text = CreateText(
            label,
            21,
            FontWeights.SemiBold,
            new SolidColorBrush(Color.FromRgb(177, 191, 211)),
            width);
        text.TextAlignment = TextAlignment.Center;
        drawing.DrawText(text, new Point(left, top));
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

    internal static IReadOnlyList<WristLauncherCalibrationButton>
        WristLauncherCalibrationControls => WristLauncherCalibrationButtons;

    private static readonly WristLauncherCalibrationButton[] WristLauncherCalibrationButtons =
    [
        new(
            WristLauncherCalibrationAction.MoveTowardHandBack,
            new Rect(44, 145, 174, 99),
            "甲へ  −X"),
        new(
            WristLauncherCalibrationAction.MoveTowardPalm,
            new Rect(232, 145, 174, 99),
            "掌へ  ＋X"),
        new(
            WristLauncherCalibrationAction.MoveDown,
            new Rect(437, 145, 174, 99),
            "下へ  −Y"),
        new(
            WristLauncherCalibrationAction.MoveUp,
            new Rect(625, 145, 174, 99),
            "上へ  ＋Y"),
        new(
            WristLauncherCalibrationAction.MoveTowardFingertips,
            new Rect(830, 145, 174, 99),
            "手先へ  −Z"),
        new(
            WristLauncherCalibrationAction.MoveTowardElbow,
            new Rect(1018, 145, 174, 99),
            "肘へ  ＋Z"),
        new(
            WristLauncherCalibrationAction.DecreasePitch,
            new Rect(44, 298, 174, 99),
            "縦  −5°"),
        new(
            WristLauncherCalibrationAction.IncreasePitch,
            new Rect(232, 298, 174, 99),
            "縦  ＋5°"),
        new(
            WristLauncherCalibrationAction.DecreaseYaw,
            new Rect(437, 298, 174, 99),
            "横  −5°"),
        new(
            WristLauncherCalibrationAction.IncreaseYaw,
            new Rect(625, 298, 174, 99),
            "横  ＋5°"),
        new(
            WristLauncherCalibrationAction.DecreaseRoll,
            new Rect(830, 298, 174, 99),
            "傾き  −5°"),
        new(
            WristLauncherCalibrationAction.IncreaseRoll,
            new Rect(1018, 298, 174, 99),
            "傾き  ＋5°"),
        new(
            WristLauncherCalibrationAction.MakeSmaller,
            new Rect(437, 453, 174, 83),
            "小さく  −5%"),
        new(
            WristLauncherCalibrationAction.MakeLarger,
            new Rect(625, 453, 174, 83),
            "大きく  ＋5%"),
        new(
            WristLauncherCalibrationAction.Reset,
            new Rect(44, 575, 270, 105),
            "初期値"),
        new(
            WristLauncherCalibrationAction.Cancel,
            new Rect(348, 575, 270, 105),
            "中止"),
        new(
            WristLauncherCalibrationAction.Save,
            new Rect(652, 575, 540, 105),
            "保存して閉じる"),
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
            new Rect(0, 0, PixelWidth, HeaderHeight));
    }

    private void DrawHeader(DrawingContext drawing)
    {
        FormattedText title = CreateText(
            _title,
            34,
            FontWeights.SemiBold,
            Brushes.White,
            1184);
        drawing.DrawText(title, new Point(48, 31));
    }

    private void DrawResultControls(DrawingContext drawing, int page)
    {
        DrawResultButton(drawing, PreviousPageButtonBounds, "◀ 前へ", page > 0);
        DrawResultButton(
            drawing,
            NextPageButtonBounds,
            "次へ ▶",
            page < _resultPageCount - 1);
        DrawResultButton(drawing, CloseButtonBounds, "閉じる ×", enabled: true);
    }

    private void DrawResultButton(
        DrawingContext drawing,
        Rect bounds,
        string label,
        bool enabled)
    {
        Color background = enabled
            ? Color.FromRgb(72, 91, 118)
            : Color.FromRgb(48, 57, 70);
        Color foreground = enabled
            ? Color.FromRgb(245, 249, 255)
            : Color.FromRgb(111, 123, 140);
        drawing.DrawRectangle(new SolidColorBrush(background), null, bounds);
        // WPF centers a Pen on the supplied geometry. Inset the border by its
        // one-pixel half-width so no visible pixel extends beyond the exact
        // rectangle consumed by HitTestResult.
        Rect borderBounds = new(
            bounds.Left + 1,
            bounds.Top + 1,
            bounds.Width - 2,
            bounds.Height - 2);
        drawing.DrawRectangle(
            null,
            new System.Windows.Media.Pen(
                new SolidColorBrush(Color.FromRgb(103, 128, 161)),
                2),
            borderBounds);
        FormattedText text = CreateText(
            label,
            28,
            FontWeights.SemiBold,
            new SolidColorBrush(foreground),
            bounds.Width);
        text.TextAlignment = TextAlignment.Center;
        drawing.DrawText(
            text,
            new Point(bounds.Left, bounds.Top + ((bounds.Height - text.Height) / 2) - 2));
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

    private (FormattedText Body, double ViewportHeight) PrepareResultLayout()
    {
        FormattedText body = CreateBodyText();
        double viewportHeight = BodyTextBottom - BodyTop;
        int requiredPages = Math.Max(1, (int)Math.Ceiling(body.Height / viewportHeight));
        _resultPageCount = Math.Min(MaximumResultPages, requiredPages);
        _resultTruncated = requiredPages > MaximumResultPages;
        _resultPage = Math.Clamp(_resultPage, 0, _resultPageCount - 1);
        return (body, viewportHeight);
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

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Video;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace VrcVa.Windows.OpenVr;

internal readonly record struct VrVoiceSearchControl(VrVoiceSearchAction Action, Rect Bounds, string Label, bool Enabled);

internal sealed partial class ResultPanelTexture
{
    internal static readonly Rect TranscriptBounds = new(54, 132, 1126, 288);
    internal static readonly Rect DirectSearchBounds = new(54, 476, 550, 78);
    internal static readonly Rect InterpretedSearchBounds = new(626, 476, 554, 78);
    private const double TranscriptLineHeight = 36;
    internal VrVoiceSearchSnapshot? VoiceSearch { get; private set; }

    internal void SetVoiceSearch(VrVoiceSearchSnapshot snapshot)
    {
        SetContent(string.Empty, string.Empty);
        VoiceSearch = snapshot;
        _title = snapshot.IsInput ? "認識文 / 検索方法を選択" : snapshot.Result is { } result
            ? $"動画候補 / 取得 {result.Candidates.Count} 件{(result.IsPartial ? "（一部）" : string.Empty)}"
            : snapshot.State == VideoSearchFlowState.Failed ? "動画検索に失敗しました" : "動画検索 / 処理中";
        _resultPage = snapshot.PageIndex;
        _resultPageCount = snapshot.PageCount;
        _resultTruncated = false;
    }

    internal int MeasureTranscriptPages(string transcript) => Math.Max(1,
        (int)Math.Ceiling(CreateTranscriptText(transcript).Height / TranscriptBounds.Height));

    private FormattedText CreateTranscriptText(string text)
    {
        FormattedText formatted = CreateText(text, 28, FontWeights.Normal, Brushes.White, TranscriptBounds.Width);
        formatted.LineHeight = TranscriptLineHeight;
        return formatted;
    }

    internal static IReadOnlyList<VrVoiceSearchControl> VoiceSearchControls(VrVoiceSearchSnapshot snapshot)
    {
        List<VrVoiceSearchControl> controls = [];
        void Add(VrVoiceSearchAction action, Rect bounds, string label) => controls.Add(new(action, bounds, label, snapshot.Allows(action)));
        if (snapshot.IsInput)
        {
            Add(VrVoiceSearchAction.DirectSearch, DirectSearchBounds, "そのまま検索");
            Add(VrVoiceSearchAction.InterpretedSearch, InterpretedSearchBounds, "解釈して検索");
        }
        for (int index = 0; index < snapshot.Cards.Count; index++)
        {
            Add(VrVoiceSearchAction.Candidate1 + index, new Rect(54, 218 + index * 70, 1126, 62), snapshot.Cards[index].Candidate.Title);
        }
        Add(VrVoiceSearchAction.Previous, new Rect(54, 580, 130, 80), "前へ");
        Add(VrVoiceSearchAction.Next, new Rect(196, 580, 130, 80), "次へ");
        Add(VrVoiceSearchAction.Back, new Rect(338, 580, 130, 80), "入力へ");
        Add(VrVoiceSearchAction.Rerecord, new Rect(480, 580, 130, 80), "録り直す");
        Add(VrVoiceSearchAction.Retry, new Rect(622, 580, 130, 80), "やり直す");
        Add(VrVoiceSearchAction.Cancel, new Rect(764, 580, 130, 80), "中止");
        Add(VrVoiceSearchAction.AdjustPlacement, new Rect(906, 580, 130, 80), "位置調整");
        Add(VrVoiceSearchAction.Close, new Rect(1048, 580, 132, 80), "閉じる");
        return controls;
    }

    internal VrVoiceSearchAction HitTestVoiceSearch(float x, float y)
    {
        if (VoiceSearch is null) { return VrVoiceSearchAction.None; }
        foreach (VrVoiceSearchControl control in VoiceSearchControls(VoiceSearch))
        {
            if (control.Bounds.Contains(x, y)) { return control.Enabled ? control.Action : VrVoiceSearchAction.None; }
        }
        return VrVoiceSearchAction.None;
    }

    private void DrawVoiceSearch(DrawingContext drawing)
    {
        VrVoiceSearchSnapshot snapshot = VoiceSearch!;
        DrawBackground(drawing);
        DrawHeader(drawing);
        if (snapshot.IsInput)
        {
            FormattedText transcript = CreateTranscriptText(snapshot.Input!.Transcript);
            drawing.PushClip(new RectangleGeometry(TranscriptBounds));
            drawing.DrawText(transcript, new Point(TranscriptBounds.Left, TranscriptBounds.Top - snapshot.PageIndex * TranscriptBounds.Height));
            drawing.Pop();
            DrawBoundedVoiceText(drawing, "直接: YouTubeへ原文 / 解釈: OpenAIへ原文（有料）→ YouTube", new Rect(54, 433, 1126, 30), 22);
        }
        else if (snapshot.Result is not null)
        {
            DrawBoundedVoiceText(drawing, "検索語: " + snapshot.Query, new Rect(54, 116, 1126, 62), 24);
            DrawBoundedVoiceText(drawing, snapshot.Message, new Rect(54, 184, 1126, 28), 22);
        }
        else
        {
            DrawBoundedVoiceText(drawing, snapshot.Message, new Rect(54, 132, 1126, 156), 30);
            DrawBoundedVoiceText(drawing, snapshot.Failure, new Rect(54, 298, 1126, 76), 24);
            DrawBoundedVoiceText(drawing, snapshot.Query.Length == 0
                ? "解釈のやり直しは有料の再送です。自動再送はしません。"
                : "検索語: " + snapshot.Query + "\n検索のやり直しは確定済みの語を再利用します。", new Rect(54, 380, 1126, 80), 24);
        }
        foreach (VrVoiceSearchControl control in VoiceSearchControls(snapshot))
        {
            if (control.Action is >= VrVoiceSearchAction.Candidate1 and <= VrVoiceSearchAction.Candidate5)
            {
                DrawVideoCard(drawing, control, snapshot.Cards[(int)control.Action - (int)VrVoiceSearchAction.Candidate1]);
            }
            else { DrawResultButton(drawing, control.Bounds, control.Label, control.Enabled); }
        }
        string footer = snapshot.IsInput ? $"認識文 {snapshot.PageIndex + 1}/{snapshot.PageCount} ページ / 原文を保持"
            : snapshot.Result is not null ? $"候補 {snapshot.PageIndex + 1}/{snapshot.PageCount} ページ / "
                + (snapshot.PageIndex + 1 == snapshot.PageCount ? "取得した候補はここまで / " : string.Empty)
                + "選択でclipboard上書き・貼り付けは手動" : "閉じると内容を破棄 / 送信済み要求の中止は料金取消を保証しません";
        DrawBoundedVoiceText(drawing, footer, new Rect(54, 677, 1160, 28), 20);
    }

    private void DrawVideoCard(DrawingContext drawing, VrVoiceSearchControl control, VrVideoCard card)
    {
        Rect bounds = control.Bounds;
        drawing.DrawRoundedRectangle(new SolidColorBrush(control.Enabled ? Color.FromRgb(35, 67, 94) : Color.FromRgb(40, 45, 53)),
            null, bounds, 10, 10);
        Rect border = bounds;
        border.Inflate(-1, -1);
        drawing.DrawRoundedRectangle(null, new System.Windows.Media.Pen(Brushes.SlateGray, 2), border, 9, 9);
        Rect imageBounds = new(bounds.Left + 6, bounds.Top + 4, 96, 54);
        drawing.DrawRectangle(Brushes.DarkSlateGray, null, imageBounds);
        if (card.Thumbnail is { UsePlaceholder: false, Image: { } image })
        {
            BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
                image.Bgra32.ToArray(), image.Width * 4);
            bitmap.Freeze();
            drawing.DrawImage(bitmap, imageBounds);
        }
        else { DrawBoundedVoiceText(drawing, "画像なし", imageBounds, 18); }
        DrawBoundedVoiceText(drawing, control.Label, new Rect(bounds.Left + 116, bounds.Top + 5, bounds.Width - 132, 52), 22);
    }

    private void DrawBoundedVoiceText(DrawingContext drawing, string text, Rect bounds, double size)
    {
        FormattedText formatted = CreateText(text, size, FontWeights.Normal, Brushes.White, bounds.Width);
        formatted.LineHeight = size + 4;
        formatted.MaxTextHeight = bounds.Height;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawText(formatted, bounds.TopLeft);
        drawing.Pop();
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcVa.Core;
using VrcVa.Infrastructure;

namespace VrcVa.Windows.Video;

public partial class VideoSearchPanel : System.Windows.Controls.UserControl
{
    private readonly VideoSearchFlow _flow;
    private readonly TextRequestQuota _quota;
    private readonly Func<Task> _close;
    private readonly Dictionary<Guid, (VideoThumbnailImage Image, BitmapSource Bitmap)> _bitmaps = [];
    private CandidateCard[] _cards = [];

    internal VideoSearchPanel(VideoSearchFlow flow, TextRequestQuota quota, Func<Task> close)
    {
        InitializeComponent();
        _flow = flow;
        _quota = quota;
        _close = close;
        flow.Changed += Flow_Changed;
        Refresh();
    }

    private void Flow_Changed(object? sender, EventArgs args) { Dispatcher.VerifyAccess(); Refresh(); }

    internal void Refresh()
    {
        DirectSearchButton.IsEnabled = _flow.CanSearch;
        InterpretedSearchButton.IsEnabled = _flow.CanSearch;
        // Both choices exist directly below the transcript, including before the first search.
        CancelSearchButton.IsEnabled = _flow.CanCancel;
        RetrySearchButton.Visibility = _flow.State == VideoSearchFlowState.Failed ? Visibility.Visible : Visibility.Collapsed;
        RetrySearchButton.IsEnabled = _flow.CanRetry;
        RetrySearchButton.Content = _flow.Query.Length == 0 ? "やり直す" : "確定済みの検索語でやり直す";
        BackToInputButton.IsEnabled = _flow.CanSearch;
        CloseSearchButton.IsEnabled = true; // closing invalidates immediately and awaits cleanup
        SearchStatusText.Text = _flow.Message;
        FailureStageText.Text = _flow.FailureCode is { } failure
            ? $"失敗した段階: {(_flow.FailureStage == ScanStage.SearchInterpretation ? "検索語の解釈" : _flow.FailureStage == ScanStage.Trigger ? "設定確認" : "YouTube検索")} / {failure}" : string.Empty;
        QueryText.Text = _flow.Query.Length == 0 ? string.Empty : $"検索語: {_flow.Query}";
        InterpretationQuotaText.Text = $"検索解釈枠: 残り {_quota.Remaining}/{_quota.Maximum} 回 / 起動。翻訳・音声とは別枠です。送信後の失敗・中止も消費し、解釈の再送は追加の1回です。検索だけの再試行とページ移動は再解釈しません。";
        VideoSearchResult? result = _flow.Result;
        CandidateCountText.Text = result is null ? string.Empty
            : $"取得 {result.Candidates.Count} 件 / {_flow.PageIndex + 1} / {result.PageCount} ページ（1ページ最大5件）"
                + (result.IsPartial ? " / 一部のみ取得" : string.Empty);
        HashSet<Guid> currentIds = result?.Candidates.Select(candidate => candidate.CandidateId).ToHashSet() ?? [];
        foreach (Guid id in _bitmaps.Keys.Where(id => !currentIds.Contains(id)).ToArray()) { _bitmaps.Remove(id); }
        IReadOnlyList<VideoCandidate> candidates = _flow.Candidates;
        if (!_cards.Select(card => card.Action.CandidateId).SequenceEqual(candidates.Select(candidate => candidate.CandidateId)))
        {
            _cards = candidates.Select(candidate => new CandidateCard(candidate.Title, candidate.CreateSelectionAction())).ToArray();
            CandidateCards.ItemsSource = _cards;
        }
        for (int index = 0; index < candidates.Count; index++)
        {
            _cards[index].Update(ToBitmap(candidates[index].CandidateId, _flow.ImageFor(candidates[index])), _flow.CanSelectCandidates);
        }
        PreviousPageButton.IsEnabled = _flow.CanPrevious;
        NextPageButton.IsEnabled = _flow.CanNext;
        Pagination.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        BatchEndText.Visibility = result is not null && _flow.PageIndex + 1 == result.PageCount ? Visibility.Visible : Visibility.Collapsed;
    }

    private BitmapSource? ToBitmap(Guid candidateId, VideoThumbnailResult? thumbnail)
    {
        if (thumbnail?.UsePlaceholder != false || thumbnail.Image is not { } image) { return null; }
        if (_bitmaps.TryGetValue(candidateId, out var cached) && ReferenceEquals(cached.Image, image)) { return cached.Bitmap; }
        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
            image.Bgra32.ToArray(), image.Width * 4);
        bitmap.Freeze();
        _bitmaps[candidateId] = (image, bitmap);
        return bitmap;
    }

    private void DirectSearchButton_Click(object sender, RoutedEventArgs args) => _flow.TrySearch(interpreted: false);
    private void InterpretedSearchButton_Click(object sender, RoutedEventArgs args) => _flow.TrySearch(interpreted: true);
    private void CancelSearchButton_Click(object sender, RoutedEventArgs args) => _flow.Cancel();
    private void RetrySearchButton_Click(object sender, RoutedEventArgs args) => _flow.TrySearch(interpreted: false, retry: true);
    private void BackToInputButton_Click(object sender, RoutedEventArgs args) => _flow.BackToInput();
    private async void CloseSearchButton_Click(object sender, RoutedEventArgs args) => await _close();
    private void PreviousPageButton_Click(object sender, RoutedEventArgs args) => _flow.MovePage(-1);
    private void NextPageButton_Click(object sender, RoutedEventArgs args) => _flow.MovePage(1);
    private void Candidate_Click(object sender, RoutedEventArgs args)
    {
        if (sender is System.Windows.Controls.Button { Tag: VideoCandidateAction selection }) { _flow.TryCopy(selection); }
    }

    internal void Detach()
    {
        _flow.Changed -= Flow_Changed;
        CandidateCards.ItemsSource = null;
        _bitmaps.Clear();
        _cards = [];
        QueryText.Text = string.Empty;
    }

    private sealed class CandidateCard(string title, VideoCandidateAction action) : INotifyPropertyChanged
    {
        public string Title { get; } = title;
        public VideoCandidateAction Action { get; } = action;
        public BitmapSource? Image { get; private set; }
        public bool CanSelect { get; private set; }
        public string AccessibleName => $"{Title} の動画URLをコピー";
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(BitmapSource? image, bool canSelect)
        {
            if (!ReferenceEquals(Image, image))
            {
                Image = image;
                PropertyChanged?.Invoke(this, new(nameof(Image)));
            }
            if (CanSelect != canSelect)
            {
                CanSelect = canSelect;
                PropertyChanged?.Invoke(this, new(nameof(CanSelect)));
            }
        }
    }
}

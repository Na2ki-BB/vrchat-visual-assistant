using VrcVa.Core;

namespace VrcVa.Windows.Video;

internal enum VideoSearchFlowState { Input, Interpreting, Searching, Candidates, Copying, Failed, Cancelling, Closed }

/// <summary>Dispatcher-owned orchestration. All public entry points and presentation checks run on the same UI thread.</summary>
internal sealed class VideoSearchFlow : IAsyncDisposable
{
    private readonly ExecutionCoordinator _execution;
    private readonly VideoSearchSession _session;
    private readonly FeatureCatalog _catalog;
    private readonly Func<TextInputSession?> _currentInput;
    private readonly Action _prepare;
    private readonly IVideoThumbnailProvider _thumbnails;
    private readonly VideoCandidateClipboard _clipboard;
    private readonly Dictionary<Guid, VideoThumbnailResult> _images = [];
    private ExecutionOperation? _operation;
    private TextInputSession? _input;
    private VideoSearchResult? _result;
    private FeatureId _feature;
    private Guid? _retrySearchOperationId;
    private string _query = string.Empty;
    private CancellationTokenSource? _imageCancellation;
    private Task _imageTasks = Task.CompletedTask;
    private Task _pending = Task.CompletedTask;
    private long _viewGeneration;
    private bool _disposed;

    public VideoSearchFlow(ExecutionCoordinator execution, VideoSearchSession session, FeatureCatalog catalog,
        Func<TextInputSession?> currentInput, Action prepare, IVideoThumbnailProvider thumbnails,
        VideoCandidateClipboard clipboard)
    {
        _execution = execution;
        _session = session;
        _catalog = catalog;
        _currentInput = currentInput;
        _prepare = prepare;
        _thumbnails = thumbnails;
        _clipboard = clipboard;
    }

    public event EventHandler? Changed;
    public VideoSearchFlowState State { get; private set; }
    public string Message { get; private set; } = "認識文の検索方法を選んでください。";
    public ScanFailureCode? FailureCode { get; private set; }
    public ScanStage? FailureStage { get; private set; }
    public bool RequiresRestart => FailureCode == ScanFailureCode.VideoSearchCleanupFailed;
    public bool IsBusy => State is VideoSearchFlowState.Interpreting or VideoSearchFlowState.Searching
        or VideoSearchFlowState.Copying or VideoSearchFlowState.Cancelling;
    public bool CanSearch => !_disposed && !RequiresRestart && !IsBusy && !_execution.IsRunning && _currentInput() is not null;
    public bool CanRetry => CanSearch && State == VideoSearchFlowState.Failed;
    public bool CanSelectCandidates => !IsBusy && !_execution.IsRunning && Result is not null;
    public bool CanCancel => IsBusy && State != VideoSearchFlowState.Cancelling;
    public int PageIndex { get; private set; }
    public VideoSearchResult? Result => IsResultCurrent() ? _result : null;
    public IReadOnlyList<VideoCandidate> Candidates => Result?.GetPage(PageIndex) ?? [];
    public string Query => Result?.Query ?? (IsInputCurrent() ? _query : string.Empty);
    public bool CanPrevious => !IsBusy && !_execution.IsRunning && Result is not null && PageIndex > 0;
    public bool CanNext => !IsBusy && !_execution.IsRunning && Result is not null && PageIndex + 1 < Result.PageCount;
    public Task WhenIdle => _pending;
    public Task WhenImagesIdle => _imageTasks;

    public bool TrySearch(bool interpreted, bool retry = false)
    {
        if (!CanSearch || (retry && !CanRetry)) { return false; }
        TextInputSession input = _currentInput()!;
        FeatureId feature = retry ? _feature : interpreted ? FeatureIds.InterpretedVideoSearch : FeatureIds.DirectVideoSearch;
        Guid? retryId = retry && feature == FeatureIds.InterpretedVideoSearch ? _retrySearchOperationId : null;
        if (!_execution.TryBeginOperation(input.SessionId, Guid.NewGuid(), out ExecutionOperation? operation)) { return false; }
        InvalidateView();
        if (!retry) { _retrySearchOperationId = null; }
        _query = string.Empty;
        _input = input;
        _operation = operation;
        _feature = feature;
        FailureCode = null;
        FailureStage = null;
        State = feature == FeatureIds.InterpretedVideoSearch && retryId is null
            ? VideoSearchFlowState.Interpreting : VideoSearchFlowState.Searching;
        Message = State == VideoSearchFlowState.Interpreting ? "OpenAIへ認識文を送り、検索語を解釈しています。" : "YouTubeから動画の候補情報を取得しています。";
        _pending = RunSearchAsync(input, operation, feature, retryId, _viewGeneration);
        Notify();
        return true;
    }

    private async Task RunSearchAsync(TextInputSession input, ExecutionOperation operation, FeatureId feature,
        Guid? retryId, long view)
    {
        await Task.Yield(); // publish ownership and WhenIdle before fast fake providers complete
        VideoSearchResult? result = null;
        ScanException? failure = null;
        bool cancelled = false;
        try
        {
            operation.ThrowIfNotCurrent();
            try { _prepare(); }
            catch (Exception)
            {
                throw new ScanException(ScanFailureCode.VideoSearchNotConfigured, ScanStage.Trigger,
                    "検索設定を読み込めませんでした。設定を確認してやり直してください。");
            }
            ScanRequest request = ScanRequest.CreateText("voice-video-search", feature, input) with
            { CorrelationId = operation.OperationId, RetrySearchOperationId = retryId };
            IProgress<ScanProgress> progress = new Progress<ScanProgress>(value =>
            {
                if (view != _viewGeneration || !ReferenceEquals(_operation, operation) || !operation.IsCurrent) { return; }
                _query = QueryFor(operation);
                State = value.Stage == ScanStage.SearchInterpretation ? VideoSearchFlowState.Interpreting : VideoSearchFlowState.Searching;
                Message = State == VideoSearchFlowState.Interpreting ? "OpenAIへ認識文を送り、検索語を解釈しています。" : "YouTubeから動画の候補情報を取得しています。";
                Notify();
            });
            FeatureResult response = await _catalog.Resolve(feature).TextHandler!.HandleAsync(input, request, progress, operation.CancellationToken);
            operation.ThrowIfNotCurrent();
            result = response.VideoSearch ?? throw new InvalidOperationException("Missing candidate snapshot.");
        }
        catch (ScanException exception) when (exception.FailureCode == ScanFailureCode.VideoSearchCleanupFailed)
        {
            // The provider intentionally invalidates all sessions when process cleanup cannot be proved.
            failure = exception;
        }
        catch (Exception) when (!operation.IsCurrent) { cancelled = true; }
        catch (ScanException exception) { failure = exception; }
        catch (Exception)
        {
            failure = new ScanException(ScanFailureCode.VideoSearchFailed,
                feature == FeatureIds.InterpretedVideoSearch && _session.CurrentInterpretedQuery is null
                    ? ScanStage.SearchInterpretation : ScanStage.TextHandling,
                "処理に失敗しました。本人の操作でやり直してください。");
        }
        finally
        {
            operation.Dispose(); // retain the gate until adapter cleanup has finished
            if (ReferenceEquals(_operation, operation)) { _operation = null; }
        }
        if (_disposed) { return; }
        if (failure?.FailureCode == ScanFailureCode.VideoSearchCleanupFailed)
        {
            _session.DiscardInvalidatedState();
            InvalidateView();
            _input = null;
            FailureCode = failure.FailureCode;
            FailureStage = failure.Stage;
            State = VideoSearchFlowState.Failed;
            Message = "yt-dlpの終了を確認できません。安全のため全処理を停止しました。アプリを終了し、再起動してください。";
        }
        else if (view != _viewGeneration) { Notify(); return; }
        else if (!IsInputCurrent()) { InvalidateView(); State = VideoSearchFlowState.Closed; }
        else if (cancelled)
        {
            _query = string.Empty;
            State = VideoSearchFlowState.Input;
            Message = "中止と後処理が完了しました。認識文は保持しています。";
        }
        else if (failure is not null)
        {
            _query = QueryFor(operation);
            if (_session.RetryableSearchOperationId == operation.OperationId)
            {
                _retrySearchOperationId = operation.OperationId;
            }
            FailureCode = failure.FailureCode;
            FailureStage = failure.Stage;
            State = VideoSearchFlowState.Failed;
            Message = FailureMessage(failure.FailureCode);
        }
        else
        {
            _result = result;
            PageIndex = 0;
            State = VideoSearchFlowState.Candidates;
            Message = result!.Candidates.Count == 0
                ? "候補は0件でした。入力へ戻り、録り直すか閉じてください。"
                : result.IsPartial ? "一部の候補を取得できませんでした。取得できた候補を表示します。" : "候補を選ぶと、その動画のURLをコピーします。";
            StartImages(result);
        }
        Notify();
    }

    public void Cancel()
    {
        if (!CanCancel) { return; }
        if (_operation is not null) { _execution.CancelOperation(_operation); }
        else if (State == VideoSearchFlowState.Copying && IsInputCurrent()) { _execution.CancelCurrentOperation(); }
        CancelImages();
        State = VideoSearchFlowState.Cancelling;
        Message = "中止中です。処理とリソースの回収を待っています。";
        Notify();
    }

    public bool MovePage(int delta)
    {
        if ((delta != -1 && delta != 1) || IsBusy || _execution.IsRunning || Result is not { } result) { return false; }
        int next = PageIndex + delta;
        if (next < 0 || next >= result.PageCount) { return false; }
        PageIndex = next;
        Notify();
        return true;
    }

    public bool TryCopy(VideoCandidateAction selection)
    {
        if (_disposed || IsBusy || _execution.IsRunning || Result is null
            || !Candidates.Any(candidate => candidate.CreateSelectionAction() == selection)) { return false; }
        long view = _viewGeneration;
        State = VideoSearchFlowState.Copying;
        Message = "URLをコピーしています。";
        _pending = CopyAsync(selection, view);
        Notify();
        return true;
    }

    private async Task CopyAsync(VideoCandidateAction selection, long view)
    {
        // Acquire the shared gate synchronously before allowing another click.
        Task<VideoClipboardCopyResult> copy = _clipboard.CopyAsync(selection, Guid.NewGuid());
        await Task.Yield();
        VideoClipboardCopyResult result;
        try { result = await copy; }
        catch (Exception)
        {
            if (view == _viewGeneration && !_disposed && IsResultCurrent())
            {
                State = VideoSearchFlowState.Candidates;
                Message = "URLをコピーできませんでした。もう一度選択してください。";
                Notify();
            }
            return;
        }
        if (view != _viewGeneration || _disposed) { return; }
        if (_clipboard.IsFeedbackCurrent(result) && IsResultCurrent())
        {
            State = VideoSearchFlowState.Candidates;
            Message = result.Message;
        }
        else if (IsInputCurrent())
        {
            InvalidateView();
            State = VideoSearchFlowState.Input;
            Message = "コピーを中止しました。認識文から検索し直せます。";
        }
        else { InvalidateView(); State = VideoSearchFlowState.Closed; }
        Notify();
    }

    public void BackToInput()
    {
        if (_execution.IsRunning || RequiresRestart) { return; }
        InvalidateView();
        State = VideoSearchFlowState.Input;
        Message = "認識文は変更していません。目的と違う場合は録り直してください。";
        FailureCode = null;
        FailureStage = null;
        _query = string.Empty;
        _retrySearchOperationId = null;
        Notify();
    }

    public void Refresh()
    {
        if (_disposed || RequiresRestart) { return; }
        if (_input is not null && !IsInputCurrent())
        {
            _session.DiscardInvalidatedState();
            InvalidateView();
            _input = null;
            _query = string.Empty;
            _retrySearchOperationId = null;
            State = VideoSearchFlowState.Closed;
            Message = string.Empty;
        }
        Notify();
    }

    public VideoThumbnailResult? ImageFor(VideoCandidate candidate) => IsResultCurrent()
        && _images.TryGetValue(candidate.CandidateId, out var image) ? image : null;

    private void StartImages(VideoSearchResult result)
    {
        CancellationTokenSource cancellation = new();
        _imageCancellation = cancellation;
        long view = _viewGeneration;
        _imageTasks = Task.WhenAll(_imageTasks, LoadImagesAsync(result, view, cancellation));
    }

    private async Task LoadImagesAsync(VideoSearchResult result, long view, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(result.Candidates.Select(async candidate =>
            {
                VideoThumbnailResult image;
                try { image = await _thumbnails.LoadAsync(candidate.ThumbnailUrl, cancellation.Token); }
                catch (Exception) { image = new(VideoThumbnailStatus.Unavailable); }
                // Revalidate after await on the presentation thread. Late images never populate a newer view.
                if (cancellation.IsCancellationRequested || view != _viewGeneration || !IsResultCurrent()
                    || !ReferenceEquals(_result, result)) { return; }
                _images[candidate.CandidateId] = image;
                Notify();
            }));
        }
        finally
        {
            if (ReferenceEquals(_imageCancellation, cancellation)) { _imageCancellation = null; }
            cancellation.Dispose();
        }
    }

    private bool IsInputCurrent() => _input is not null && ReferenceEquals(_currentInput(), _input)
        && _execution.IsSessionCurrent(_input.SessionId);
    private string QueryFor(ExecutionOperation operation) => _session.CurrentSearchRequest is { } request
        && request.OperationId == operation.OperationId ? request.Query : string.Empty;
    private bool IsResultCurrent() => _result is not null && IsInputCurrent() && ReferenceEquals(_session.CurrentResult, _result);
    private void CancelImages() { _imageCancellation?.Cancel(); }
    private void InvalidateView() { _viewGeneration++; CancelImages(); _images.Clear(); _result = null; PageIndex = 0; }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task CloseAsync()
    {
        Guid sessionId = _input?.SessionId ?? _currentInput()?.SessionId ?? Guid.Empty;
        InvalidateView();
        _execution.CloseSession(sessionId);
        _session.DiscardInvalidatedState();
        _input = null;
        _query = string.Empty;
        _retrySearchOperationId = null;
        if (!RequiresRestart) { State = VideoSearchFlowState.Closed; Message = string.Empty; }
        Notify();
        await _pending;
        await _imageTasks;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        await CloseAsync();
    }

    private static string FailureMessage(ScanFailureCode failure) => failure switch
    {
        ScanFailureCode.SearchInterpretationNotConfigured => "解釈用のOpenAIキーがありません。既存のテキスト用資格情報設定で保存してください。音声専用キーは使いません。",
        ScanFailureCode.SearchInterpretationAuthenticationFailed => "検索語の解釈で認証に失敗しました。保存済みのテキスト用OpenAIキーを確認してください。",
        ScanFailureCode.SearchInterpretationUsageLimitReached => "この起動中の検索解釈枠を使い切りました。入力へ戻って、そのまま検索も選べます。",
        ScanFailureCode.SearchInterpretationRateLimited => "検索語の解釈がサービス側の利用制限で失敗しました。自動再送はしません。",
        ScanFailureCode.SearchInterpretationTimedOut => "検索語の解釈が時間内に完了しませんでした。やり直すと解釈を再送します。",
        ScanFailureCode.SearchInterpretationInvalidResponse => "検索語の解釈から有効な検索語が返りませんでした。やり直すか入力へ戻ってください。",
        ScanFailureCode.VideoSearchNotConfigured => "検索設定または固定版yt-dlpが未設定です。READMEの配置手順を確認してやり直してください。",
        ScanFailureCode.VideoSearchExecutableRejected => "yt-dlpの版・配置・ハッシュを確認できません。READMEの固定版の配置を確認してください。",
        ScanFailureCode.VideoSearchTimedOut => "YouTube検索が時間内に完了しませんでした。確定済みの検索語だけでやり直せます。",
        ScanFailureCode.VideoSearchInvalidMetadata => "YouTube検索の候補情報を検証できませんでした。やり直すか入力へ戻ってください。",
        ScanFailureCode.VideoSearchOutputTooLarge => "YouTube検索の応答が容量上限を超えました。やり直すか入力へ戻ってください。",
        _ => "検索処理に失敗しました。自動再送はしません。やり直すか入力へ戻ってください。",
    };
}

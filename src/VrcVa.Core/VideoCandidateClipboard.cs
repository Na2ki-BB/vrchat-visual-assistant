namespace VrcVa.Core;

public enum VideoClipboardCopyStatus
{
    Copied,
    Busy,
    StaleSelection,
    Cancelled,
    Unavailable,
}

/// <summary>Feedback carries the exact selection and copy generation, never a provider-supplied URL.</summary>
public sealed record VideoClipboardCopyResult(
    VideoCandidateAction Selection, Guid CopyOperationId, VideoClipboardCopyStatus Status)
{
    /// <summary>Generation observed at admission, including a rejected non-queued Busy click.</summary>
    public long Generation { get; init; }

    public bool CanRetry => Status is VideoClipboardCopyStatus.Busy or VideoClipboardCopyStatus.Unavailable;
    public string Message => Status switch
    {
        VideoClipboardCopyStatus.Copied => "URLをコピーしました",
        VideoClipboardCopyStatus.Busy => "クリップボードまたは処理が使用中です。もう一度選択してください",
        VideoClipboardCopyStatus.Unavailable => "URLをコピーできませんでした。もう一度選択してください",
        _ => string.Empty,
    };
}

/// <summary>Invokes one short synchronous write callback on the platform's clipboard thread.</summary>
public interface IVideoClipboardDispatcher
{
    Task<VideoClipboardCopyStatus> InvokeAsync(Func<VideoClipboardCopyStatus> write, CancellationToken cancellationToken);
}

public interface IVideoClipboardTextWriter
{
    void SetText(string text);
}

/// <summary>A clipboard admission is non-queuing and shares the application's execution gate.</summary>
public sealed class VideoCandidateClipboard
{
    private readonly ExecutionCoordinator _execution;
    private readonly VideoSearchSession _session;
    private readonly IVideoClipboardDispatcher _dispatcher;
    private readonly IVideoClipboardTextWriter _writer;

    public VideoCandidateClipboard(ExecutionCoordinator execution, VideoSearchSession session,
        IVideoClipboardDispatcher dispatcher, IVideoClipboardTextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(writer);
        _execution = execution;
        _session = session;
        _dispatcher = dispatcher;
        _writer = writer;
    }

    public async Task<VideoClipboardCopyResult> CopyAsync(VideoCandidateAction selection, Guid copyOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (copyOperationId == Guid.Empty) { throw new ArgumentException("The copy operation ID must be nonempty.", nameof(copyOperationId)); }
        long generation = _execution.CurrentGeneration;
        VideoClipboardCopyResult Result(VideoClipboardCopyStatus status) => new(selection, copyOperationId, status) { Generation = generation };
        if (cancellationToken.IsCancellationRequested) { return Result(VideoClipboardCopyStatus.Cancelled); }
        if (!_session.TryResolveSelection(selection, out _)) { return Result(VideoClipboardCopyStatus.StaleSelection); }
        if (!_execution.TryBeginOperation(selection.SessionId, copyOperationId, out var operation, cancellationToken))
        {
            return Result(_execution.IsRunning ? VideoClipboardCopyStatus.Busy : VideoClipboardCopyStatus.StaleSelection);
        }

        generation = operation.Generation;
        using (operation)
        {
            try
            {
                VideoClipboardCopyStatus status = await _dispatcher.InvokeAsync(() =>
                    operation.TryClaimExecution() && _session.TryWriteSelection(selection, operation, _writer.SetText)
                        ? VideoClipboardCopyStatus.Copied : VideoClipboardCopyStatus.StaleSelection,
                    operation.CancellationToken).ConfigureAwait(false);
                return Result(status);
            }
            catch (OperationCanceledException) { return Result(VideoClipboardCopyStatus.Cancelled); }
        }
    }

    /// <summary>K5 must recheck this at presentation time, including after an awaited dispatcher hop.</summary>
    public bool IsFeedbackCurrent(VideoClipboardCopyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Generation == _execution.CurrentGeneration
            && (_execution.IsCurrent(result.CopyOperationId)
                || (result.Status == VideoClipboardCopyStatus.Busy && _execution.IsRunning))
            && _session.TryResolveSelection(result.Selection, out _);
    }
}

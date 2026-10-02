using VrcVa.Core;
using VrcVa.Windows.Video;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Rendering;

internal enum VrVoiceSearchAction { None, DirectSearch, InterpretedSearch, Previous, Next, Back, Close, Rerecord, Cancel, Retry, AdjustPlacement, Candidate1, Candidate2, Candidate3, Candidate4, Candidate5 }

internal sealed record VrVideoCard(VideoCandidate Candidate, VideoThumbnailResult? Thumbnail);

internal sealed record VrVoiceSearchSnapshot(
    TextInputSession? Input, VideoSearchResult? Result, VideoSearchFlowState State,
    string Message, string Query, string Failure, int PageIndex, int PageCount,
    IReadOnlyList<VrVideoCard> Cards, bool CanSearch, bool CanSelect, bool CanPrevious,
    bool CanNext, bool CanBack, bool CanRerecord, bool CanCancel, bool CanRetry,
    bool CanAdjustPlacement = false)
{
    public bool IsInput => Input is not null && Result is null && State is VideoSearchFlowState.Input or VideoSearchFlowState.Closed;
    public bool Allows(VrVoiceSearchAction action) => action switch
    {
        VrVoiceSearchAction.DirectSearch or VrVoiceSearchAction.InterpretedSearch => IsInput && CanSearch,
        VrVoiceSearchAction.Previous => CanPrevious,
        VrVoiceSearchAction.Next => CanNext,
        VrVoiceSearchAction.Back => CanBack,
        VrVoiceSearchAction.Close => true,
        VrVoiceSearchAction.Rerecord => CanRerecord,
        VrVoiceSearchAction.Cancel => CanCancel,
        VrVoiceSearchAction.Retry => CanRetry,
        VrVoiceSearchAction.AdjustPlacement => CanAdjustPlacement,
        >= VrVoiceSearchAction.Candidate1 and <= VrVoiceSearchAction.Candidate5 =>
            CanSelect && (int)action - (int)VrVoiceSearchAction.Candidate1 < Cards.Count,
        _ => false,
    };
}

internal sealed class VrVoiceSearchActionEventArgs(VrVoiceSearchSnapshot snapshot, VrVoiceSearchAction action) : EventArgs
{
    public VrVoiceSearchSnapshot Snapshot { get; } = snapshot;
    public VrVoiceSearchAction Action { get; } = action;
}

internal interface IVrVoiceSearchView
{
    event EventHandler? MicrophoneRequested;
    event EventHandler<VrVoiceSearchActionEventArgs>? VoiceSearchActionRequested;
    int MeasureTranscriptPages(string transcript);
    bool TryShowVoiceSearch(VrVoiceSearchSnapshot snapshot);
    bool TryShowVoicePlacementCalibration(VrVoiceSearchSnapshot snapshot) => false;
    void DismissVoiceSearch(VrVoiceSearchSnapshot snapshot);
}

/// <summary>A view of the existing desktop flows, never another session or provider owner.</summary>
internal sealed class VrVoiceSearchController : IDisposable
{
    private readonly ExecutionCoordinator _execution;
    private readonly VoiceInputFlow _voice;
    private readonly VideoSearchFlow _search;
    private readonly IVrVoiceSearchView _view;
    private VrVoiceSearchSnapshot? _shown;
    private TextInputSession? _pagedInput;
    private int _textPage;
    private int _textPageCount = 1;
    private bool _disposed;
    private bool _closing;
    private bool _dismissed;

    public VrVoiceSearchController(ExecutionCoordinator execution, VoiceInputFlow voice,
        VideoSearchFlow search, IVrVoiceSearchView view)
    {
        _execution = execution;
        _voice = voice;
        _search = search;
        _view = view;
        voice.Changed += Changed;
        search.Changed += Changed;
        view.MicrophoneRequested += MicrophoneRequested;
        view.VoiceSearchActionRequested += ActionRequested;
    }

    private void MicrophoneRequested(object? sender, EventArgs args)
    {
        if (_disposed || _closing || _search.RequiresRestart) { return; }
        if (_voice.CanStop) { _voice.StopRecording(); }
        else if (!_execution.IsRunning) { _voice.TryStart(); }
    }

    private void Changed(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _voice))
        {
            if (_voice.IsCurrent && _voice.State == VoiceFlowState.Recording) { _dismissed = false; }
            _search.Refresh();
        }
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || _closing) { return; }
        if (_dismissed) { Dismiss(); return; }
        TextInputSession? input = _voice.CurrentInput;
        if (!ReferenceEquals(_pagedInput, input))
        {
            _pagedInput = input;
            _textPage = 0;
            _textPageCount = input is null ? 1 : _view.MeasureTranscriptPages(input.Transcript);
        }
        if ((input is null || _voice.State != VoiceFlowState.Completed) && !_search.RequiresRestart)
        {
            Dismiss();
            return;
        }
        VideoSearchResult? result = _search.Result;
        bool isInput = input is not null && result is null && _search.State is VideoSearchFlowState.Input or VideoSearchFlowState.Closed;
        int pageCount = isInput ? _textPageCount : result?.PageCount ?? 1;
        _textPage = Math.Clamp(_textPage, 0, pageCount - 1);
        VrVoiceSearchSnapshot next = new(input, result, _search.State, _search.Message, _search.Query,
            _search.FailureCode is { } failure ? $"段階: {_search.FailureStage} / {failure}" : string.Empty,
            isInput ? _textPage : _search.PageIndex, pageCount,
            _search.Candidates.Select(candidate => new VrVideoCard(candidate, _search.ImageFor(candidate))).ToArray(),
            _search.CanSearch, _search.CanSelectCandidates,
            isInput ? _textPage > 0 && !_execution.IsRunning : _search.CanPrevious,
            isInput ? _textPage + 1 < pageCount && !_execution.IsRunning : _search.CanNext,
            !isInput && _search.CanSearch, !_execution.IsRunning && !_voice.RequiresRestart && !_search.RequiresRestart,
            _search.CanCancel, _search.CanRetry,
            CanAdjustPlacement: !_execution.IsRunning && !_search.RequiresRestart);
        if (_shown is { } previous && previous with { Cards = next.Cards } == next && previous.Cards.SequenceEqual(next.Cards)) { return; }
        _shown = next;
        try { _view.TryShowVoiceSearch(next); }
        catch (Exception) { /* Desktop controls remain available after a VR display failure. */ }
    }

    private void ActionRequested(object? sender, VrVoiceSearchActionEventArgs args)
    {
        if (_disposed || _closing) { return; }
        Refresh(); // re-check the shared gate, session and page at activation time
        if (!ReferenceEquals(args.Snapshot, _shown) || !_shown.Allows(args.Action)) { return; }
        switch (args.Action)
        {
            case VrVoiceSearchAction.DirectSearch: _search.TrySearch(false); break;
            case VrVoiceSearchAction.InterpretedSearch: _search.TrySearch(true); break;
            case VrVoiceSearchAction.Previous: MovePage(-1); break;
            case VrVoiceSearchAction.Next: MovePage(1); break;
            case VrVoiceSearchAction.Back: _search.BackToInput(); break;
            case VrVoiceSearchAction.Cancel: _search.Cancel(); break;
            case VrVoiceSearchAction.Retry: _search.TrySearch(false, retry: true); break;
            case VrVoiceSearchAction.Rerecord: _voice.TryStart(); _search.Refresh(); break;
            case VrVoiceSearchAction.Close: _ = CloseAsync(); break;
            case VrVoiceSearchAction.AdjustPlacement:
                _view.TryShowVoicePlacementCalibration(_shown);
                break;
            case >= VrVoiceSearchAction.Candidate1 and <= VrVoiceSearchAction.Candidate5:
                _search.TryCopy(_shown.Cards[(int)args.Action - (int)VrVoiceSearchAction.Candidate1].Candidate.CreateSelectionAction());
                break;
        }
        Refresh();
    }

    private void MovePage(int delta)
    {
        if (_shown!.IsInput) { _textPage += delta; }
        else { _search.MovePage(delta); }
    }

    public Task CloseAsync()
    {
        // Invalidate both owners before any await. Old thumbnail cleanup must not
        // close a new recording that starts while those images are draining.
        _closing = true;
        _dismissed = true;
        Dismiss();
        Task search = _search.CloseAsync();
        Task voice = _voice.CloseAsync();
        _pagedInput = null;
        _closing = false;
        return Task.WhenAll(search, voice);
    }

    private void Dismiss()
    {
        VrVoiceSearchSnapshot? previous = _shown;
        _shown = null;
        if (previous is not null)
        {
            try { _view.DismissVoiceSearch(previous); }
            catch (Exception) { /* Display ownership never controls flow cleanup. */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _voice.Changed -= Changed;
        _search.Changed -= Changed;
        _view.MicrophoneRequested -= MicrophoneRequested;
        _view.VoiceSearchActionRequested -= ActionRequested;
        Dismiss();
        _pagedInput = null;
    }
}

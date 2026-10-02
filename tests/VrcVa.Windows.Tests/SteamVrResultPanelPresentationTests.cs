using VrcVa.Core;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Video;

namespace VrcVa.Windows.Tests;

public sealed class SteamVrResultPanelPresentationTests
{
    [Fact]
    public void SameProgressOwner_CanReplaceVisibleCountdownWithoutHiding()
    {
        Guid sessionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        OperationProgressSnapshot current = Progress(sessionId, operationId, "10");
        OperationProgressSnapshot next = Progress(sessionId, operationId, "9");

        Assert.True(SteamVrResultPanel.CanUpdateVisiblePresentation(
            true, current, null, next, null));
        Assert.False(SteamVrResultPanel.CanUpdateVisiblePresentation(
            false, current, null, next, null));
        Assert.False(SteamVrResultPanel.CanUpdateVisiblePresentation(
            true, current, null, Progress(sessionId, Guid.NewGuid(), "9"), null));
        Assert.False(SteamVrResultPanel.CanUpdateVisiblePresentation(
            true, current, null, Progress(Guid.NewGuid(), operationId, "9"), null));
    }

    [Fact]
    public void SameVoiceSession_CanReplaceVisibleCopyFeedbackWithoutHiding()
    {
        Guid sessionId = Guid.NewGuid();
        VrVoiceSearchSnapshot candidates = Voice(sessionId, VideoSearchFlowState.Candidates, "select");
        VrVoiceSearchSnapshot copying = Voice(sessionId, VideoSearchFlowState.Copying, "copying");

        Assert.True(SteamVrResultPanel.CanUpdateVisiblePresentation(
            true, null, candidates, null, copying));
        Assert.False(SteamVrResultPanel.CanUpdateVisiblePresentation(
            true, null, candidates, null, Voice(Guid.NewGuid(), VideoSearchFlowState.Copying, "copying")));
        Assert.False(SteamVrResultPanel.CanUpdateVisiblePresentation(
            true, null, candidates, Progress(Guid.NewGuid(), Guid.NewGuid(), "new owner"), null));
    }

    private static OperationProgressSnapshot Progress(Guid sessionId, Guid operationId, string message) => new(
        sessionId,
        operationId,
        OperationProgressState.Recording,
        "recording",
        message,
        "detail",
        CanStop: true,
        CanCancel: true);

    private static VrVoiceSearchSnapshot Voice(Guid sessionId, VideoSearchFlowState state, string message) => new(
        new TextInputSession(sessionId, "synthetic transcript"),
        Result: null,
        State: state,
        Message: message,
        Query: string.Empty,
        Failure: string.Empty,
        PageIndex: 0,
        PageCount: 1,
        Cards: [],
        CanSearch: false,
        CanSelect: state == VideoSearchFlowState.Candidates,
        CanPrevious: false,
        CanNext: false,
        CanBack: false,
        CanRerecord: false,
        CanCancel: false,
        CanRetry: false);
}

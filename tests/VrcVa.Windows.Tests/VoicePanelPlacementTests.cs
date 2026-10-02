using System.Reflection;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Video;

namespace VrcVa.Windows.Tests;

public sealed class VoicePanelPlacementTests
{
    [Fact]
    public void SaveRequest_RequiresExplicitPersistenceAcceptance()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelPlacement placement = ResultPanelPlacement.Default with { YawDegrees = 25 };

        Assert.False(panel.TryAcceptVoicePlacementSave(placement, out string missingHandler));
        Assert.Contains("保存できませんでした", missingHandler, StringComparison.Ordinal);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);

        panel.VoicePlacementSaveRequested += (_, args) =>
            args.FailureMessage = "設定ファイルへ保存できませんでした";
        Assert.False(panel.TryAcceptVoicePlacementSave(placement, out string rejected));
        Assert.Equal("設定ファイルへ保存できませんでした", rejected);
        Assert.True(Get<bool>(panel, "_calibrationActive"));
        Assert.True(Get<bool>(panel, "_voicePlacementCalibrationActive"));

        using SteamVrResultPanel acceptedPanel = new(Dispatcher.CurrentDispatcher);
        ResultPanelPlacement? persisted = null;
        acceptedPanel.VoicePlacementSaveRequested += (_, args) =>
        {
            persisted = args.Placement;
            args.Accepted = true;
        };
        Assert.True(acceptedPanel.TryAcceptVoicePlacementSave(placement, out _));
        Assert.Equal(placement, persisted);
    }

    [Fact]
    public void NativeCloseDuringVoiceCalibration_RestoresPlacementAndClosesLatestOwner()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelImageUploadTracker tracker = Get<ResultPanelImageUploadTracker>(panel, "_imageUpload");
        ResultPanelTexture texture = Get<ResultPanelTexture>(panel, "_texture");
        WristLauncherStateMachine launcherState = Get<WristLauncherStateMachine>(panel, "_launcherState");
        ResultPanelPlacement original = ResultPanelPlacement.Default with { X = 0.12 };
        Guid owner = Guid.NewGuid();
        VrVoiceSearchSnapshot visible = Voice(owner, "visible before calibration");
        VrVoiceSearchSnapshot latest = Voice(owner, "latest calibration owner");
        texture.SetVoiceSearch(visible);
        launcherState.ShowResult();
        tracker.Begin(ResultPanelImageUploadKind.Calibration);
        Set(panel, "_placement", original with { X = 0.42 });
        Set(panel, "_visible", true);
        Set(panel, "_showAfterImageLoad", true);
        Set(panel, "_enableInteractionAfterImageLoad", true);
        Set(panel, "_calibrationOriginalPlacement", original);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", owner);
        Set(panel, "_voiceCalibrationSearch", visible);
        Set(panel, "_scanSessionActive", true);
        Set(panel, "_resultDesired", true);
        Assert.True((bool)Invoke(
            panel,
            "TryQueueVoiceCalibrationPresentation",
            null,
            latest)!);
        VrVoiceSearchActionEventArgs? requested = null;
        int saves = 0;
        panel.VoiceSearchActionRequested += (_, args) =>
        {
            requested = args;
            panel.DismissVoiceSearch(args.Snapshot);
        };
        panel.VoicePlacementSaveRequested += (_, _) => saves++;

        Invoke(panel, "HandleUserResultClose");

        Assert.Same(latest, requested!.Snapshot);
        Assert.Equal(VrVoiceSearchAction.Close, requested.Action);
        Assert.Equal(0, saves);
        Assert.Equal(original, Get<ResultPanelPlacement>(panel, "_placement"));
        Assert.False(Get<bool>(panel, "_calibrationActive"));
        Assert.False(Get<bool>(panel, "_voicePlacementCalibrationActive"));
        Assert.Null(Get<VrVoiceSearchSnapshot?>(panel, "_voiceCalibrationSearch"));
        Assert.Null(texture.VoiceSearch);
        Assert.False(Get<bool>(panel, "_showAfterImageLoad"));
        Assert.False(Get<bool>(panel, "_enableInteractionAfterImageLoad"));
        Assert.False(Get<bool>(panel, "_scanSessionActive"));
        Assert.False(Get<bool>(panel, "_resultDesired"));
        Assert.Equal(WristLauncherView.DimChip, launcherState.View);
        tracker.Complete();
        Assert.False(panel.TryApplyQueuedPresentation());
    }

    [Fact]
    public void HideDuringCalibrationUpload_DiscardsReturnAndDelayedImageCannotReshow()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelImageUploadTracker tracker = Get<ResultPanelImageUploadTracker>(panel, "_imageUpload");
        VrVoiceSearchSnapshot snapshot = Voice(Guid.NewGuid(), "pending");
        tracker.Begin(ResultPanelImageUploadKind.Calibration);
        Set(panel, "_visible", true);
        Set(panel, "_showAfterImageLoad", true);
        Set(panel, "_enableInteractionAfterImageLoad", true);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", snapshot.Input!.SessionId);
        Set(panel, "_voiceCalibrationSearch", snapshot);

        panel.Hide();
        Assert.True(tracker.InFlight);
        tracker.Complete();

        Assert.False(panel.TryApplyQueuedPresentation());
        Assert.False(Get<bool>(panel, "_showAfterImageLoad"));
        Assert.False(Get<bool>(panel, "_enableInteractionAfterImageLoad"));
        Assert.False(Get<bool>(panel, "_calibrationActive"));
        Assert.Null(Get<VrVoiceSearchSnapshot?>(panel, "_voiceCalibrationSearch"));
    }

    [Fact]
    public void PresentationFailure_AbandonsUnsavedPlacementAndQueuedOwner()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelPlacement original = ResultPanelPlacement.Default with { RollDegrees = -20 };
        VrVoiceSearchSnapshot snapshot = Voice(Guid.NewGuid(), "queued");
        Set(panel, "_placement", original with { RollDegrees = 35 });
        Set(panel, "_calibrationOriginalPlacement", original);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", snapshot.Input!.SessionId);
        Set(panel, "_voiceCalibrationSearch", snapshot);

        Invoke(panel, "Disconnect");

        Assert.Equal(original, Get<ResultPanelPlacement>(panel, "_placement"));
        Assert.False(Get<bool>(panel, "_calibrationActive"));
        Assert.False(Get<bool>(panel, "_voicePlacementCalibrationActive"));
        Assert.Null(Get<VrVoiceSearchSnapshot?>(panel, "_voiceCalibrationSearch"));
    }

    [Fact]
    public void DifferentSessionCannotInheritActiveCalibration()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        Guid owner = Guid.NewGuid();
        ResultPanelPlacement original = ResultPanelPlacement.Default with { Y = 0.08 };
        Set(panel, "_placement", original with { Y = 0.28 });
        Set(panel, "_calibrationOriginalPlacement", original);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", owner);
        Set(panel, "_voiceCalibrationSearch", Voice(owner, "owner"));
        VrVoiceSearchSnapshot replacement = Voice(Guid.NewGuid(), "replacement");

        bool queued = (bool)Invoke(
            panel,
            "TryQueueVoiceCalibrationPresentation",
            null,
            replacement)!;

        Assert.False(queued);
        Assert.Equal(original, Get<ResultPanelPlacement>(panel, "_placement"));
        Assert.False(Get<bool>(panel, "_voicePlacementCalibrationActive"));
        Assert.Null(Get<VrVoiceSearchSnapshot?>(panel, "_voiceCalibrationSearch"));
    }

    [Fact]
    public void SameSessionQueuesLatestPresentationWithoutLeavingCalibration()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        Guid owner = Guid.NewGuid();
        VrVoiceSearchSnapshot latest = Voice(owner, "latest");
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", owner);
        Set(panel, "_voiceCalibrationSearch", Voice(owner, "old"));

        bool queued = (bool)Invoke(
            panel,
            "TryQueueVoiceCalibrationPresentation",
            null,
            latest)!;

        Assert.True(queued);
        Assert.True(Get<bool>(panel, "_voicePlacementCalibrationActive"));
        Assert.Same(latest, Get<VrVoiceSearchSnapshot?>(panel, "_voiceCalibrationSearch"));
    }

    [Fact]
    public void RefreshedCalibrationOwnerDismissal_ClearsDelayedUploadAndIgnoresLateSaveOrCancel()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelImageUploadTracker tracker = Get<ResultPanelImageUploadTracker>(panel, "_imageUpload");
        Guid owner = Guid.NewGuid();
        ResultPanelPlacement original = ResultPanelPlacement.Default with { Z = -1.4 };
        VrVoiceSearchSnapshot latest = Voice(owner, "thumbnail refresh");
        tracker.Begin(ResultPanelImageUploadKind.Calibration);
        Set(panel, "_placement", original with { Z = -1.1 });
        Set(panel, "_calibrationOriginalPlacement", original);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", owner);
        Set(panel, "_voiceCalibrationSearch", Voice(owner, "before refresh"));
        Set(panel, "_visible", true);
        Set(panel, "_showAfterImageLoad", true);
        Set(panel, "_enableInteractionAfterImageLoad", true);
        int saves = 0;
        panel.VoicePlacementSaveRequested += (_, _) => saves++;

        Assert.True((bool)Invoke(
            panel,
            "TryQueueVoiceCalibrationPresentation",
            null,
            latest)!);
        panel.DismissVoiceSearch(latest);

        Assert.Equal(original, Get<ResultPanelPlacement>(panel, "_placement"));
        Assert.False(Get<bool>(panel, "_calibrationActive"));
        Assert.False(Get<bool>(panel, "_voicePlacementCalibrationActive"));
        Assert.Null(Get<VrVoiceSearchSnapshot?>(panel, "_voiceCalibrationSearch"));
        Assert.False(Get<bool>(panel, "_showAfterImageLoad"));
        Assert.False(Get<bool>(panel, "_enableInteractionAfterImageLoad"));
        tracker.Complete();
        Assert.False(panel.TryApplyQueuedPresentation());

        Invoke(panel, "HandlePlacementCalibrationClick", 922f, 627f);
        Invoke(panel, "HandlePlacementCalibrationClick", 483f, 627f);
        Assert.Equal(0, saves);
        Assert.Equal(original, Get<ResultPanelPlacement>(panel, "_placement"));
    }

    [Fact]
    public void SameSessionBusyUpdate_EndsCalibrationAndPassesPresentationThrough()
    {
        Guid owner = Guid.NewGuid();
        ResultPanelPlacement original = ResultPanelPlacement.Default with { YawDegrees = -15 };
        using SteamVrResultPanel progressPanel = CreateActiveCalibrationPanel(owner, original);
        OperationProgressSnapshot transcribing = new(
            owner,
            Guid.NewGuid(),
            OperationProgressState.Transcribing,
            "文字起こし中",
            "送信済み",
            "中止できます",
            CanCancel: true,
            UsesVoicePlacement: true);

        Assert.False((bool)Invoke(
            progressPanel,
            "TryQueueVoiceCalibrationPresentation",
            transcribing,
            null)!);
        Assert.Equal(original, Get<ResultPanelPlacement>(progressPanel, "_placement"));
        Assert.False(Get<bool>(progressPanel, "_voicePlacementCalibrationActive"));

        using SteamVrResultPanel searchPanel = CreateActiveCalibrationPanel(owner, original);
        VrVoiceSearchSnapshot runningSearch = Voice(owner, "検索中") with
        {
            CanAdjustPlacement = false,
        };

        Assert.False((bool)Invoke(
            searchPanel,
            "TryQueueVoiceCalibrationPresentation",
            null,
            runningSearch)!);
        Assert.Equal(original, Get<ResultPanelPlacement>(searchPanel, "_placement"));
        Assert.False(Get<bool>(searchPanel, "_voicePlacementCalibrationActive"));
    }

    [Fact]
    public void RejectedCalibrationStart_RestoresPreviousPresentationAndInteraction()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelPlacement previous = ResultPanelPlacement.Default with { X = 0.17 };
        ResultPanelPlacement? restored = null;
        int shows = 0;

        panel.RestorePresentationAfterRejectedCalibration(
            previous,
            previousUsesVoicePlacement: true,
            wasVisible: true,
            wasInteractive: true,
            placement => restored = placement,
            () => shows++);

        Assert.Equal(previous, restored);
        Assert.Equal(previous, Get<ResultPanelPlacement>(panel, "_placement"));
        Assert.True(Get<bool>(panel, "_currentUsesVoicePlacement"));
        Assert.True(Get<bool>(panel, "_visible"));
        Assert.True(Get<bool>(panel, "_interactive"));
        Assert.Equal(1, shows);
    }

    [Fact]
    public void SizeLabels_MatchFixedSixCentimeterSteps()
    {
        Assert.Contains(
            ResultPanelTexture.CalibrationControls,
            control => control.Action == ResultPanelCalibrationAction.MakeSmaller
                && control.Label == "小さく  −6cm");
        Assert.Contains(
            ResultPanelTexture.CalibrationControls,
            control => control.Action == ResultPanelCalibrationAction.MakeLarger
                && control.Label == "大きく  ＋6cm");
    }

    private static SteamVrResultPanel CreateActiveCalibrationPanel(
        Guid owner,
        ResultPanelPlacement original)
    {
        SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        Set(panel, "_placement", original with { YawDegrees = original.YawDegrees + 20 });
        Set(panel, "_calibrationOriginalPlacement", original);
        Set(panel, "_calibrationActive", true);
        Set(panel, "_voicePlacementCalibrationActive", true);
        Set(panel, "_voicePlacementCalibrationSessionId", owner);
        Set(panel, "_voiceCalibrationSearch", Voice(owner, "adjusting"));
        return panel;
    }

    private static VrVoiceSearchSnapshot Voice(Guid sessionId, string message) => new(
        new TextInputSession(sessionId, "synthetic transcript"),
        Result: null,
        State: VideoSearchFlowState.Input,
        Message: message,
        Query: string.Empty,
        Failure: string.Empty,
        PageIndex: 0,
        PageCount: 1,
        Cards: [],
        CanSearch: true,
        CanSelect: false,
        CanPrevious: false,
        CanNext: false,
        CanBack: false,
        CanRerecord: true,
        CanCancel: false,
        CanRetry: false,
        CanAdjustPlacement: true);

    private static object? Invoke(object owner, string name, params object?[]? arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, arguments);

    private static void Set<T>(object owner, string name, T value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, value);

    private static T Get<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
}

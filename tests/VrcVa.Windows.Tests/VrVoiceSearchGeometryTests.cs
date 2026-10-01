using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Video;

namespace VrcVa.Windows.Tests;

public sealed class VrVoiceSearchGeometryTests
{
    [Theory]
    [InlineData((int)VideoSearchFlowState.Input)]
    [InlineData((int)VideoSearchFlowState.Interpreting)]
    [InlineData((int)VideoSearchFlowState.Searching)]
    [InlineData((int)VideoSearchFlowState.Candidates)]
    [InlineData((int)VideoSearchFlowState.Copying)]
    [InlineData((int)VideoSearchFlowState.Cancelling)]
    [InlineData((int)VideoSearchFlowState.Failed)]
    public async Task AllPagesAndControls_RawNativeIntersectionToLogicalAction_CentersEdgesCornersNeighborsAndHeldHover(int state)
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new() { TranscriptPages = 2 };
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            VrVoiceSearchSnapshot input = view.Current!;
            view.Activate(VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            for (int page = 0; page < 2; page++)
            {
                VrVoiceSearchSnapshot candidates = view.Current!;
                VrVoiceSearchSnapshot snapshot = (VideoSearchFlowState)state switch
                {
                    VideoSearchFlowState.Input => input with { PageIndex = page, CanPrevious = page > 0, CanNext = page == 0 },
                    VideoSearchFlowState.Candidates => candidates,
                    VideoSearchFlowState.Copying => candidates with
                    {
                        State = VideoSearchFlowState.Copying,
                        CanSelect = false,
                        CanPrevious = false,
                        CanNext = false,
                        CanBack = false,
                        CanRerecord = false,
                        CanCancel = true
                    },
                    _ => input with
                    {
                        State = (VideoSearchFlowState)state,
                        PageCount = 1,
                        CanSearch = state == (int)VideoSearchFlowState.Failed,
                        CanPrevious = false,
                        CanNext = false,
                        CanBack = state == (int)VideoSearchFlowState.Failed,
                        CanRerecord = state == (int)VideoSearchFlowState.Failed,
                        CanRetry = state == (int)VideoSearchFlowState.Failed,
                        CanCancel = state is (int)VideoSearchFlowState.Searching or (int)VideoSearchFlowState.Interpreting
                    },
                };
                AssertGeometry(snapshot);
                // Explicitly disabled controls still own exactly their visible area.
                AssertGeometry(snapshot with
                {
                    CanSearch = false,
                    CanSelect = false,
                    CanPrevious = false,
                    CanNext = false,
                    CanBack = false,
                    CanRerecord = false,
                    CanCancel = false,
                    CanRetry = false
                });
                if (page == 0) { view.Activate(VrVoiceSearchAction.Next); }
            }

        });
    }

    private static void AssertGeometry(VrVoiceSearchSnapshot snapshot)
    {
        ResultPanelTexture texture = new();
        texture.SetVoiceSearch(snapshot);
        OverlayTextureView view = OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720));
        foreach (VrVoiceSearchControl control in ResultPanelTexture.VoiceSearchControls(snapshot))
        {
            Assert.True(control.Bounds.Top > ResultPanelTexture.HeaderHeight);
            foreach (float x in Coordinates(control.Bounds.Left, control.Bounds.Right))
            {
                foreach (float y in Coordinates(control.Bounds.Top, control.Bounds.Bottom))
                {
                    OpenVrIntersection intersection = Map(view, x, y);
                    Assert.InRange(Math.Abs(intersection.LocalPoint.X - x), 0, 0.001f);
                    Assert.InRange(Math.Abs(intersection.LocalPoint.Y - y), 0, 0.001f);
                    VrVoiceSearchAction expected = control.Bounds.Contains(x, y) && control.Enabled ? control.Action : VrVoiceSearchAction.None;
                    VrVoiceSearchAction action = texture.HitTestVoiceSearch(intersection.LocalPoint.X, intersection.LocalPoint.Y);
                    Assert.Equal(expected, action);
                    Assert.Equal(ResultPanelAction.None, texture.HitTestResult(x, y));
                    Assert.Equal(OperationProgressAction.None, texture.HitTestProgress(x, y));
                    Assert.False(texture.IsScrollbar(x, y));
                    int target = action == VrVoiceSearchAction.None ? 0 : 600 + (int)action;
                    PointerActivationGate gate = new();
                    Assert.Equal(0, gate.Update(target, true, true, true));
                    Assert.Equal(0, gate.Update(target, true, true, false));
                    Assert.Equal(0, gate.Update(target, true, false, true));
                    Assert.Equal(target, gate.Update(target, true, true, true));
                    Assert.Equal(0, gate.Update(target, true, true, true));
                    // View replacement and newly enabled targets require another release.
                    gate.Reset();
                    Assert.Equal(0, gate.Update(target, true, true, true));
                }
            }
        }
        for (int x = 0; x <= 1280; x += 32)
        {
            for (int y = 0; y <= ResultPanelTexture.HeaderHeight; y += 18)
            {
                OpenVrIntersection intersection = Map(view, x, y);
                Assert.Equal(VrVoiceSearchAction.None, texture.HitTestVoiceSearch(intersection.LocalPoint.X, intersection.LocalPoint.Y));
            }
        }
        Assert.False(texture.IsScrollbar(1230, 600));
    }

    [Fact]
    public async Task FullViewMappedActionsActuallyDriveBothCandidatePagesAndInputControls()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new() { TranscriptPages = 2 };
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            void Click(VrVoiceSearchAction action)
            {
                VrVoiceSearchSnapshot snapshot = view.Current!;
                VrVoiceSearchControl control = Assert.Single(ResultPanelTexture.VoiceSearchControls(snapshot), item => item.Action == action);
                ResultPanelTexture texture = new();
                texture.SetVoiceSearch(snapshot);
                OpenVrIntersection intersection = Map(OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720)),
                    (float)(control.Bounds.Left + control.Bounds.Width / 2), (float)(control.Bounds.Top + control.Bounds.Height / 2));
                view.Activate(texture.HitTestVoiceSearch(intersection.LocalPoint.X, intersection.LocalPoint.Y), snapshot);
            }
            Click(VrVoiceSearchAction.Next);
            Assert.Equal(1, view.Current!.PageIndex);
            Click(VrVoiceSearchAction.Previous);
            Click(VrVoiceSearchAction.InterpretedSearch);
            await fixture.Flow.WhenIdle;
            for (int page = 0; page < 2; page++)
            {
                for (int index = 0; index < 5; index++)
                {
                    string expected = view.Current!.Cards[index].Candidate.WatchUrl.AbsoluteUri;
                    Click(VrVoiceSearchAction.Candidate1 + index);
                    await fixture.Flow.WhenIdle;
                    Assert.Equal(expected, fixture.Writes[^1]);
                }
                if (page == 0) { Click(VrVoiceSearchAction.Next); }
            }
            Click(VrVoiceSearchAction.Previous);
            Click(VrVoiceSearchAction.Back);
            Click(VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            Assert.Equal(2, fixture.Searches);
            Assert.Equal(1, fixture.Interpretations);
            Click(VrVoiceSearchAction.Close);
            Assert.Null(view.Current);

        });
    }

    [Fact]
    public async Task MappedRetryCancelRerecordAndMicrophoneStop_InvokeSharedLifecycleActions()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using OperationProgressController progress = new(fixture.Voice.Execution, view);
            progress.AttachVoice(fixture.Voice.Flow);
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            fixture.SearchResponse = (_, _) => throw new ScanException(ScanFailureCode.VideoSearchTimedOut, ScanStage.TextHandling, "synthetic failure");
            ClickMapped(view, VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            ClickMapped(view, VrVoiceSearchAction.Retry);
            await fixture.Flow.WhenIdle;
            Assert.Equal(2, fixture.Searches);
            ClickMapped(view, VrVoiceSearchAction.Back);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<VideoSearchBatch> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.SearchResponse = (_, _) => { entered.TrySetResult(); return response.Task; };
            ClickMapped(view, VrVoiceSearchAction.InterpretedSearch);
            await entered.Task;
            ClickMapped(view, VrVoiceSearchAction.Cancel);
            Assert.Equal(VideoSearchFlowState.Cancelling, view.Current!.State);
            response.SetResult(SearchFlowFixture.Batch());
            await fixture.Flow.WhenIdle;
            fixture.Voice.NextMicrophone();
            ClickMapped(view, VrVoiceSearchAction.Rerecord);
            await fixture.Voice.Microphone.Started.Task;
            progress.Refresh();
            ResultPanelTexture recording = new();
            recording.SetProgress(view.Progress!);
            OperationProgressButton stop = Assert.Single(ResultPanelTexture.ProgressControls(view.Progress!), item => item.Action == OperationProgressAction.Stop);
            OpenVrIntersection point = Map(OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720)),
                (float)(stop.Bounds.Left + stop.Bounds.Width / 2), (float)(stop.Bounds.Top + stop.Bounds.Height / 2));
            view.ActivateProgress(recording.HitTestProgress(point.LocalPoint.X, point.LocalPoint.Y));
            await fixture.Voice.Flow.WhenIdle;
            Assert.Equal(2, fixture.Voice.Requests);
            Assert.True(view.Current!.IsInput);
            ClickMapped(view, VrVoiceSearchAction.Close);
            Assert.Null(view.Current);
        });
    }

    private static void ClickMapped(VrSearchView view, VrVoiceSearchAction action)
    {
        VrVoiceSearchSnapshot snapshot = view.Current!;
        VrVoiceSearchControl control = Assert.Single(ResultPanelTexture.VoiceSearchControls(snapshot), item => item.Action == action);
        ResultPanelTexture texture = new();
        texture.SetVoiceSearch(snapshot);
        OpenVrIntersection point = Map(OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720)),
            (float)(control.Bounds.Left + control.Bounds.Width / 2), (float)(control.Bounds.Top + control.Bounds.Height / 2));
        view.Activate(texture.HitTestVoiceSearch(point.LocalPoint.X, point.LocalPoint.Y), snapshot);
    }

    [Fact]
    public void LauncherEveryInteractiveCell_UsesExactAtlasViewAndSharedGeometry()
    {
        WristLauncherTexture texture = new();
        foreach (WristLauncherAction hover in Enum.GetValues<WristLauncherAction>())
        {
            int cell = texture.GetAtlasCell(WristLauncherView.Menu, hover);
            OverlayTextureView view = OverlayTextureView.CreateAtlasCell(new OverlaySurfaceSpec(800, 400), cell, 4, 2);
            (WristLauncherAction Action, Rect Bounds)[] buttons =
            [
                (WristLauncherAction.Translate, WristLauncherTexture.TranslationButtonBounds),
                (WristLauncherAction.Microphone, WristLauncherTexture.MicrophoneButtonBounds),
                (WristLauncherAction.Calibrate, WristLauncherTexture.CalibrationButtonBounds),
                (WristLauncherAction.CloseMenu, WristLauncherTexture.CloseMenuButtonBounds),
            ];
            foreach (var button in buttons)
            {
                foreach (float x in Coordinates(button.Bounds.Left, button.Bounds.Right))
                {
                    foreach (float y in Coordinates(button.Bounds.Top, button.Bounds.Bottom))
                    {
                        OpenVrIntersection intersection = Map(view, x, y);
                        WristLauncherAction action = texture.HitTest(WristLauncherView.Menu, intersection.LocalPoint.X, intersection.LocalPoint.Y);
                        Assert.Equal(button.Bounds.Contains(x, y) ? button.Action : WristLauncherAction.None, action);
                        PointerActivationGate gate = new();
                        int target = action == WristLauncherAction.None ? 0 : 300 + (int)action;
                        Assert.Equal(0, gate.Update(target, true, true, true));
                        Assert.Equal(0, gate.Update(target, true, false, true));
                        Assert.Equal(target, gate.Update(target, true, true, true));
                    }
                }
            }
        }
        Assert.Equal(7, texture.GetAtlasCell(WristLauncherView.Menu, WristLauncherAction.Microphone));
        WristLauncherStateMachine state = new();
        Assert.False(state.CanRequestMicrophone);
        state.Start(); Assert.False(state.CanRequestMicrophone);
        state.ExpandMenu(); Assert.True(state.CanRequestMicrophone);
        state.BeginCalibration(); Assert.False(state.CanRequestMicrophone);
        state.EndCalibration(); state.ExpandMenu(); state.BeginScan(); Assert.False(state.CanRequestMicrophone);
        state.ShowResult(); Assert.False(state.CanRequestMicrophone);
        state.ReturnToChip(); state.ExpandMenu(); Assert.True(state.CanRequestMicrophone);
        state.Stop(); Assert.False(state.CanRequestMicrophone);
    }

    [Fact]
    public async Task LongTranscriptTitlesAndQuery_RenderFullPagesWithoutMovingControls()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            fixture.Voice.Response = (_, _) => Task.FromResult(VoiceFlowFixture.Success(string.Concat(Enumerable.Repeat("長文😀 synthetic words\n", 80))));
            VideoSearchBatch longTitles = SearchFlowFixture.Batch(title: string.Concat(Enumerable.Repeat("長い title😀 ", 160)));
            fixture.SearchResponse = (_, _) => Task.FromResult(longTitles);
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            ResultPanelTexture texture = new();
            int pages = texture.MeasureTranscriptPages(view.Current!.Input!.Transcript);
            Assert.True(pages > 3);
            for (int page = 0; page < pages; page++)
            {
                texture.SetVoiceSearch(view.Current with { PageIndex = page, PageCount = pages });
                Assert.Equal(1280 * 720 * 4, texture.RenderCurrentResultRgba().Length);
                Assert.Same(view.Current.Input, texture.VoiceSearch!.Input);
            }
            view.Activate(VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            Assert.Equal(VideoSearchFlowState.Candidates, view.Current!.State);
            Assert.Equal(10, view.Current.Result!.Candidates.Count);
            foreach (int page in new[] { 0, 1 })
            {
                Assert.Equal(5, view.Current.Cards.Count);
                texture.SetVoiceSearch(view.Current!);
                Assert.Equal(1280 * 720 * 4, texture.RenderCurrentResultRgba().Length);
                Assert.Equal(12, ResultPanelTexture.VoiceSearchControls(view.Current!).Count);
                if (page == 0) { view.Activate(VrVoiceSearchAction.Next); }
            }
            texture.SetContent("translation", "body");
            Assert.Null(texture.VoiceSearch);
            Assert.Equal(VrVoiceSearchAction.None, texture.HitTestVoiceSearch(1100, 620));

        });
    }

    [Fact]
    public async Task QueuedReplacementOwnsSurfaceAndOldHidesCannotEraseIt_CloseDiscardsDelayedImage()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
            ResultPanelTexture texture = Field<ResultPanelTexture>(panel, "_texture");
            ResultPanelImageUploadTracker tracker = Field<ResultPanelImageUploadTracker>(panel, "_imageUpload");
            VrVoiceSearchSnapshot old = view.Current!;
            texture.SetVoiceSearch(old);
            tracker.Begin(ResultPanelImageUploadKind.ResultPage);
            OperationProgressSnapshot scan = new(Guid.NewGuid(), Guid.NewGuid(), OperationProgressState.Processing, "SCAN", "capture", "");
            panel.QueuePresentation(scan.Title, scan.Message, scan);
            panel.DismissVoiceSearch(old);
            Assert.Null(texture.VoiceSearch);
            Assert.True(panel.TryApplyQueuedPresentation());
            Assert.Same(scan, texture.Progress);
            panel.QueuePresentation("", "", null, old);
            panel.DismissProgress();
            Assert.True(panel.TryApplyQueuedPresentation());
            Assert.Same(old, texture.VoiceSearch);
            VrVoiceSearchSnapshot replacement = old with { Message = "new snapshot" };
            panel.QueuePresentation("", "", null, replacement);
            VrVoiceSearchActionEventArgs? nativeClose = null;
            panel.VoiceSearchActionRequested += (_, args) => nativeClose = args;
            typeof(SteamVrResultPanel).GetMethod("HandleUserResultClose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            Assert.Same(replacement, nativeClose!.Snapshot);
            Assert.Equal(VrVoiceSearchAction.Close, nativeClose.Action);
            panel.DismissVoiceSearch(replacement);
            Assert.Null(texture.VoiceSearch);
            Assert.True(tracker.InFlight);
            tracker.Complete();
            Assert.False(panel.TryApplyQueuedPresentation());
            Assert.False(Field<bool>(panel, "_showAfterImageLoad"));
            Assert.False(Field<bool>(panel, "_enableInteractionAfterImageLoad"));

        });
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static float[] Coordinates(double left, double right) => [(float)left - 1, (float)left, (float)left + 1, (float)((left + right) / 2), (float)right - 1, (float)right, (float)right + 1];
    private static OpenVrIntersection Map(OverlayTextureView view, float x, float y)
    {
        OverlayTextureBounds bounds = view.UpperLeftTextureBounds;
        OverlayLocalPoint raw = new(bounds.UMin + x / view.Surface.LogicalWidth * (bounds.UMax - bounds.UMin),
            1 - (bounds.VMin + y / view.Surface.LogicalHeight * (bounds.VMax - bounds.VMin)));
        OpenVrNativeIntersection native = new(raw, new(1, 2, 3), new(0, 0, -1), 0.5f);
        Assert.True(OpenVrInterop.TryMapNativeIntersection(new(new(0, 0, 0), new(0.6f, 0, -0.8f)), view, native,
            out OpenVrIntersection intersection, out OpenVrIntersectionAttempt attempt));
        Assert.Equal(OpenVrIntersectionOutcome.Hit, attempt.Outcome);
        OpenVrAbsoluteTransform cursor = OpenVrAbsoluteTransform.CreateCursor(intersection);
        Assert.Equal(native.Point.X, cursor.M3);
        Assert.Equal(native.Point.Y, cursor.M7);
        Assert.Equal(native.Point.Z, cursor.M11);
        return intersection;
    }
}

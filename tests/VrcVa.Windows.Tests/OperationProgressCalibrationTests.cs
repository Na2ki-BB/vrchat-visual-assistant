using System.Reflection;
using System.Windows.Threading;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;

namespace VrcVa.Windows.Tests;

public sealed class OperationProgressCalibrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressCancelsUnsavedCalibration_ButRetainsInFlightTextureOwnership(bool wrist)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
                ResultPanelImageUploadTracker tracker = Get<ResultPanelImageUploadTracker>(panel, "_imageUpload");
                tracker.Begin(ResultPanelImageUploadKind.Calibration);
                Set(panel, wrist ? "_launcherCalibrationActive" : "_calibrationActive", true);
                Set(panel, "_showAfterImageLoad", true);
                Set(panel, "_enableInteractionAfterImageLoad", true);
                bool finished = false;
                if (wrist)
                {
                    WristLauncherPlacement original = WristLauncherPlacement.Default;
                    Set(panel, "_launcherCalibrationOriginalPlacement", original);
                    panel.WristLauncherPlacementCalibrationFinished += (_, result) =>
                    {
                        Assert.False(result.SaveRequested);
                        Assert.Equal(original, result.Placement);
                        finished = true;
                    };
                }
                else
                {
                    ResultPanelPlacement original = ResultPanelPlacement.Default;
                    Set(panel, "_calibrationOriginalPlacement", original);
                    panel.PlacementCalibrationFinished += (_, result) =>
                    {
                        Assert.False(result.SaveRequested);
                        Assert.Equal(original, result.Placement);
                        finished = true;
                    };
                }
                panel.PrepareProgressPresentation();
                Assert.True(finished);
                Assert.False(Get<bool>(panel, "_launcherCalibrationActive"));
                Assert.False(Get<bool>(panel, "_calibrationActive"));
                Assert.False(Get<bool>(panel, "_interactive"));
                Assert.False(Get<bool>(panel, "_showAfterImageLoad"));
                Assert.False(Get<bool>(panel, "_enableInteractionAfterImageLoad"));
                Assert.True(tracker.InFlight);
                Assert.Equal(ResultPanelImageUploadKind.Calibration, tracker.Complete());
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        thread.Join();
    }

    [Fact]
    public void DeferredImageLoaded_AppliesLatestCancellationInsteadOfQueuedResult_AndHideDiscardsIt()
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        ResultPanelImageUploadTracker tracker = Get<ResultPanelImageUploadTracker>(panel, "_imageUpload");
        tracker.Begin(ResultPanelImageUploadKind.ResultPage);
        panel.QueuePresentation("old result", "private old result", null);
        OperationProgressSnapshot cancelled = new(Guid.NewGuid(), Guid.NewGuid(), OperationProgressState.Cancelling,
            "中止中", "回収中", "detail");
        panel.QueuePresentation(cancelled.Title, cancelled.Message, cancelled);
        Assert.False(Get<bool>(panel, "_showAfterImageLoad"));
        Assert.False(Get<bool>(panel, "_enableInteractionAfterImageLoad"));
        Assert.Equal(ResultPanelImageUploadKind.ResultPage, tracker.Complete());
        Assert.True(panel.TryApplyQueuedPresentation());
        Assert.Same(cancelled, Get<ResultPanelTexture>(panel, "_texture").Progress);
        Assert.False(panel.TryApplyQueuedPresentation());
        tracker.Begin(ResultPanelImageUploadKind.ResultPage);
        panel.QueuePresentation(cancelled.Title, cancelled.Message, cancelled);
        panel.Hide();
        Assert.True(tracker.InFlight); // owned upload still drains; it cannot resurrect the hidden view
        tracker.Complete();
        Assert.False(panel.TryApplyQueuedPresentation());
        Assert.False(Get<bool>(panel, "_showAfterImageLoad"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCloseDuringLaterCalibration_DoesNotDispatchStaleProgressAction(bool wrist)
    {
        using SteamVrResultPanel panel = new(Dispatcher.CurrentDispatcher);
        Get<ResultPanelTexture>(panel, "_texture").SetProgress(new(Guid.NewGuid(), Guid.NewGuid(),
            OperationProgressState.Failed, "old", "old", "old", CanClose: true));
        panel.Hide(); // retained content is not a live progress presentation
        Set(panel, wrist ? "_launcherCalibrationActive" : "_calibrationActive", true);
        int oldProgressActions = 0;
        panel.ProgressActionRequested += (_, _) => oldProgressActions++;
        typeof(SteamVrResultPanel).GetMethod("HandleUserResultClose", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        Assert.Equal(0, oldProgressActions);
        Assert.False(Get<bool>(panel, "_calibrationActive"));
        Assert.False(Get<bool>(panel, "_launcherCalibrationActive"));
    }

    private static void Set<T>(SteamVrResultPanel panel, string name, T value) =>
        typeof(SteamVrResultPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, value);
    private static T Get<T>(SteamVrResultPanel panel, string name) =>
        (T)typeof(SteamVrResultPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
}

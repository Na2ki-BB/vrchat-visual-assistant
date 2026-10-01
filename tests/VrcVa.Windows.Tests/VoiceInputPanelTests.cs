using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class VoiceInputPanelTests
{
    [Fact]
    public Task WpfWithoutVr_CanOptInSaveFakeKeyRecordDisplayFullTextAndClose() => OnDispatcher(async () =>
    {
        await using VoiceFlowFixture fixture = new(enabled: false);
        VoiceInputPanel panel = CreatePanel(fixture);
        try
        {
            Button record = Find<Button>(panel, "RecordButton");
            Assert.False(record.IsEnabled);
            PasswordBox key = Find<PasswordBox>(panel, "VoiceKeyBox");
            key.Password = "synthetic-ui-key";
            Click(Find<Button>(panel, "SaveKeyButton"));
            Assert.Equal(string.Empty, key.Password);
            Assert.False(fixture.Settings.VoiceInput.IsEnabled);
            Assert.Equal(0, fixture.Opens);
            CheckBox consent = Find<CheckBox>(panel, "ConsentCheckBox");
            consent.IsChecked = true;
            Click(consent);
            Assert.True(record.IsEnabled);
            Assert.Equal(0, fixture.Opens);
            Click(record);
            await fixture.Microphone.Started.Task;
            Assert.True(Find<Button>(panel, "CancelVoiceButton").IsEnabled);
            Assert.False(consent.IsEnabled);
            Click(record);
            await fixture.Flow.WhenIdle;
            Assert.Equal("synthetic transcript", Find<TextBox>(panel, "TranscriptBox").Text);
            Assert.True(Find<TextBox>(panel, "TranscriptBox").IsReadOnly);
            Assert.Null(panel.ResultActions.Content); // no public search UI before K5
            Assert.Equal("synthetic transcript", panel.CurrentInput!.Transcript);
            Click(Find<Button>(panel, "CloseVoiceButton"));
            await fixture.Flow.WhenIdle;
            Assert.Empty(Find<TextBox>(panel, "TranscriptBox").Text);
            Assert.Null(panel.CurrentInput);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public Task CancelRemainsReachableWhileRecording_RecoveryAndLongTextLayoutDoNotRequireVr() => OnDispatcher(async () =>
    {
        await using VoiceFlowFixture fixture = new();
        VoiceInputPanel panel = CreatePanel(fixture);
        try
        {
            Click(Find<Button>(panel, "RecordButton"));
            await fixture.Microphone.Started.Task;
            Click(Find<Button>(panel, "CancelVoiceButton"));
            await fixture.Flow.WhenIdle;
            Assert.Equal(0, fixture.Requests);
            Assert.True(Find<Button>(panel, "RecordButton").IsEnabled);
            fixture.NextMicrophone();
            string full = new('a', 3900);
            fixture.Response = (_, _) => Task.FromResult(VoiceFlowFixture.Success(full));
            Click(Find<Button>(panel, "RecordButton"));
            await fixture.Microphone.Started.Task;
            Click(Find<Button>(panel, "RecordButton"));
            await fixture.Flow.WhenIdle;
            panel.Measure(new Size(600, 500));
            panel.Arrange(new Rect(0, 0, 600, 500));
            panel.UpdateLayout();
            TextBox transcript = Find<TextBox>(panel, "TranscriptBox");
            Assert.Equal(full, transcript.Text);
            Assert.Equal(TextWrapping.Wrap, transcript.TextWrapping);
            Assert.Equal(ScrollBarVisibility.Auto, transcript.VerticalScrollBarVisibility);
            Assert.True(transcript.ActualWidth <= 600);
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public Task StaleCompletedResultIsClearedAtDispatcherPresentationBoundary() => OnDispatcher(async () =>
    {
        await using VoiceFlowFixture fixture = new();
        VoiceInputPanel panel = CreatePanel(fixture);
        try
        {
            await fixture.RecordAsync();
            Assert.NotEmpty(Find<TextBox>(panel, "TranscriptBox").Text);
            Assert.True(fixture.Execution.TryBeginSession(Guid.NewGuid(), out var newer));
            using (newer)
            {
                panel.Refresh();
                Assert.Empty(Find<TextBox>(panel, "TranscriptBox").Text);
                Assert.Null(panel.CurrentInput);
                await fixture.Flow.CloseAsync();
                Assert.True(newer!.IsCurrent);
            }
        }
        finally { panel.Detach(); }
    });

    [Fact]
    public Task ConnectedVrLossCancelsAndDrainsVoice_InitiallyAbsentVrDoesNot() => OnDispatcher(async () =>
    {
        await using VoiceFlowFixture fixture = new();
        using SteamVrResultPanel vr = new(Dispatcher.CurrentDispatcher);
        int losses = 0;
        Task cleanup = Task.CompletedTask;
        vr.ConnectionLost += (_, _) => { losses++; cleanup = fixture.Flow.CloseAsync(); };
        vr.UpdateConnectionAvailability(false);
        Assert.Equal(0, losses);
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        vr.UpdateConnectionAvailability(true);
        vr.UpdateConnectionAvailability(false);
        vr.UpdateConnectionAvailability(false);
        await cleanup;
        Assert.Equal(1, losses);
        Assert.True(fixture.Microphone.Disposed);
        Assert.Equal(0, fixture.Requests);
        Assert.Null(fixture.Flow.CurrentInput);
        fixture.NextMicrophone();
        await fixture.RecordAsync();
        Assert.NotNull(fixture.Flow.CurrentInput); // explicit desktop-only recovery
    });

    [Fact]
    public Task ResultOnlyConnectionIsTrackedWithoutPartialLauncherFalseLosses() => OnDispatcher(() =>
    {
        using SteamVrResultPanel vr = new(Dispatcher.CurrentDispatcher);
        int losses = 0;
        vr.ConnectionLost += (_, _) => losses++;
        vr.UpdateConnectionAvailability(true, resultConnection: true);
        vr.UpdateConnectionAvailability(false); // incomplete launcher retry must not hide a live result connection
        Assert.Equal(0, losses);
        vr.UpdateConnectionAvailability(false, resultConnection: true);
        Assert.Equal(1, losses);
        vr.UpdateConnectionAvailability(false, resultConnection: true);
        Assert.Equal(1, losses);
        return Task.CompletedTask;
    });

    private static VoiceInputPanel CreatePanel(VoiceFlowFixture fixture) => new(fixture.Flow,
        fixture.Execution, fixture.Configuration, fixture.Quotas.Voice, () => fixture.Settings.VoiceInput);
    private static T Find<T>(VoiceInputPanel panel, string name) where T : class => Assert.IsType<T>(panel.FindName(name));
    private static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static async Task OnDispatcher(Func<Task> test)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The voice test dispatcher did not stop.");
    }
}

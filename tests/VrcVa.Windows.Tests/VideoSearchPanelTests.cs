using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Windows.Video;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class VideoSearchPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TranscriptDirectlyExposesBothButtons_CardsPageAndCopyWithoutIntermediateChoice(bool interpreted) => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Assert.True(Find<Button>(panel, "DirectSearchButton").IsEnabled);
            Assert.True(Find<Button>(panel, "InterpretedSearchButton").IsEnabled);
            Assert.Equal(Visibility.Visible, voice.ResultActions.Visibility);
            Click(Find<Button>(panel, interpreted ? "InterpretedSearchButton" : "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            Assert.Equal(1, fixture.Searches);
            Assert.Equal(interpreted ? 1 : 0, fixture.Interpretations);
            Assert.Equal(5, Find<ItemsControl>(panel, "CandidateCards").Items.Count);
            Assert.False(Find<Button>(panel, "PreviousPageButton").IsEnabled);
            Assert.True(Find<Button>(panel, "NextPageButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(panel, "BatchEndText").Visibility);
            Click(Find<Button>(panel, "NextPageButton"));
            Assert.True(Find<Button>(panel, "PreviousPageButton").IsEnabled);
            Assert.False(Find<Button>(panel, "NextPageButton").IsEnabled);
            Assert.Equal("取得した候補はここまで", Find<TextBlock>(panel, "BatchEndText").Text);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(panel, "BatchEndText").Visibility);
            Layout(voice);
            Button[] cards = Descendants<Button>(panel).Where(button => button.Tag is VideoCandidateAction).ToArray();
            Assert.Equal(5, cards.Length);
            Click(cards[4]);
            await fixture.Flow.WhenIdle;
            Assert.Equal("https://www.youtube.com/watch?v=video000009", Assert.Single(fixture.Writes));
            Assert.Equal(5, Find<ItemsControl>(panel, "CandidateCards").Items.Count);
            Click(Find<Button>(panel, "BackToInputButton"));
            Assert.Empty(Find<ItemsControl>(panel, "CandidateCards").Items);
            Assert.Equal("synthetic transcript", Find<TextBox>(voice, "TranscriptBox").Text);
            Click(Find<Button>(panel, "CloseSearchButton"));
            await fixture.Flow.WhenIdle;
            await fixture.Voice.Flow.WhenIdle;
            Assert.Null(fixture.Voice.Flow.CurrentInput);
            Assert.Equal(Visibility.Collapsed, voice.ResultActions.Visibility);
        }
        finally { voice.Detach(); }
    });

    [Fact]
    public Task LongTextWrapsAndThumbnailFailureLeavesLiteralTitleAndCopyAction() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        string transcript = new('a', 3900);
        string title = "<synthetic title> " + new string('b', 3900);
        fixture.Voice.Response = (_, _) => Task.FromResult(VoiceFlowFixture.Success(transcript));
        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(1, true, title));
        fixture.ImageResponse = (_, _) => Task.FromResult(new VideoThumbnailResult(VideoThumbnailStatus.InvalidImage));
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            await fixture.Flow.WhenImagesIdle;
            Layout(voice);
            Assert.Equal(transcript, Find<TextBox>(voice, "TranscriptBox").Text);
            Assert.Equal("検索語: " + transcript, Find<TextBlock>(panel, "QueryText").Text);
            TextBlock displayedTitle = Assert.Single(Descendants<TextBlock>(panel), block => block.Text == title);
            Assert.Equal(TextWrapping.Wrap, displayedTitle.TextWrapping);
            Assert.True(displayedTitle.ActualWidth <= 600);
            Assert.Contains("一部", Find<TextBlock>(panel, "CandidateCountText").Text, StringComparison.Ordinal);
            Assert.Single(Descendants<Button>(panel), button => button.Tag is VideoCandidateAction && button.IsEnabled);
            Assert.Contains(Descendants<TextBlock>(panel), block => block.Text == "画像なし");
            Assert.All(Descendants<Image>(panel), image => Assert.Null(image.Source));
        }
        finally { voice.Detach(); }
    });

    [Fact]
    public Task CancelAndCloseStayEnabledUnderBusyHost_TranscriptSurvivesSearchCancellation() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<VideoSearchBatch> cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SearchResponse = (_, _) => { started.SetResult(); return cleanup.Task; };
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "InterpretedSearchButton"));
            await started.Task;
            Assert.True(voice.ResultActions.IsEnabled);
            Assert.True(Find<Button>(panel, "CancelSearchButton").IsEnabled);
            Assert.True(Find<Button>(panel, "CloseSearchButton").IsEnabled);
            Assert.False(Find<Button>(panel, "DirectSearchButton").IsEnabled);
            Assert.False(Find<Button>(voice, "RecordButton").IsEnabled);
            Click(Find<Button>(voice, "CancelVoiceButton"));
            Assert.Equal(VideoSearchFlowState.Cancelling, fixture.Flow.State);
            Assert.False(Find<Button>(panel, "CancelSearchButton").IsEnabled);
            Assert.Equal("synthetic transcript", Find<TextBox>(voice, "TranscriptBox").Text);
            cleanup.SetResult(SearchFlowFixture.Batch());
            await fixture.Flow.WhenIdle;
            Assert.True(Find<Button>(panel, "DirectSearchButton").IsEnabled);
            Assert.Equal("synthetic transcript", Find<TextBox>(voice, "TranscriptBox").Text);
        }
        finally { cleanup.TrySetResult(SearchFlowFixture.Batch()); voice.Detach(); }
    });

    [Fact]
    public Task SlowImageCleanupCannotCloseANewerRecording() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource<VideoThumbnailResult> image = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ImageResponse = (_, _) => image.Task;
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            Click(Find<Button>(panel, "CloseSearchButton"));
            Assert.Null(fixture.Voice.Flow.CurrentInput);
            Assert.Equal(Guid.Empty, fixture.Voice.Flow.SessionId);
            fixture.Voice.NextMicrophone();
            await fixture.Voice.RecordAsync();
            Guid current = fixture.Voice.Flow.SessionId;
            image.SetResult(new(VideoThumbnailStatus.Available, new VideoThumbnailImage(1, 1, new byte[] { 0, 0, 0, 255 })));
            await fixture.Flow.WhenImagesIdle;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(current, fixture.Voice.Flow.SessionId);
            Assert.Equal("synthetic transcript", Find<TextBox>(voice, "TranscriptBox").Text);
        }
        finally { image.TrySetResult(new(VideoThumbnailStatus.Cancelled)); voice.Detach(); }
    });

    [Fact]
    public Task AvailableThumbnailUsesFrozenBitmapAndRetainsCandidateSelection() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(1));
        fixture.ImageResponse = (_, _) => Task.FromResult(new VideoThumbnailResult(VideoThumbnailStatus.Available,
            new VideoThumbnailImage(1, 1, new byte[] { 0, 0, 0, 255 })));
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            await fixture.Flow.WhenImagesIdle;
            Layout(voice);
            Image displayed = Assert.Single(Descendants<Image>(panel));
            Assert.NotNull(displayed.Source);
            Assert.True(displayed.Source.IsFrozen);
            Assert.Single(Descendants<Button>(panel), button => button.Tag is VideoCandidateAction && button.IsEnabled);
        }
        finally { voice.Detach(); }
    });

    [Fact]
    public Task TimerAndThumbnailRefreshKeepCandidateButtonIdentityAndLogicalFocus() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource<VideoThumbnailResult> images = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ImageResponse = (_, _) => images.Task;
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            Layout(voice);
            Button first = Descendants<Button>(panel).First(button => button.Tag is VideoCandidateAction);
            System.Windows.Input.FocusManager.SetIsFocusScope(panel, true);
            System.Windows.Input.FocusManager.SetFocusedElement(panel, first);
            for (int tick = 0; tick < 4; tick++) { fixture.Voice.Flow.Refresh(); Layout(voice); }
            Assert.Same(first, Descendants<Button>(panel).First(button => button.Tag is VideoCandidateAction));
            Assert.Same(first, System.Windows.Input.FocusManager.GetFocusedElement(panel));
            images.SetResult(new(VideoThumbnailStatus.Available, new VideoThumbnailImage(1, 1, new byte[] { 0, 0, 0, 255 })));
            await fixture.Flow.WhenImagesIdle;
            Layout(voice);
            Assert.Same(first, Descendants<Button>(panel).First(button => button.Tag is VideoCandidateAction));
            Assert.Same(first, System.Windows.Input.FocusManager.GetFocusedElement(panel));
            Click(first);
            await fixture.Flow.WhenIdle;
            Assert.Single(fixture.Writes);
        }
        finally { images.TrySetResult(new(VideoThumbnailStatus.Cancelled)); voice.Detach(); }
    });

    [Fact]
    public Task TerminalFailureRemainsVisibleWhenCoordinatorInvalidatesVoiceSession() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        fixture.SearchResponse = (_, _) =>
        {
            fixture.Voice.Execution.Stop();
            throw new ScanException(ScanFailureCode.VideoSearchCleanupFailed, ScanStage.TextHandling, "synthetic cleanup failure");
        };
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            fixture.Voice.Flow.Refresh();
            Assert.Equal(Visibility.Visible, voice.ResultActions.Visibility);
            Assert.Contains("再起動", Find<TextBlock>(panel, "SearchStatusText").Text, StringComparison.Ordinal);
            Assert.False(Find<Button>(panel, "RetrySearchButton").IsEnabled);
            Assert.False(Find<Button>(voice, "RecordButton").IsEnabled);
            Assert.Empty(Find<TextBox>(voice, "TranscriptBox").Text);
        }
        finally { voice.Detach(); }
    });

    [Fact]
    public Task ZeroResultShowsBatchEndAndCanReturnWithoutTranscribingAgain() => OnDispatcher(async () =>
    {
        await using SearchFlowFixture fixture = new();
        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(0));
        VoiceInputPanel voice = CreateVoicePanel(fixture);
        VideoSearchPanel panel = Assert.IsType<VideoSearchPanel>(voice.ResultActions.Content);
        try
        {
            await fixture.Voice.RecordAsync();
            Click(Find<Button>(panel, "DirectSearchButton"));
            await fixture.Flow.WhenIdle;
            Assert.Empty(Find<ItemsControl>(panel, "CandidateCards").Items);
            Assert.Contains("0件", Find<TextBlock>(panel, "SearchStatusText").Text, StringComparison.Ordinal);
            Assert.False(Find<Button>(panel, "NextPageButton").IsEnabled);
            Assert.True(Find<Button>(panel, "BackToInputButton").IsEnabled);
            Click(Find<Button>(panel, "BackToInputButton"));
            Assert.Equal(1, fixture.Voice.Requests);
            Assert.Equal("synthetic transcript", fixture.Voice.Flow.CurrentInput!.Transcript);
        }
        finally { voice.Detach(); }
    });

    private static VoiceInputPanel CreateVoicePanel(SearchFlowFixture fixture)
    {
        VoiceInputPanel panel = new(fixture.Voice.Flow, fixture.Voice.Execution, fixture.Voice.Configuration,
            fixture.Voice.Quotas.Voice, () => fixture.Voice.Settings.VoiceInput);
        panel.AttachSearch(fixture.Flow, fixture.Voice.Quotas.SearchInterpretation);
        return panel;
    }
    private static T Find<T>(FrameworkElement panel, string name) where T : class => Assert.IsType<T>(panel.FindName(name));
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Layout(FrameworkElement panel)
    {
        panel.Measure(new Size(600, 700));
        panel.Arrange(new Rect(0, 0, 600, 700));
        panel.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) { yield return typed; }
            foreach (T descendant in Descendants<T>(child)) { yield return descendant; }
        }
    }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Search UI dispatcher did not stop.");
    }
}

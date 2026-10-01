using System.Runtime.InteropServices;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Windows.Video;

namespace VrcVa.Windows.Tests;

public sealed class WpfVideoClipboardDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BackgroundCaller_WritesExactlyOnceOnTheWpfStaThread()
    {
        using DispatcherFixture fixture = new();
        fixture.Start();
        WpfVideoClipboardDispatcher dispatcher = new(fixture.Dispatcher);
        int calls = 0;
        VideoClipboardCopyStatus status = await Task.Run(() => dispatcher.InvokeAsync(() =>
        {
            Assert.True(fixture.Dispatcher.CheckAccess());
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            calls++;
            return VideoClipboardCopyStatus.Copied;
        }, CancellationToken.None)).WaitAsync(Timeout);
        Assert.Equal(VideoClipboardCopyStatus.Copied, status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClipboardOccupation_ReportsFailureOnceAndAllowsExplicitRetry()
    {
        using DispatcherFixture fixture = new();
        fixture.Start();
        WpfVideoClipboardDispatcher dispatcher = new(fixture.Dispatcher);
        int calls = 0;
        VideoClipboardCopyStatus busy = await dispatcher.InvokeAsync(() =>
        {
            calls++;
            throw new ExternalException("synthetic clipboard content must not be returned");
        }, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(VideoClipboardCopyStatus.Busy, busy);
        Assert.Equal(1, calls);
        VideoClipboardCopyStatus retry = await dispatcher.InvokeAsync(() =>
        {
            calls++;
            return VideoClipboardCopyStatus.Copied;
        }, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(VideoClipboardCopyStatus.Copied, retry);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRealDispatcherCopy_RechecksTheSessionBeforeAnyFakeWrite(bool close)
    {
        using DispatcherFixture fixture = new(); // Dispatcher exists, but does not pump yet.
        ExecutionCoordinator execution = new();
        VideoSearchSession session = new(execution);
        TextInputSession input = TextInputSession.Create("synthetic transcript");
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);
        Assert.True(execution.TryBeginSession(request.CorrelationId, out var search, sessionId: input.SessionId));
        VideoSearchResult result;
        using (search)
        {
            FeatureResult feature = await new DirectVideoSearchHandler(new FakeProvider(), session)
                .HandleAsync(input, request, null, CancellationToken.None);
            result = feature.VideoSearch!;
        }
        RecordingWriter writer = new(fixture.Dispatcher);
        VideoCandidateClipboard clipboard = new(execution, session, new WpfVideoClipboardDispatcher(fixture.Dispatcher), writer);
        VideoCandidate candidate = result.Candidates[0];
        Task<VideoClipboardCopyResult> pending = clipboard.CopyAsync(candidate.CreateSelectionAction(), Guid.NewGuid());
        Assert.Empty(writer.Writes);
        if (close) { execution.CloseSession(); }
        fixture.Start();
        VideoClipboardCopyResult outcome = await pending.WaitAsync(Timeout);
        if (close)
        {
            Assert.NotEqual(VideoClipboardCopyStatus.Copied, outcome.Status);
            Assert.Empty(writer.Writes);
            Assert.False(clipboard.IsFeedbackCurrent(outcome));
        }
        else
        {
            Assert.Equal(VideoClipboardCopyStatus.Copied, outcome.Status);
            Assert.Equal([candidate.WatchUrl.AbsoluteUri], writer.Writes);
            Assert.True(clipboard.IsFeedbackCurrent(outcome));
            Assert.Same(result, session.CurrentResult);
        }
    }

    [Fact]
    public async Task NestedDispatcherFrame_CannotRunAQueuedCloseInsideTheWriteBoundary()
    {
        using DispatcherFixture fixture = new();
        fixture.Start();
        ExecutionCoordinator execution = new();
        VideoSearchSession session = new(execution);
        TextInputSession input = TextInputSession.Create("synthetic transcript");
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);
        Assert.True(execution.TryBeginSession(request.CorrelationId, out var search, sessionId: input.SessionId));
        VideoSearchResult result;
        using (search)
        {
            result = (await new DirectVideoSearchHandler(new FakeProvider(), session)
                .HandleAsync(input, request, null, CancellationToken.None)).VideoSearch!;
        }
        bool closed = false;
        TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingWriter writer = new(fixture.Dispatcher)
        {
            BeforeWrite = () =>
            {
                fixture.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    execution.CloseSession();
                    closed = true;
                    closedSignal.SetResult();
                }));
                Assert.Throws<InvalidOperationException>(() => Dispatcher.PushFrame(new DispatcherFrame()));
                Assert.False(closed);
            },
        };
        VideoCandidateClipboard clipboard = new(execution, session, new WpfVideoClipboardDispatcher(fixture.Dispatcher), writer);
        VideoClipboardCopyResult copied = await clipboard.CopyAsync(result.Candidates[0].CreateSelectionAction(), Guid.NewGuid()).WaitAsync(Timeout);
        await closedSignal.Task.WaitAsync(Timeout);
        Assert.Equal(VideoClipboardCopyStatus.Copied, copied.Status);
        Assert.Single(writer.Writes);
        Assert.False(clipboard.IsFeedbackCurrent(copied));
        Assert.Null(session.CurrentResult);
    }

    [Fact]
    public async Task MtaDispatcher_IsRejectedWithoutWriting()
    {
        using DispatcherFixture fixture = new(ApartmentState.MTA);
        fixture.Start();
        WpfVideoClipboardDispatcher dispatcher = new(fixture.Dispatcher);
        int calls = 0;
        VideoClipboardCopyStatus status = await dispatcher.InvokeAsync(() =>
        {
            calls++;
            return VideoClipboardCopyStatus.Copied;
        }, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(VideoClipboardCopyStatus.Unavailable, status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PreCancelledOrShutdownDispatcher_DoesNotRunWrite()
    {
        using DispatcherFixture fixture = new();
        fixture.Start();
        WpfVideoClipboardDispatcher dispatcher = new(fixture.Dispatcher);
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.InvokeAsync(() =>
        {
            calls++;
            return VideoClipboardCopyStatus.Copied;
        }, new CancellationToken(true)));
        await fixture.Dispatcher.InvokeAsync(() => fixture.Dispatcher.InvokeShutdown()).Task.WaitAsync(Timeout);
        Assert.Equal(VideoClipboardCopyStatus.Unavailable,
            await dispatcher.InvokeAsync(() => { calls++; return VideoClipboardCopyStatus.Copied; }, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private sealed class RecordingWriter(Dispatcher dispatcher) : IVideoClipboardTextWriter
    {
        public List<string> Writes { get; } = [];
        public Action? BeforeWrite { get; init; }
        public void SetText(string text)
        {
            Assert.True(dispatcher.CheckAccess());
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            BeforeWrite?.Invoke();
            Writes.Add(text);
        }
    }
    private sealed class FakeProvider : IVideoSearchProvider
    {
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new VideoSearchBatch([new VideoMetadata("sample00000", "Synthetic title")]));
    }
    private sealed class DispatcherFixture : IDisposable
    {
        private readonly ManualResetEventSlim _start = new();
        private readonly Thread _thread;
        public DispatcherFixture(ApartmentState apartment = ApartmentState.STA)
        {
            TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                _start.Wait();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(apartment);
            _thread.Start();
            Dispatcher = ready.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        }
        public Dispatcher Dispatcher { get; }
        public void Start() => _start.Set();
        public void Dispose()
        {
            if (!Dispatcher.HasShutdownStarted) { Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            _start.Set();
            if (!_thread.Join(Timeout)) { throw new TimeoutException("The clipboard fixture dispatcher did not stop."); }
            _start.Dispose();
        }
    }
}

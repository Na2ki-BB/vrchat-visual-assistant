using VrcVa.Windows.Startup;

namespace VrcVa.Windows.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void AcquireNamed_RejectsSecondAcquisitionInSameProcess()
    {
        string mutexName = CreateMutexName();

        using SingleInstanceGuard first = SingleInstanceGuard.AcquireNamed(mutexName);
        using SingleInstanceGuard second = SingleInstanceGuard.AcquireNamed(mutexName);

        Assert.True(first.IsPrimaryInstance);
        Assert.False(first.IsBypassed);
        Assert.False(second.IsPrimaryInstance);
        Assert.False(second.IsBypassed);
    }

    [Fact]
    public void AcquireNamed_AllowsReacquisitionAfterDispose()
    {
        string mutexName = CreateMutexName();

        SingleInstanceGuard first = SingleInstanceGuard.AcquireNamed(mutexName);
        Assert.True(first.IsPrimaryInstance);
        first.Dispose();

        using SingleInstanceGuard second = SingleInstanceGuard.AcquireNamed(mutexName);
        Assert.True(second.IsPrimaryInstance);
    }

    [Fact]
    public void Dispose_CanReleaseMutexFromADifferentThread()
    {
        string mutexName = CreateMutexName();
        SingleInstanceGuard guard = SingleInstanceGuard.AcquireNamed(mutexName);
        Exception? disposalFailure = null;
        Thread disposalThread = new(() =>
        {
            try
            {
                guard.Dispose();
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }
        });

        disposalThread.Start();
        Assert.True(disposalThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(disposalFailure);

        using SingleInstanceGuard reacquired = SingleInstanceGuard.AcquireNamed(mutexName);
        Assert.True(reacquired.IsPrimaryInstance);
    }

    [Fact]
    public void AcquireNamed_TreatsDifferentNamesIndependently()
    {
        using SingleInstanceGuard first = SingleInstanceGuard.AcquireNamed(CreateMutexName());
        using SingleInstanceGuard second = SingleInstanceGuard.AcquireNamed(CreateMutexName());

        Assert.True(first.IsPrimaryInstance);
        Assert.True(second.IsPrimaryInstance);
    }

    [Fact]
    public void AcquireNamed_CanBypassMutexForDiagnosticLaunch()
    {
        string mutexName = CreateMutexName();

        using SingleInstanceGuard normal = SingleInstanceGuard.AcquireNamed(mutexName);
        using SingleInstanceGuard diagnostic = SingleInstanceGuard.AcquireNamed(
            mutexName,
            bypassSingleInstance: true);

        Assert.True(normal.IsPrimaryInstance);
        Assert.True(diagnostic.IsPrimaryInstance);
        Assert.True(diagnostic.IsBypassed);
    }

    [Fact]
    public void AcquireNamed_TakesOwnershipOfAbandonedMutex()
    {
        string mutexName = CreateMutexName();
        using ManualResetEventSlim ownerAcquired = new(initialState: false);
        using ManualResetEventSlim ownerMayExit = new(initialState: false);
        Exception? ownerFailure = null;
        Thread abandonedOwner = new(() =>
        {
            try
            {
                using Mutex mutex = new(initiallyOwned: false, mutexName);
                mutex.WaitOne();
                ownerAcquired.Set();
                ownerMayExit.Wait();
                // Exiting the thread without ReleaseMutex intentionally abandons ownership.
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
                ownerAcquired.Set();
            }
        });

        abandonedOwner.Start();
        try
        {
            Assert.True(ownerAcquired.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(ownerFailure);

            // Keep the named kernel object alive after its owning handle is closed.
            using Mutex keeper = Mutex.OpenExisting(mutexName);
            ownerMayExit.Set();
            Assert.True(abandonedOwner.Join(TimeSpan.FromSeconds(5)));

            using SingleInstanceGuard guard = SingleInstanceGuard.AcquireNamed(mutexName);
            Assert.True(guard.IsPrimaryInstance);
        }
        finally
        {
            ownerMayExit.Set();
            abandonedOwner.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AcquireNamed_RejectsNullName() =>
        Assert.Throws<ArgumentNullException>(() => SingleInstanceGuard.AcquireNamed(null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Global\\VrcVa.Tests")]
    [InlineData("Local\\")]
    [InlineData("Local\\VrcVa/Tests")]
    [InlineData("Local\\VrcVa\\Tests")]
    [InlineData("Local\\VrcVa:Tests")]
    public void AcquireNamed_RejectsUnsafeOrNonSessionName(string mutexName) =>
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.AcquireNamed(mutexName));

    private static string CreateMutexName() => $@"Local\VrcVa.Tests.{Guid.NewGuid():N}";
}

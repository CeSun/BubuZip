using Xunit;

namespace EclipseArchive.Ui.Tests;

public sealed class BackgroundOperationsTests
{
    [Fact]
    public async Task StopWaitsForCancelledWorkToFinishItsFinallyBlock()
    {
        using var operations = new BackgroundOperations();
        var entered = Signal();
        var cleaning = Signal();
        var releaseCleanup = Signal();
        var cleaned = false;
        var operation = operations.RunAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally
            {
                cleaning.SetResult();
                await releaseCleanup.Task;
                cleaned = true;
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopped = operations.StopAsync();
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(operations.IsStopping);
        Assert.False(stopped.IsCompleted);
        Assert.False(cleaned);

        releaseCleanup.SetResult();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cleaned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    // Blocking is intentional here: the owner must be disposable from a UI thread that cannot pump.
#pragma warning disable xUnit1031
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopAndDisposeDoNotRequirePumpingTheCallerSynchronizationContext(bool disposeDirectly)
    {
        var original = SynchronizationContext.Current;
        var context = new NoPumpContext();
        using var entered = new ManualResetEventSlim();
        using var operations = new BackgroundOperations();
        var cleaned = false;
        Task operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            operation = operations.RunAsync(async token =>
            {
                Assert.Null(SynchronizationContext.Current);
                entered.Set();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { await Task.Yield(); cleaned = true; }
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            if (disposeDirectly) operations.Dispose();
            else operations.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            operations.Dispose();
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }

        Assert.True(cleaned);
        Assert.True(operation.IsCanceled);
        Assert.Equal(0, context.PostCount);
    }
#pragma warning restore xUnit1031

    [Fact]
    public async Task RunPreservesItsFailureWhileStopCompletesNormally()
    {
        using var operations = new BackgroundOperations();
        var failure = new IOException("Archive write failed.");
        var operation = operations.RunAsync(_ => throw failure);

        var observed = await Assert.ThrowsAsync<IOException>(() => operation);
        await operations.StopAsync();

        Assert.Same(failure, observed);
        Assert.True(operation.IsFaulted);
    }

    [Fact]
    public async Task CallerCancellationDoesNotStopTheOwnerOrCloseTheTokenEarly()
    {
        using var operations = new BackgroundOperations();
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var cleaned = false;
        var operation = operations.RunAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally
            {
                // A registration during cleanup must still be possible on the linked token.
                using var registration = token.Register(() => { });
                cleaned = true;
            }
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(cleaned);
        Assert.False(operations.IsStopping);
        var nextRan = false;
        await operations.RunAsync(_ => { nextRan = true; return Task.CompletedTask; });
        Assert.True(nextRan);
    }

    [Fact]
    public async Task AlreadyCancelledCallerDoesNotRunItsCallback()
    {
        using var operations = new BackgroundOperations();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.RunAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, cancellation.Token));
        await operations.StopAsync();

        Assert.False(ran);
    }

    [Fact]
    public async Task StopRejectsNewWorkAndIsIdempotent()
    {
        using var operations = new BackgroundOperations();

        operations.RequestStop();
        var firstStop = operations.StopAsync();
        var secondStop = operations.StopAsync();
        Assert.Same(firstStop, secondStop);
        Assert.Throws<InvalidOperationException>(() => { _ = operations.RunAsync(_ => Task.CompletedTask); });
        await firstStop;
        operations.Dispose();
        operations.Dispose();
        await operations.StopAsync();
        Assert.Throws<InvalidOperationException>(() => { _ = operations.RunAsync(_ => Task.CompletedTask); });
    }

    [Fact]
    public async Task ConcurrentStopRequestsJoinAllAcceptedWork()
    {
        using var operations = new BackgroundOperations();
        var entered = Signal();
        var active = 0;
        var cleaned = 0;
        var initial = operations.RunAsync(async token =>
        {
            Interlocked.Increment(ref active);
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Increment(ref cleaned); }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var submissions = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            Task operation;
            try
            {
                operation = operations.RunAsync(async token =>
                {
                    Interlocked.Increment(ref active);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { Interlocked.Increment(ref cleaned); }
                });
            }
            catch (InvalidOperationException) { return; }
            try { await operation; }
            catch (OperationCanceledException) { }
        })).ToArray();
        var stopRequests = Enumerable.Range(0, 8).Select(_ => Task.Run(operations.StopAsync)).ToArray();

        await Task.WhenAll(stopRequests).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial);

        Assert.Equal(active, cleaned);
        Assert.True(active >= 1);
        Assert.True(operations.IsStopping);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NoPumpContext : SynchronizationContext
    {
        public int PostCount;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref PostCount);
    }
}

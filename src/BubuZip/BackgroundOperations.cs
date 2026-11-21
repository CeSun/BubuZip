namespace BubuZip;

/// <summary>
/// Runs background work independently of the UI context and joins its cleanup when stopped.
/// Call <see cref="RequestStop"/> from managed work; stop or dispose the owner from outside it.
/// </summary>
public sealed class BackgroundOperations : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly TaskCompletionSource _stopSignalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Task> _active = [];
    private Task? _stopTask;
    private bool _stopping;
    private bool _disposed;

    public bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    public Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_stopping)
                throw new InvalidOperationException("Background operations are stopping and cannot accept new work.");

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCancellation.Token);
            // Do not pass the token to Task.Run: the wrapper must run even when pre-cancelled
            // so it always releases the linked source. The entire callback starts off the UI thread.
            var operation = Task.Run(async () =>
            {
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await action(linked.Token).ConfigureAwait(false);
                }
                finally { linked.Dispose(); }
            });
            _active.Add(operation);
            _ = operation.ContinueWith(static (completed, owner) =>
            {
                // Observing here prevents unobserved background exceptions; awaiting the
                // returned operation still propagates the original failure to its caller.
                _ = completed.Exception;
                var operations = (BackgroundOperations)owner!;
                lock (operations._gate) operations._active.Remove(completed);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation;
        }
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
        }

        // Cancellation callbacks may re-enter the owner, so do not invoke them under _gate.
        try { _stopCancellation.Cancel(throwOnFirstException: false); }
        catch (AggregateException)
        {
            // Cancel invokes every callback before throwing. Callback failures must not
            // prevent joining the work and allowing its finally blocks to clean up.
        }
        finally { _stopSignalled.TrySetResult(); }
    }

    public Task StopAsync()
    {
        RequestStop();
        lock (_gate)
            return _stopTask ??= JoinAsync(_stopSignalled.Task, _active.ToArray());
    }

    private static async Task JoinAsync(Task signalled, Task[] operations)
    {
        // Waiting for the signal also prevents Dispose from racing an in-flight Cancel.
        await signalled.ConfigureAwait(false);
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (Exception)
        {
            // Stop is a cleanup barrier; failures and cancellation remain on each RunAsync task.
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopCancellation.Dispose();
        }
    }
}

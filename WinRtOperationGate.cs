using Windows.Foundation;

namespace PlaycastCompanion;

/// <summary>Bounds waits and keeps one unfinished provider operation across retries.</summary>
public sealed class WinRtOperationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAsyncInfo? _pending;

    public async Task<T> InvokeAsync<T>(Func<IAsyncOperation<T>> create,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReapPending();
            var operation = create();
            try
            {
                await WaitAsync(operation, timeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var result = operation.GetResults();
                Close(operation);
                return result;
            }
            catch
            {
                if (Started(operation))
                {
                    try { operation.Cancel(); } catch { }
                    if (Started(operation)) _pending = operation;
                    else Close(operation);
                }
                else Close(operation);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private void ReapPending()
    {
        if (_pending == null) return;
        if (Started(_pending))
            throw new InvalidOperationException("Previous LampArray operation is still cancelling; retry deferred");
        Close(_pending);
        _pending = null;
    }

    private static bool Started(IAsyncInfo operation)
    {
        try { return operation.Status == AsyncStatus.Started; }
        catch { return true; }
    }

    private static void Close(IAsyncInfo operation)
    {
        try { operation.Close(); } catch { }
    }

    private static async Task WaitAsync(IAsyncInfo operation, TimeSpan timeout, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (operation.Status == AsyncStatus.Started)
        {
            token.ThrowIfCancellationRequested();
            var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("LampArray operation timed out");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining
                : TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
        }
    }
}

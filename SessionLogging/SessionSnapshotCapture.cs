namespace PlaycastCompanion;

/// <summary>Provides fresh local state if full terminal inventory cannot be collected.</summary>
public interface ITerminalSnapshotFallbackSource
{
    object CaptureTerminalFallback(string error);
}

/// <summary>Bounds callers while retaining one outstanding provider operation until it finishes.</summary>
internal sealed class SessionSnapshotCapture
{
    private readonly ISessionSnapshotSource _source;
    private readonly SemaphoreSlim _provider = new(1, 1);

    public SessionSnapshotCapture(ISessionSnapshotSource source) => _source = source;

    public async Task<object> CaptureAsync(CancellationToken cancellationToken)
    {
        await _provider.WaitAsync(cancellationToken).ConfigureAwait(false);
        var capture = Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await _source.CaptureAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _provider.Release(); }
        }, CancellationToken.None);
        // Observe a late failure even when cancellation already released this caller.
        _ = capture.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await capture.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public object Fallback(string error) => _source is ITerminalSnapshotFallbackSource fallback
        ? fallback.CaptureTerminalFallback(error) : MachineSnapshotSource.CaptureTerminalFallback(error);
}

namespace PlaycastCompanion;

/// <summary>A lighting system that can be blacked out while a guest is hosting.</summary>
public interface ILightingBackend
{
    string DisplayName { get; }
    bool Enabled { get; }
    bool IsHolding { get; }
    string StatusText { get; }
    void StartBlackout();
    Task StopBlackoutAsync();
}

/// <summary>
/// Shared hold/release loop for all lighting backends: start spins a
/// background task that re-asserts the blackout every Tick and retries after
/// RetryDelay on failure; stop cancels the loop and runs the backend's
/// release. The OperationCanceledException filter matters: HttpClient
/// timeouts throw TaskCanceledException too, and those must retry, not exit.
/// </summary>
public abstract class LightingBackendBase : ILightingBackend
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _loggedUnavailable;

    public abstract string DisplayName { get; }
    public abstract bool Enabled { get; }
    public string StatusText { get; protected set; } = "Ready";

    public bool IsHolding
    {
        get { lock (_gate) return _cts != null; }
    }

    protected virtual TimeSpan Tick => TimeSpan.FromSeconds(5);
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(30);

    public void StartBlackout()
    {
        lock (_gate)
        {
            if (_cts != null || !Enabled)
                return;
            _loggedUnavailable = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopBlackoutAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        if (cts == null)
            return;
        cts.Cancel();
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        cts.Dispose();
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"{DisplayName}: release failed ({ex.Message})");
        }
        StatusText = ReleasedStatus;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ApplyTickAsync(ct).ConfigureAwait(false);
                if (_loggedUnavailable)
                {
                    _loggedUnavailable = false;
                    Log.Info($"{DisplayName}: blackout applied (recovered)");
                }
                StatusText = HoldingStatus;
                await Task.Delay(Tick, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusText = UnavailableText(ex);
                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    Log.Info($"{DisplayName} unavailable ({ex.Message}); retrying every {(int)RetryDelay.TotalSeconds}s");
                }
                OnConnectionLost();
                try
                {
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Status shown while a tick has succeeded. Backends can override
    /// to report a nuance (e.g. WDL deferring devices to a vendor engine).</summary>
    protected virtual string HoldingStatus => "Holding blackout";

    /// <summary>Connect if needed and (re-)assert the blackout once.</summary>
    protected abstract Task ApplyTickAsync(CancellationToken ct);

    /// <summary>Hand lighting control back to the vendor software.</summary>
    protected abstract Task ReleaseAsync();
    protected virtual string ReleasedStatus => "Ready";

    /// <summary>Called when a tick failed, before the retry delay.</summary>
    protected virtual void OnConnectionLost() { }

    protected virtual string UnavailableText(Exception ex) => "Unreachable — retrying";
}

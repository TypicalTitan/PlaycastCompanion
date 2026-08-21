using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PlaycastCompanion;

/// <summary>
/// Minimal Discord Rich Presence client — talks straight to the local Discord
/// desktop client over its IPC named pipe (discord-ipc-0..9), no third-party
/// libraries. While the guest session is active it shows a configurable
/// activity on the user's profile; when the session ends the activity is
/// cleared. Discord not running or a missing Application ID just means
/// "quietly retry / do nothing" — never an error the user has to deal with.
/// </summary>
public sealed class DiscordPresence
{
    private readonly DiscordConfig _cfg;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private NamedPipeClientStream? _pipe;
    private DateTimeOffset _activeSince;
    private bool _loggedUnavailable;
    private volatile string? _hostedGame;
    private readonly SemaphoreSlim _nudge = new(0, 1);

    public string StatusText { get; private set; }

    /// <summary>Set the guest's current game (from the shim relay); null clears
    /// it. Nudges the presence loop to re-send within seconds.</summary>
    public void SetHostedGame(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (string.Equals(_hostedGame, normalized, StringComparison.Ordinal))
            return;
        _hostedGame = normalized;
        try { _nudge.Release(); } catch (SemaphoreFullException) { }
    }

    public DiscordPresence(DiscordConfig cfg)
    {
        _cfg = cfg;
        StatusText = InitialStatus();
    }

    private string InitialStatus() =>
        !_cfg.Enabled ? "Off"
        : IsConfigured ? "Ready — shows while a guest is hosting"
        : "Not linked — set Discord.ApplicationId in config.json";

    public bool IsConfigured =>
        _cfg.Enabled && ulong.TryParse(_cfg.ApplicationId?.Trim(), out _);

    public void SetActive()
    {
        lock (_gate)
        {
            if (_cts != null)
                return;
            if (!IsConfigured)
            {
                StatusText = InitialStatus();
                return;
            }
            _activeSince = DateTimeOffset.UtcNow;
            _loggedUnavailable = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    public async Task ClearAsync()
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
        await CloseAsync().ConfigureAwait(false);
        _hostedGame = null; // never let a stale game survive a guest-session-end
        StatusText = InitialStatus();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_pipe is not { IsConnected: true })
                    await ConnectAndHandshakeAsync(ct).ConfigureAwait(false);
                await SendActivityAsync(ct).ConfigureAwait(false);
                if (_loggedUnavailable)
                    Log.Info("Discord presence restored");
                _loggedUnavailable = false;
                StatusText = "Live on your profile";
                // Wait for a nudge (a captured game changed) or the 30s heartbeat,
                // whichever comes first — so a guest game shows within seconds while
                // still detecting a dead pipe / picking up edits every 30s.
                try { await _nudge.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusText = "Discord not reachable — retrying";
                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    Log.Info($"Discord presence unavailable ({ex.Message}); retrying every 30s");
                }
                DisposePipe();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConnectAndHandshakeAsync(CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(250, ct).ConfigureAwait(false);
                _pipe = pipe;
                var handshake = JsonSerializer.Serialize(new { v = 1, client_id = _cfg.ApplicationId!.Trim() });
                await SendFrameAsync(0, handshake, ct).ConfigureAwait(false);
                var ready = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (ready.Contains("\"evt\":\"ERROR\"", StringComparison.Ordinal))
                    throw new IOException("Discord rejected the handshake (check the Application ID)");
                Log.Info($"Discord presence connected (discord-ipc-{i})");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe.Dispose();
                _pipe = null;
                throw;
            }
            catch
            {
                pipe.Dispose();
                _pipe = null;
            }
        }
        throw new IOException("no Discord IPC pipe answered (is Discord running?)");
    }

    private async Task SendActivityAsync(CancellationToken ct)
    {
        var assets = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(_cfg.LargeImageKey))
            assets["large_image"] = _cfg.LargeImageKey.Trim();
        if (!string.IsNullOrWhiteSpace(_cfg.LargeImageText))
            assets["large_text"] = _cfg.LargeImageText.Trim();

        var activity = new Dictionary<string, object?>
        {
            ["timestamps"] = new Dictionary<string, object?>
            {
                ["start"] = _activeSince.ToUnixTimeMilliseconds(),
            },
        };
        var hosted = _hostedGame;
        if (_cfg.GamePassthroughEnabled && !string.IsNullOrWhiteSpace(hosted))
            activity["details"] = AppConfig.Sanitize(_cfg.HostingTemplate.Replace("{game}", hosted), 128, "Hosting a game");
        else if (!string.IsNullOrWhiteSpace(_cfg.Details))
            activity["details"] = AppConfig.Sanitize(_cfg.Details, 128, "Hosting");
        if (!string.IsNullOrWhiteSpace(_cfg.State))
            activity["state"] = AppConfig.Sanitize(_cfg.State, 128, "");
        if (assets.Count > 0)
            activity["assets"] = assets;

        await SendCommandAsync(activity, ct).ConfigureAwait(false);
        var resp = await ReadFrameAsync(ct).ConfigureAwait(false);
        if (resp.Contains("\"evt\":\"ERROR\"", StringComparison.Ordinal))
            throw new IOException("Discord rejected the activity (check the Application ID and asset key)");
    }

    private Task SendCommandAsync(object? activity, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["args"] = new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = activity,
            },
        });
        return SendFrameAsync(1, payload, ct);
    }

    private async Task CloseAsync()
    {
        if (_pipe is { IsConnected: true })
        {
            // Clearing is belt-and-braces: Discord also drops the activity as
            // soon as the pipe closes.
            try
            {
                using var timeout = new CancellationTokenSource(2000);
                await SendCommandAsync(null, timeout.Token).ConfigureAwait(false);
            }
            catch { }
        }
        DisposePipe();
    }

    private void DisposePipe()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    private async Task SendFrameAsync(int opcode, string json, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        var data = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + data.Length];
        BitConverter.GetBytes(opcode).CopyTo(frame, 0);
        BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
        data.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> ReadFrameAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3000);
        var header = new byte[8];
        await ReadExactAsync(pipe, header, timeout.Token).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > 1_000_000)
            throw new IOException("malformed IPC frame");
        var body = new byte[length];
        await ReadExactAsync(pipe, body, timeout.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("pipe closed");
            offset += read;
        }
    }
}

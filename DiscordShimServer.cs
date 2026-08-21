using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PlaycastCompanion;

/// <summary>
/// Runs ONLY in the guest (headless) session. Pretends to be a Discord IPC
/// endpoint so a guest-launched, Discord-integrated game connects to it, then
/// captures just the game's Application ID from the handshake and relays it to
/// the owner instance. Rich-presence frames are read past and discarded.
///
/// Load-bearing constraints (verified — see README "Game pass-through"):
///  - The \\.\pipe namespace is machine-global and the owner's real Discord
///    holds discord-ipc-0. We therefore NEVER bind slot 0 (we probe 1..9 and
///    take the lowest free one), so we can never block or intercept the owner's
///    real Discord. A game only reaches us if its client advanced past a denied
///    slot 0 — games whose Discord library gives up on ACCESS_DENIED are simply
///    never seen, and that is accepted.
///  - Every connection is session-gated: a client not in this (guest) session
///    is logged and dropped, so we never capture an owner-session game if the
///    owner's Discord is momentarily absent and slot 0 is free.
/// </summary>
public sealed class DiscordShimServer
{
    private readonly AppConfig _cfg;
    private readonly int _ownSessionId;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _boundSlot = -1;
    private string? _currentClientId;

    public DiscordShimServer(AppConfig cfg)
    {
        _cfg = cfg;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        _ownSessionId = self.SessionId;
    }

    public void Start()
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts == null)
            return;
        cts.Cancel();
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        LogOccupancy();
        long heartbeat = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer(out int slot);
                if (server == null)
                {
                    // every slot 1..9 taken — unusual; wait and retry
                    Log.Info("Shim: no free discord-ipc slot (1..9) to bind; retrying in 30s");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }
                if (slot != _boundSlot)
                {
                    _boundSlot = slot;
                    Log.Info($"Shim: listening on discord-ipc-{slot} (guest session {_ownSessionId})");
                }

                var connected = await WaitForConnectionAsync(server, ct, () =>
                {
                    if (++heartbeat % 6 == 0) // ~ every 3 min of idle
                        Log.Info($"Shim: no game has connected yet (discord-ipc-{_boundSlot})");
                }).ConfigureAwait(false);
                if (!connected)
                    continue;

                await HandleClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Shim: accept loop error ({ex.Message}); retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>Bind the lowest free discord-ipc slot in 1..9 (never 0).</summary>
    private static NamedPipeServerStream? CreateServer(out int slot)
    {
        for (int i = 1; i <= 9; i++)
        {
            try
            {
                var server = new NamedPipeServerStream($"discord-ipc-{i}", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                slot = i;
                return server;
            }
            catch (IOException) { /* ERROR_PIPE_BUSY — real Discord or another app holds it */ }
            catch (UnauthorizedAccessException) { /* held with a restrictive DACL */ }
        }
        slot = -1;
        return null;
    }

    private static async Task<bool> WaitForConnectionAsync(NamedPipeServerStream server, CancellationToken ct, Action onIdleTick)
    {
        while (!ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await server.WaitForConnectionAsync(idle.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                onIdleTick(); // idle 30s slice elapsed — recheck cancellation, keep waiting
            }
        }
        return false;
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        // Session-gate: only accept a game running in THIS guest session.
        int clientPid = TryGetClientPid(server);
        int clientSession = SessionOf(clientPid);
        string exeName = ProductNameOf(clientPid);
        if (clientPid > 0 && clientSession >= 0 && clientSession != _ownSessionId)
        {
            Log.Info($"Shim: dropping foreign-session client pid {clientPid} (session {clientSession} != {_ownSessionId})");
            return;
        }

        // First inbound frame is the handshake (opcode 0, {v, client_id}).
        var (opcode, body) = await ReadFrameAsync(server, ct).ConfigureAwait(false);
        string? clientId = null;
        if (opcode == 0)
            clientId = TryExtractClientId(body);
        Log.Info($"Shim: game connected pid {clientPid} exe '{exeName}' client_id '{clientId ?? "?"}'");

        // Reply with a minimal READY so the game stays quiet; capture already done.
        await SendReadyAsync(server, clientId, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(clientId) || !string.IsNullOrEmpty(exeName))
        {
            _currentClientId = clientId;
            await RelayAsync(new { @event = "game", clientId, procHint = exeName }, ct).ConfigureAwait(false);
        }

        // Drain and DISCARD subsequent frames (SET_ACTIVITY, etc.) until close.
        try
        {
            while (server.IsConnected && !ct.IsCancellationRequested)
                await ReadFrameAsync(server, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* game closed the pipe */ }
        finally
        {
            if (_currentClientId != null || !string.IsNullOrEmpty(exeName))
            {
                _currentClientId = null;
                Log.Info("Shim: game disconnected -> clear");
                await RelayAsync(new { @event = "clear" }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task RelayAsync(object message, CancellationToken ct)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _cfg.Discord.ShimPipeName,
                PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            await client.ConnectAsync(2000, timeout.Token).ConfigureAwait(false);
            var line = JsonSerializer.Serialize(message) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // owner instance may not be up yet; harmless, next event retries
            Log.Info($"Shim: relay to owner failed ({ex.Message})");
        }
    }

    private void LogOccupancy()
    {
        var occupied = new List<int>();
        for (int i = 0; i <= 9; i++)
        {
            if (File.Exists($@"\\.\pipe\discord-ipc-{i}"))
                occupied.Add(i);
        }
        Log.Info($"Shim: startup — discord-ipc slots occupied: [{string.Join(",", occupied)}]");
    }

    // ---- framing (mirrors DiscordPresence: [opcode LE int32][len LE int32][utf8 json]) ----

    private static async Task<(int opcode, string body)> ReadFrameAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactAsync(pipe, header, ct).ConfigureAwait(false);
        int opcode = BitConverter.ToInt32(header, 0);
        int length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > 1_000_000)
            throw new IOException("malformed shim frame");
        var body = new byte[length];
        await ReadExactAsync(pipe, body, ct).ConfigureAwait(false);
        return (opcode, Encoding.UTF8.GetString(body));
    }

    private static async Task SendReadyAsync(NamedPipeServerStream pipe, string? clientId, CancellationToken ct)
    {
        var ready = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["cmd"] = "DISPATCH",
            ["evt"] = "READY",
            ["data"] = new Dictionary<string, object?>
            {
                ["v"] = 1,
                ["config"] = new Dictionary<string, object?>
                {
                    ["cdn_host"] = "cdn.discordapp.com",
                    ["api_endpoint"] = "//discord.com/api",
                    ["environment"] = "production",
                },
                ["user"] = new Dictionary<string, object?>
                {
                    ["id"] = "0",
                    ["username"] = "playcast",
                    ["discriminator"] = "0000",
                    ["global_name"] = "playcast",
                },
            },
        });
        var data = Encoding.UTF8.GetBytes(ready);
        var frame = new byte[8 + data.Length];
        BitConverter.GetBytes(1).CopyTo(frame, 0); // opcode 1 = FRAME
        BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
        data.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
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

    private static string? TryExtractClientId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("client_id", out var id))
                return id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText().Trim('"');
        }
        catch { }
        return null;
    }

    // ---- client identity ----

    private static int TryGetClientPid(NamedPipeServerStream server)
    {
        try
        {
            return GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out uint pid)
                ? (int)pid : -1;
        }
        catch { return -1; }
    }

    private static int SessionOf(int pid)
    {
        if (pid <= 0)
            return -1;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.SessionId;
        }
        catch { return -1; }
    }

    private static string ProductNameOf(int pid)
    {
        if (pid <= 0)
            return "";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            var path = p.MainModule?.FileName;
            if (string.IsNullOrEmpty(path))
                return "";
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var name = info.ProductName?.Trim();
            if (!string.IsNullOrEmpty(name))
                return name!;
            var desc = info.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc))
                return desc!;
            return Path.GetFileNameWithoutExtension(path);
        }
        catch { return ""; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PlaycastCompanion;

/// <summary>
/// Runs ONLY in the owner (non-headless) session. Hosts the guest→owner relay:
/// an ACL'd named pipe the guest shim connects to, gated so only the guest
/// account can write and every caller's identity is re-verified server-side
/// (so no other local process can inject a game name into your Discord). Each
/// message resolves an Application ID to a display name and folds it into the
/// live presence; a "clear" drops back to the static line.
/// </summary>
public sealed class DiscordChannelListener
{
    private readonly AppConfig _cfg;
    private readonly DiscordPresence _discord;
    private readonly GameNameCache _names;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DiscordChannelListener(AppConfig cfg, DiscordPresence discord)
    {
        _cfg = cfg;
        _discord = discord;
        _names = new GameNameCache(cfg.Discord);
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
        Log.Info($"Relay: listening for the guest shim on pipe '{_cfg.Discord.ShimPipeName}'");
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                if (!CallerIsGuest(server))
                {
                    Log.Info("Relay: rejected a connection from a non-guest caller");
                    continue;
                }

                var line = await ReadLineAsync(server, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                    await HandleMessageAsync(line!).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"Relay: listener error ({ex.Message}); retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        var self = WindowsIdentity.GetCurrent().User;
        if (self != null)
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        try
        {
            var guestSid = (SecurityIdentifier)new NTAccount(_cfg.TargetUsername).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(guestSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        catch
        {
            // guest account not resolvable (e.g. never created yet) — admit any
            // authenticated local user; the per-message CallerIsGuest check is
            // still the real trust gate.
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(_cfg.Discord.ShimPipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private bool CallerIsGuest(NamedPipeServerStream server)
    {
        try
        {
            var caller = server.GetImpersonationUserName(); // e.g. MACHINE\NonsoleMode
            var shortName = caller.Contains('\\') ? caller[(caller.IndexOf('\\') + 1)..] : caller;
            return string.Equals(shortName, _cfg.TargetUsername, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Info($"Relay: could not read caller identity ({ex.Message})");
            return false;
        }
    }

    private async Task HandleMessageAsync(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            if (evt == "clear")
            {
                _discord.SetHostedGame(null);
                Log.Info("Relay: guest game cleared");
                return;
            }
            if (evt == "game")
            {
                var clientId = root.TryGetProperty("clientId", out var c) ? c.GetString() : null;
                var procHint = root.TryGetProperty("procHint", out var p) ? p.GetString() : null;
                var name = await _names.ResolveAsync(clientId, procHint).ConfigureAwait(false);
                _discord.SetHostedGame(name);
                Log.Info($"Relay: guest game '{name}' (client_id {clientId ?? "?"})");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Relay: bad message ({ex.Message})");
        }
    }

    private static async Task<string?> ReadLineAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3000);
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (true)
        {
            int read = await pipe.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
                break;
            int nl = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (nl >= 0)
            {
                sb.Append(Encoding.UTF8.GetString(buffer, 0, nl));
                break;
            }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (sb.Length > 8192)
                break; // guard against a runaway sender
        }
        return sb.ToString();
    }
}

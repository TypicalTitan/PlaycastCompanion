using System.Net.Sockets;
using System.Text;

namespace PlaycastCompanion;

/// <summary>
/// OpenRGB blackout via its SDK server (TCP, default port 6742) using profile
/// commands: on the first tick the current state is saved to a restore
/// profile, then the user's all-black profile (default name "Blackout",
/// created once in OpenRGB) is loaded; release loads the restore profile
/// back. Profile commands need protocol v2+, so the version is negotiated on
/// every connection. One integration covers motherboards, RAM, GPUs and most
/// peripheral brands OpenRGB drives.
/// </summary>
public sealed class OpenRgbController : LightingBackendBase
{
    private const string RestoreProfile = "playcast-restore";

    private readonly OpenRgbConfig _cfg;
    private bool _saved;

    public OpenRgbController(OpenRgbConfig cfg) => _cfg = cfg;

    public override string DisplayName => "OpenRGB";
    public override bool Enabled => _cfg.Enabled;
    protected override TimeSpan Tick => TimeSpan.FromSeconds(30);
    protected override string UnavailableText(Exception ex) => "OpenRGB server not reachable — retrying";

    protected override async Task ApplyTickAsync(CancellationToken ct)
    {
        using var client = await OpenRgbClient.ConnectAsync(_cfg.Host, _cfg.Port, ct).ConfigureAwait(false);
        if (!_saved)
        {
            // capture the user's current lighting once per hold — never again,
            // or a failed tick could overwrite the restore point with black
            await client.SaveProfileAsync(RestoreProfile, ct).ConfigureAwait(false);
            _saved = true;
            Log.Info($"OpenRGB: current lighting saved to profile '{RestoreProfile}'");
        }
        var profile = AppConfig.Sanitize(_cfg.BlackoutProfile, 64, "Blackout");
        await client.LoadProfileAsync(profile, ct).ConfigureAwait(false);
    }

    protected override async Task ReleaseAsync()
    {
        if (!_saved)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            using var client = await OpenRgbClient.ConnectAsync(_cfg.Host, _cfg.Port, timeout.Token).ConfigureAwait(false);
            await client.LoadProfileAsync(RestoreProfile, timeout.Token).ConfigureAwait(false);
            await client.DeleteProfileAsync(RestoreProfile, timeout.Token).ConfigureAwait(false);
            Log.Info("OpenRGB: restore profile loaded; lighting back to normal");
        }
        finally
        {
            _saved = false;
        }
    }

    /// <summary>Minimal OpenRGB SDK protocol client ("ORGB"-magic framed TCP).</summary>
    private sealed class OpenRgbClient : IDisposable
    {
        private const uint RequestProtocolVersion = 40;
        private const uint SetClientName = 50;
        private const uint RequestSaveProfile = 151;
        private const uint RequestLoadProfile = 152;
        private const uint RequestDeleteProfile = 153;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private OpenRgbClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        public static async Task<OpenRgbClient> ConnectAsync(string host, int port, CancellationToken ct)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
            var client = new OpenRgbClient(tcp);
            try
            {
                await client.SendAsync(RequestProtocolVersion, BitConverter.GetBytes(3u), ct).ConfigureAwait(false);
                var reply = await client.ReadPayloadAsync(ct).ConfigureAwait(false);
                var serverVersion = reply.Length >= 4 ? BitConverter.ToUInt32(reply, 0) : 0u;
                if (serverVersion < 2)
                    throw new InvalidOperationException($"OpenRGB protocol {serverVersion} has no profile support (need v2+)");
                await client.SendAsync(SetClientName, Encoding.ASCII.GetBytes("Playcast Companion\0"), ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public Task SaveProfileAsync(string name, CancellationToken ct) => SendAsync(RequestSaveProfile, NameBytes(name), ct);
        public Task LoadProfileAsync(string name, CancellationToken ct) => SendAsync(RequestLoadProfile, NameBytes(name), ct);
        public Task DeleteProfileAsync(string name, CancellationToken ct) => SendAsync(RequestDeleteProfile, NameBytes(name), ct);

        private static byte[] NameBytes(string name) => Encoding.ASCII.GetBytes(name + "\0");

        private async Task SendAsync(uint command, byte[] payload, CancellationToken ct)
        {
            var frame = new byte[16 + payload.Length];
            frame[0] = (byte)'O';
            frame[1] = (byte)'R';
            frame[2] = (byte)'G';
            frame[3] = (byte)'B';
            BitConverter.GetBytes(0u).CopyTo(frame, 4);               // device id (unused here)
            BitConverter.GetBytes(command).CopyTo(frame, 8);
            BitConverter.GetBytes((uint)payload.Length).CopyTo(frame, 12);
            payload.CopyTo(frame, 16);
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadPayloadAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            var header = new byte[16];
            await ReadExactAsync(header, timeout.Token).ConfigureAwait(false);
            var length = BitConverter.ToUInt32(header, 12);
            if (length > 1_000_000)
                throw new IOException("malformed OpenRGB frame");
            var payload = new byte[length];
            await ReadExactAsync(payload, timeout.Token).ConfigureAwait(false);
            return payload;
        }

        private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("connection closed");
                offset += read;
            }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
            try { _tcp.Dispose(); } catch { }
        }
    }
}

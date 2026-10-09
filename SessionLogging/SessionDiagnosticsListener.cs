using System.IO.Pipes;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaycastCompanion;

/// <summary>Owner-only authenticated local diagnostic sink. Producer access is limited to owner, guest, and SYSTEM.</summary>
public sealed class SessionDiagnosticsListener : IDisposable
{
    public const string PipeName = "PlaycastCompanion.SessionDiagnostics";
    public const int MaxFrameBytes = 1024 * 1024;
    private readonly string _targetUsername;
    private readonly string _pipeName;
    private readonly Action<JsonObject> _onEvent;
    private readonly Action<string> _onStatus;
    private readonly DiagnosticClientIdentityVerifier _clientVerifier = new();
    private readonly uint _ownerSessionId;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public SessionDiagnosticsListener(string targetUsername, Action<JsonObject> onEvent, Action<string> onStatus,
        string pipeName = PipeName)
    {
        _targetUsername = targetUsername;
        _pipeName = pipeName;
        _onEvent = onEvent;
        _onStatus = onStatus;
        using var process = Process.GetCurrentProcess();
        _ownerSessionId = (uint)process.SessionId;
    }

    public void Start()
    {
        if (_cancellation != null) return;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _loop = Task.Run(() => ListenAsync(token));
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        var loop = _loop;
        _cancellation = null;
        _loop = null;
        if (cancellation == null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var allowed = AllowedIdentities();
                using var server = CreateServer(allowed, _pipeName);
                _onStatus("Waiting for Playcast diagnostic feed; realtime coverage unavailable until connected.");
                // Guest accounts may be provisioned after Companion starts. Recreate the idle
                // pipe periodically so its explicit ACL includes a newly created guest SID.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                try { await server.WaitForConnectionAsync(idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { continue; }
                await ConsumeAsync(server, allowed, cancellationToken).ConfigureAwait(false);
                _onStatus("Playcast diagnostic feed disconnected; waiting for reconnect.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or IdentityNotMappedException or InvalidOperationException)
            {
                _onStatus($"Playcast diagnostic feed unavailable: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ConsumeAsync(NamedPipeServerStream server, SecurityIdentifier[] allowed,
        CancellationToken cancellationToken)
    {
        await using var frames = new DiagnosticFrameReader().ReadAsync(server, cancellationToken).GetAsyncEnumerator();
        if (!await frames.MoveNextAsync().ConfigureAwait(false)) return;
        _clientVerifier.Verify(server, allowed, _ownerSessionId);
        _onStatus("Playcast diagnostic feed connected; host observer supplies realtime messages.");
        do
        {
            if (TryParseFrame(frames.Current, out var envelope, out var error))
                _onEvent(envelope!);
            else
                _onStatus($"Playcast diagnostic frame rejected: {error}");
        }
        while (await frames.MoveNextAsync().ConfigureAwait(false));
    }

    public static bool TryParseFrame(ReadOnlySpan<byte> bytes, out JsonObject? envelope, out string error)
    {
        envelope = null;
        error = "Invalid envelope";
        if (bytes.Length > MaxFrameBytes)
        {
            error = "Frame exceeds 1 MiB";
            return false;
        }
        try
        {
            envelope = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
            if (envelope?["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1)
            {
                error = "Unsupported diagnostic protocol version";
                return false;
            }
            if (envelope["category"] is not JsonValue category || !category.TryGetValue<string>(out var name)
                || name is not ("realtime" or "ipc" or "state" or "diagnostics") || !envelope.ContainsKey("payload"))
            {
                error = "Missing or invalid category/payload";
                return false;
            }
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            error = "Invalid JSON: " + ex.Message;
            return false;
        }
    }

    private SecurityIdentifier[] AllowedIdentities()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var identities = new HashSet<SecurityIdentifier>
        {
            identity.User ?? throw new UnauthorizedAccessException("Cannot identify the diagnostic sink owner."),
            new(WellKnownSidType.LocalSystemSid, null),
        };
        try
        {
            identities.Add((SecurityIdentifier)new NTAccount(Environment.MachineName, _targetUsername)
                .Translate(typeof(SecurityIdentifier)));
        }
        catch (IdentityNotMappedException)
        {
            _onStatus("Guest account SID unavailable; only owner and SYSTEM can send diagnostics.");
        }
        return identities.ToArray();
    }

    private static NamedPipeServerStream CreateServer(SecurityIdentifier[] allowed, string pipeName)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Deny));
        foreach (var sid in allowed)
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var owner = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(owner.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 16 * 1024, 0, security);
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

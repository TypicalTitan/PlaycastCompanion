using System.IO.Pipes;
using System.Security.Principal;

namespace PlaycastCompanion;

public sealed record DiagnosticPipePeer(uint ProcessId, uint SessionId);

public interface IDiagnosticPipePeerSource
{
    string? FailureReason => null;
    DiagnosticPipePeer? GetPeer(NamedPipeServerStream pipe);
    SecurityIdentifier? GetSessionUser(uint sessionId);
    SecurityIdentifier? GetProcessUser(uint processId);
}

/// <summary>Verifies kernel-reported client sessions without impersonating cross-account Node clients.</summary>
public sealed class DiagnosticClientIdentityVerifier
{
    private readonly IDiagnosticPipePeerSource _source;

    public DiagnosticClientIdentityVerifier(IDiagnosticPipePeerSource? source = null) =>
        _source = source ?? new WindowsDiagnosticPipePeerSource();

    public void Verify(NamedPipeServerStream pipe, IReadOnlyCollection<SecurityIdentifier> allowed,
        uint ownerSessionId)
    {
        var peer = _source.GetPeer(pipe);
        if (peer == null || peer.ProcessId == 0)
            throw new UnauthorizedAccessException("Cannot query the diagnostic producer's kernel process/session identity. "
                + _source.FailureReason);
        // The pipe's explicit DACL has already restricted the authenticated account
        // to owner/guest/SYSTEM. The kernel session check also excludes other sessions.
        if (peer.SessionId == ownerSessionId) return;
        var user = _source.GetSessionUser(peer.SessionId) ?? _source.GetProcessUser(peer.ProcessId);
        if (user == null || !allowed.Contains(user))
            throw new UnauthorizedAccessException(
                $"Cannot verify an allowed diagnostic producer account in Windows session {peer.SessionId}. "
                + _source.FailureReason);
    }
}

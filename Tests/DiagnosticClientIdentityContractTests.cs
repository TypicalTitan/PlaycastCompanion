using System.IO.Pipes;
using System.Security.Principal;

namespace PlaycastCompanion.Tests;

public static class DiagnosticClientIdentityContractTests
{
    public static void Run()
    {
        var owner = new SecurityIdentifier("S-1-5-21-1-2-3-1000");
        var guest = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var foreign = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier[] allowed = [owner, guest, system];
        using var pipe = new NamedPipeServerStream("PlaycastCompanion.IdentityContract." + Guid.NewGuid().ToString("N"));
        var source = new FakePeerSource { Peer = new DiagnosticPipePeer(100, 1) };
        var verifier = new DiagnosticClientIdentityVerifier(source);
        verifier.Verify(pipe, allowed, 1);
        Check(source.SessionQueries == 0 && source.ProcessQueries == 0,
            "The ACL-authorized owner session must not require cross-account impersonation or token access.");
        source.Peer = new DiagnosticPipePeer(101, 2);
        source.SessionUser = guest;
        verifier.Verify(pipe, allowed, 1);
        Check(source.ProcessQueries == 0, "A guest session account must verify without querying its protected process token.");
        source.SessionUser = foreign;
        ExpectRejected(() => verifier.Verify(pipe, allowed, 1), "Foreign session accounts must be rejected.");
        source.SessionUser = null;
        source.ProcessUser = null;
        ExpectRejected(() => verifier.Verify(pipe, allowed, 1), "Unavailable account queries must reject the producer.");
        source.Peer = new DiagnosticPipePeer(102, 0);
        source.ProcessUser = system;
        verifier.Verify(pipe, allowed, 1);
        source.ProcessUser = foreign;
        ExpectRejected(() => verifier.Verify(pipe, allowed, 1), "Session zero must not imply a SYSTEM identity.");
        source.Peer = null;
        ExpectRejected(() => verifier.Verify(pipe, allowed, 1), "Missing kernel identifiers must reject the producer.");
        source.Peer = new DiagnosticPipePeer(0, 1);
        ExpectRejected(() => verifier.Verify(pipe, allowed, 1), "Invalid process identifiers must reject even an owner-session match.");
    }

    private static void ExpectRejected(Action action, string message)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);

    private sealed class FakePeerSource : IDiagnosticPipePeerSource
    {
        public DiagnosticPipePeer? Peer { get; set; }
        public SecurityIdentifier? SessionUser { get; set; }
        public SecurityIdentifier? ProcessUser { get; set; }
        public int SessionQueries { get; private set; }
        public int ProcessQueries { get; private set; }
        public DiagnosticPipePeer? GetPeer(NamedPipeServerStream pipe) => Peer;
        public SecurityIdentifier? GetSessionUser(uint sessionId) { SessionQueries++; return SessionUser; }
        public SecurityIdentifier? GetProcessUser(uint processId) { ProcessQueries++; return ProcessUser; }
    }
}

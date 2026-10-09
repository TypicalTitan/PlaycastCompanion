using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PlaycastCompanion;

/// <summary>Reads OS-authenticated pipe identifiers and account metadata without impersonation privileges.</summary>
public sealed class WindowsDiagnosticPipePeerSource : IDiagnosticPipePeerSource
{
    public string? FailureReason { get; private set; }

    public DiagnosticPipePeer? GetPeer(NamedPipeServerStream pipe)
    {
        FailureReason = null;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        { NoteFailure("GetNamedPipeClientProcessId"); return null; }
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId))
        { NoteFailure("GetNamedPipeClientSessionId"); return null; }
        return new DiagnosticPipePeer(processId, sessionId);
    }

    public SecurityIdentifier? GetSessionUser(uint sessionId)
    {
        var username = SessionText(sessionId, 5); // WTSUserName
        if (string.IsNullOrWhiteSpace(username)) return null;
        var domain = SessionText(sessionId, 7); // WTSDomainName
        if (domain == null) return null; // A denied domain query must not invent a local domain.
        try
        {
            return (SecurityIdentifier)new NTAccount(string.IsNullOrWhiteSpace(domain)
                ? Environment.MachineName : domain, username).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        { FailureReason = "The Windows session account could not be resolved to a SID."; return null; }
    }

    public SecurityIdentifier? GetProcessUser(uint processId)
    {
        // WTS has no interactive username for session 0. An independently queryable
        // process token can identify SYSTEM; denied token queries remain unavailable.
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) { NoteFailure("OpenProcess"); return null; }
        if (!OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY
        { NoteFailure("OpenProcessToken"); token.Dispose(); return null; }
        using (token)
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out var length); // TokenUser
            if (length < IntPtr.Size || length > 65536) return null;
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetTokenInformation(token, 1, buffer, length, out _))
                { NoteFailure("GetTokenInformation"); return null; }
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private string? SessionText(uint sessionId, int informationClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, informationClass, out var buffer, out var bytes))
        { NoteFailure($"WTSQuerySessionInformation({informationClass})"); return null; }
        try { return bytes >= 2 && buffer != IntPtr.Zero ? Marshal.PtrToStringUni(buffer) ?? "" : ""; }
        finally { WTSFreeMemory(buffer); }
    }

    private void NoteFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        var description = $"{operation} failed (Win32 {error}: {new Win32Exception(error).Message}).";
        FailureReason = FailureReason == null ? description : FailureReason + " " + description;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, uint informationLength, out uint returnLength);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int informationClass,
        out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

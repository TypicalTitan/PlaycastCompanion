using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Forms;

namespace PlaycastCompanion;

/// <summary>
/// Hidden window that receives WM_WTSSESSION_CHANGE for all sessions
/// (WTSRegisterSessionNotification) and answers "is the target user currently
/// logged on?" by enumerating sessions and querying WTSUserName. Re-evaluating
/// the full session list on every event keeps this stateless and immune to
/// missed or reordered notifications.
/// </summary>
public sealed class SessionWatcher : NativeWindow, IDisposable
{
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int NOTIFY_FOR_ALL_SESSIONS = 1;
    private const int WTS_USERNAME = 5; // WTS_INFO_CLASS.WTSUserName

    private enum ConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    /// <summary>Raised on any session logon/logoff/connect/disconnect/lock/unlock, for any session.</summary>
    public event Action? SessionsChanged;

    private readonly string _targetUser;
    private readonly bool _includeDisconnected;
    private bool _registered;

    public SessionWatcher(AppConfig cfg)
    {
        _targetUser = cfg.TargetUsername;
        _includeDisconnected = cfg.IncludeDisconnectedSessions;
        CreateHandle(new CreateParams());
        _registered = WTSRegisterSessionNotification(Handle, NOTIFY_FOR_ALL_SESSIONS);
        if (!_registered)
            Log.Info($"WTSRegisterSessionNotification failed (error {Marshal.GetLastWin32Error()}); relying on the periodic poll only");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WTSSESSION_CHANGE)
            SessionsChanged?.Invoke();
        base.WndProc(ref m);
    }

    public bool IsTargetUserLoggedOn() => GetTargetSessionIds().Count > 0;

    public IReadOnlyList<int> GetTargetSessionIds()
    {
        // If this instance itself runs under the target account, blackout applies
        // for the whole lifetime of the process.
        if (string.Equals(Environment.UserName, _targetUser, StringComparison.OrdinalIgnoreCase))
        {
            using var current = Process.GetCurrentProcess();
            return [current.SessionId];
        }

        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var pInfo, out var count))
            return [];
        var sessions = new List<int>();
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(pInfo + i * size);
                if (!CountsAsLoggedOn((ConnectState)info.State))
                    continue;
                if (string.Equals(GetSessionUser(info.SessionId), _targetUser, StringComparison.OrdinalIgnoreCase))
                    sessions.Add(info.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(pInfo);
        }
        return sessions;
    }

    private bool CountsAsLoggedOn(ConnectState state) =>
        state is ConnectState.Active or ConnectState.Connected
        || (_includeDisconnected && state == ConnectState.Disconnected);

    private static string? GetSessionUser(int sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTS_USERNAME, out var pBuf, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(pBuf);
        }
        finally
        {
            WTSFreeMemory(pBuf);
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            WTSUnRegisterSessionNotification(Handle);
            _registered = false;
        }
        DestroyHandle();
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}

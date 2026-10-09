using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PlaycastCompanion;

public sealed class WindowsSessionInventorySource : ISessionInventorySource
{
    public SessionInventorySnapshot Capture(string targetUsername, bool includeDisconnected)
    {
        var sessions = new List<SessionObservation>();
        var errors = new List<SnapshotError>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var memory, out var count))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            return new(SnapshotFailures.Status(error), sessions, new[] { SnapshotFailures.From("wts_enumeration", error) });
        }
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(memory + index * size);
                var username = QueryUsername(session.SessionId, errors);
                var active = session.State is 0 or 1 || includeDisconnected && session.State == 4;
                sessions.Add(new(session.SessionId, StateName(session.State), username.Value,
                    active && string.Equals(username.Value, targetUsername, StringComparison.OrdinalIgnoreCase), username.Status));
            }
        }
        finally { WTSFreeMemory(memory); }
        return new(errors.Count == 0 ? "available" : "partial", sessions, errors);
    }

    private static ObservedMetric<string> QueryUsername(int sessionId, List<SnapshotError> errors)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, 5, out var memory, out _))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            errors.Add(SnapshotFailures.From($"wts_username:{sessionId}", error));
            return new(null, SnapshotFailures.Status(error), error.Message);
        }
        try { return new(Marshal.PtrToStringUni(memory), "available"); }
        finally { WTSFreeMemory(memory); }
    }

    private static string StateName(int state) => state switch
    {
        0 => "active", 1 => "connected", 2 => "connect_query", 3 => "shadow", 4 => "disconnected",
        5 => "idle", 6 => "listen", 7 => "reset", 8 => "down", 9 => "init", _ => $"unknown:{state}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo { public int SessionId; public IntPtr StationName; public int State; }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr memory, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr memory, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

public sealed class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
    public ProcessInventorySnapshot Capture(IReadOnlySet<int> targetSessions,
        IReadOnlyList<InstalledGame> games, CancellationToken cancellationToken)
    {
        var playcast = new List<ProcessObservation>();
        var running = new List<ProcessObservation>();
        var errors = new List<SnapshotError>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception error) { return new(SnapshotFailures.Status(error), playcast, running, new[] { SnapshotFailures.From("process_enumeration", error) }); }
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureProcess(process, targetSessions, games, playcast, running, errors);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return new(errors.Count == 0 ? "available" : "partial", playcast, running, errors);
    }

    private static void CaptureProcess(Process process, IReadOnlySet<int> targetSessions,
        IReadOnlyList<InstalledGame> games, List<ProcessObservation> playcast,
        List<ProcessObservation> running, List<SnapshotError> errors)
    {
        var name = Read(() => process.ProcessName);
        if (name.Status != "available")
        {
            errors.Add(new($"process_name:{process.Id}", name.Status, name.Error ?? "Process name unavailable."));
            return;
        }
        var session = Read<int?>(() => process.SessionId);
        if (session.Status != "available")
            errors.Add(new($"process_session:{process.Id}", session.Status, session.Error ?? "Session identity unavailable."));
        bool isPlaycast = name.Value!.StartsWith("Playcast", StringComparison.OrdinalIgnoreCase)
            || name.Value.StartsWith("PlayJector", StringComparison.OrdinalIgnoreCase);
        bool isTarget = session.Value is int id && targetSessions.Contains(id);
        if (!isPlaycast && !isTarget) return;
        var executable = Read(() => process.MainModule?.FileName);
        if (executable.Status != "available")
            errors.Add(new($"process_path:{process.Id}", executable.Status, executable.Error ?? "No executable path."));
        var game = isTarget && executable.Value is string path && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? games.FirstOrDefault(game => GameInventorySource.ContainsExecutable(game.InstallRoot, path)) : null;
        if (!isPlaycast && game is null) return;
        var observation = new ProcessObservation(process.Id, name.Value, session, executable,
            Read<double?>(() => process.TotalProcessorTime.TotalSeconds),
            Read<long?>(() => process.WorkingSet64),
            Read<DateTimeOffset?>(() => new DateTimeOffset(process.StartTime.ToUniversalTime())),
            game?.Id, game?.Name, game?.Store);
        if (isPlaycast) playcast.Add(observation);
        if (game is not null) running.Add(observation);
    }

    private static ObservedMetric<T> Read<T>(Func<T?> read)
    {
        try
        {
            var value = read();
            return new(value, value is null ? "unavailable" : "available");
        }
        catch (InvalidOperationException error) { return new(default, "process_exited", error.Message); }
        catch (Exception error) { return new(default, SnapshotFailures.Status(error), error.Message); }
    }
}

public sealed class WindowsNonsoleRegistrySource : INonsoleRegistrySource
{
    public NonsoleRegistrySnapshot Capture()
    {
        var errors = new List<SnapshotError>();
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var nonsole = machine.OpenSubKey(@"SOFTWARE\Playcast\Nonsole", false);
            using var guest = machine.OpenSubKey(@"SOFTWARE\Playcast\GuestMode", false);
            return new(nonsole is null ? "not_found" : "available",
                ReadFileTime(nonsole, "SessionLoggedInUtc"), ReadFileTime(nonsole, "LastHeartbeatUtc"),
                ReadInt(nonsole, "HeartbeatIntervalSeconds"), ReadString(guest, "VirtualDisplayDeviceName"), errors);
        }
        catch (Exception error)
        {
            var status = SnapshotFailures.Status(error);
            errors.Add(SnapshotFailures.From("nonsole_registry", error));
            return new(status, new(null, status, error.Message), new(null, status, error.Message),
                new(null, status, error.Message), new(null, status, error.Message), errors);
        }
    }

    private static ObservedMetric<DateTimeOffset?> ReadFileTime(RegistryKey? key, string name)
    {
        try
        {
            var value = key?.GetValue(name);
            if (value is null) return new(null, "missing");
            if (value is not long fileTime || fileTime <= 0) return new(null, "invalid_value", "Expected positive REG_QWORD Windows FILETIME.");
            return new(new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)), "available");
        }
        catch (Exception error) { return new(null, SnapshotFailures.Status(error), error.Message); }
    }

    private static ObservedMetric<int?> ReadInt(RegistryKey? key, string name)
    {
        try
        {
            var value = key?.GetValue(name);
            return value is int number ? new(number, "available") : new(null, value is null ? "missing" : "invalid_value");
        }
        catch (Exception error) { return new(null, SnapshotFailures.Status(error), error.Message); }
    }

    private static ObservedMetric<string> ReadString(RegistryKey? key, string name)
    {
        try
        {
            var value = key?.GetValue(name);
            return value is string text ? new(text, "available") : new(null, value is null ? "missing" : "invalid_value");
        }
        catch (Exception error) { return new(null, SnapshotFailures.Status(error), error.Message); }
    }
}

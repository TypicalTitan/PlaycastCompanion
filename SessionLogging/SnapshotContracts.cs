namespace PlaycastCompanion;

public sealed record SnapshotError(string Source, string Status, string Message);
public sealed record SourceCoverage(string Source, string Status, int EntriesRead,
    IReadOnlyList<SnapshotError> Errors);
public sealed record InstalledGame(string Store, string Id, string Name, string InstallRoot,
    string ManifestPath);
public sealed record GameInventorySnapshot(IReadOnlyList<InstalledGame> Games,
    IReadOnlyList<SourceCoverage> Sources, IReadOnlyList<string> Limitations);

public interface IGameInventorySource
{
    GameInventorySnapshot Capture(CancellationToken cancellationToken);
}

public interface IInventoryFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    string ReadAllText(string path);
    IEnumerable<string> EnumerateFiles(string path, string pattern);
}

public sealed record SessionObservation(int SessionId, string State, string? Username,
    bool IsTarget, string UsernameStatus);
public sealed record SessionInventorySnapshot(string Status,
    IReadOnlyList<SessionObservation> Sessions, IReadOnlyList<SnapshotError> Errors);
public interface ISessionInventorySource
{
    SessionInventorySnapshot Capture(string targetUsername, bool includeDisconnected);
}

public sealed record ObservedMetric<T>(T? Value, string Status, string? Error = null);
public sealed record ProcessObservation(int Pid, string Name,
    ObservedMetric<int?> SessionId, ObservedMetric<string> ExecutablePath,
    ObservedMetric<double?> CpuSeconds, ObservedMetric<long?> WorkingSetBytes,
    ObservedMetric<DateTimeOffset?> StartedAtUtc, string? MatchedGameId = null,
    string? MatchedGameName = null, string? GameStore = null);
public sealed record ProcessInventorySnapshot(string Status,
    IReadOnlyList<ProcessObservation> PlaycastProcesses,
    IReadOnlyList<ProcessObservation> RunningGames,
    IReadOnlyList<SnapshotError> Errors);
public interface IProcessSnapshotSource
{
    ProcessInventorySnapshot Capture(IReadOnlySet<int> targetSessions,
        IReadOnlyList<InstalledGame> games, CancellationToken cancellationToken);
}

public sealed record NonsoleRegistrySnapshot(string Status,
    ObservedMetric<DateTimeOffset?> SessionLoggedInUtc,
    ObservedMetric<DateTimeOffset?> LastHeartbeatUtc,
    ObservedMetric<int?> HeartbeatIntervalSeconds,
    ObservedMetric<string> VirtualDisplayDeviceName,
    IReadOnlyList<SnapshotError> Errors);
public interface INonsoleRegistrySource
{
    NonsoleRegistrySnapshot Capture();
}

public static class SnapshotFailures
{
    public static string Status(Exception error) => error is UnauthorizedAccessException
        or System.Security.SecurityException ? "access_denied"
        : error is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
            ? "access_denied" : "error";

    public static SnapshotError From(string source, Exception error) =>
        new(source, Status(error), error.Message);
}

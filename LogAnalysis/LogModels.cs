using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

public sealed record LogRecord(int Id, string Category, string ObservedCategory,
    DateTimeOffset RecordedAtUtc, DateTimeOffset? ObservedAtUtc, string EventType,
    string SourceFile, int LineNumber, string SessionKey, bool IncludeSecrets,
    JsonElement Raw, JsonElement Payload, JsonElement Message)
{
    public DateTimeOffset EventTimeUtc => ObservedAtUtc ?? RecordedAtUtc;
}

public sealed record LogImportIssue(string SourceFile, int? LineNumber, string Message);

public sealed record LogImportResult(IReadOnlyList<LogRecord> Records,
    IReadOnlyList<LogImportIssue> Issues, int FilesImported, long BytesRead,
    bool IsPartial, int IssueCount)
{
    public int RawSecretRecordCount => Records.Count(record => record.IncludeSecrets);
}

public sealed record LogImportLimits(long MaxTotalBytes = 128 * 1024 * 1024,
    int MaxRecords = 50000, int MaxLineBytes = 10 * 1024 * 1024, int MaxReportedIssues = 200);

public sealed record ScriptExecutionGroup(string Key, string Name, string ExecutionId,
    string? DeploymentId, string SessionKey, string Status, DateTimeOffset FirstAtUtc,
    DateTimeOffset LastAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? EndedAtUtc,
    string? ScriptText, int? ExitCode, string? Stdout, string? Stderr, string? Error,
    IReadOnlyList<LogRecord> Records)
{
    public TimeSpan? ObservedRuntime => StartedAtUtc is DateTimeOffset start
        && EndedAtUtc is DateTimeOffset end && end >= start ? end - start : null;
}

public sealed record InstalledGameView(string Store, string Id, string Name,
    string? InstallRoot, bool IsRunning);

public sealed record RunningGameView(string Store, string Id, string Name,
    int? ProcessId, double? WorkingSetBytes, double? CpuSeconds);

public sealed record GameSnapshotView(DateTimeOffset RecordedAtUtc, string SessionKey,
    IReadOnlyList<InstalledGameView> InstalledGames, IReadOnlyList<RunningGameView> RunningGames,
    string DetectionStatus, IReadOnlyList<string> CoverageNotes, LogRecord SourceRecord)
{
    public int RunningGameCount => RunningGames.Select(game => (game.Store.ToUpperInvariant(), game.Id.ToUpperInvariant())).Distinct().Count();
    public int RunningProcessCount => RunningGames.Count;
}

public sealed record HealthSample(DateTimeOffset RecordedAtUtc, string SessionKey,
    int? ProcessId, double? WorkingSetBytes, double? CpuSeconds, LogRecord SourceRecord);

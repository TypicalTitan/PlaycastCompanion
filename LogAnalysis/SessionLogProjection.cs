namespace PlaycastCompanion.LogAnalysis;

public sealed class SessionLogProjection
{
    private static readonly HashSet<string> FailureEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "feed_dropped", "recordRejected", "sessionStorageLimitReached", "snapshotFailed",
        "observer_failed", "realtime_error", "realtime_send_failed", "realtime_http_failed",
    };

    public IReadOnlyList<LogRecord> Records { get; }
    public IReadOnlyList<LogRecord> CommunicationRecords { get; }
    public IReadOnlyList<ScriptExecutionGroup> Scripts { get; }
    public IReadOnlyList<LogRecord> ScriptCancellationEvents { get; }
    public GameSnapshotView? LatestGames { get; }
    public IReadOnlyList<HealthSample> HealthSamples { get; }
    public IReadOnlyList<LogRecord> CaptureFailures { get; }

    public SessionLogProjection(LogImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);
        Records = import.Records.OrderBy(record => record.RecordedAtUtc).ThenBy(record => record.Id).ToArray();
        CommunicationRecords = Records.Where(record => record.Category is "realtime" or "ipc").ToArray();
        var scripts = new ScriptLogProjection(Records);
        Scripts = scripts.Groups;
        ScriptCancellationEvents = scripts.Cancellations;
        var machine = new MachineLogProjection(Records);
        LatestGames = machine.LatestGames;
        HealthSamples = machine.HealthSamples;
        CaptureFailures = Records.Where(record => FailureEvents.Contains(record.EventType)).ToArray();
    }
}

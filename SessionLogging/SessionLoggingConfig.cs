namespace PlaycastCompanion;

public sealed class SessionLoggingConfig
{
    public bool Enabled { get; set; } = true;
    public bool IncludeSecrets { get; set; }
    public int SnapshotIntervalSeconds { get; set; } = 30;
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public long MaxSessionBytes { get; set; } = 100 * 1024 * 1024;
    public int RetentionDays { get; set; } = 14;

    public void Normalize()
    {
        SnapshotIntervalSeconds = Math.Clamp(SnapshotIntervalSeconds, 5, 600);
        MaxFileBytes = Math.Clamp(MaxFileBytes, 64 * 1024, 50 * 1024 * 1024);
        MaxSessionBytes = Math.Clamp(MaxSessionBytes, MaxFileBytes + 4096, 1024L * 1024 * 1024);
        RetentionDays = Math.Clamp(RetentionDays, 1, 365);
    }
}

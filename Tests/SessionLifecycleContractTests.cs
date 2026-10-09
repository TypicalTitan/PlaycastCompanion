namespace PlaycastCompanion.Tests;

public static class SessionLifecycleContractTests
{
    public static async Task RunAsync()
    {
        await StartsAutomaticallyWritesSnapshotsAndStopsCleanly();
        await RotatesWhenSessionIdentityChanges();
        await StopsLoggingWhenDisabledAndRestartsWhenEnabled();
        await SecretToggleAppliesToFutureRecords();
    }

    private static async Task StartsAutomaticallyWritesSnapshotsAndStopsCleanly()
    {
        var root = SessionLogContractTests.NewRoot();
        var source = new FakeSnapshot();
        using var logger = new SessionLogger(new AppConfig(), source, true, root);
        logger.Record("realtime", new { ignored = true });
        Check(!Directory.EnumerateDirectories(root).Any(), "Inactive sessions must not generate log files.");
        logger.UpdateSession(true, "guest WTS active", "42");
        await source.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitFor(() => File.Exists(Path.Combine(logger.LogDirectory, "games.jsonl")));
        logger.LogAppEvent("Playcast connected");
        logger.Record("realtime", new { type = "hello", token = "hidden-token" });
        var directory = logger.LogDirectory;
        logger.UpdateSession(false, "guest logged off", "");
        logger.Record("realtime", new { ignoredAfter = true });
        await logger.StopAsync();
        var logs = SessionLogContractTests.ReadAll(directory);
        Check(logs.Contains("sessionStarted") && logs.Contains("sessionEnded") && logs.Contains("machineSnapshot"),
            "Automatic sessions must include boundaries and machine snapshots.");
        Check(logs.Contains("Game A") && logs.Contains("detected") && logs.Contains("partial"), "Games and their coverage must be recorded.");
        Check(!logs.Contains("hidden-token") && !logs.Contains("ignoredAfter"), "Session boundaries and redaction must apply to all categories.");
        Check(logger.StatusText.Contains("Headless"), "Guest instances must clearly disclose owner-only realtime coverage.");
    }

    private static async Task RotatesWhenSessionIdentityChanges()
    {
        using var logger = new SessionLogger(new AppConfig(), new FakeSnapshot(), true, SessionLogContractTests.NewRoot());
        logger.UpdateSession(true, "first Windows guest session", "42");
        var first = logger.LogDirectory;
        logger.UpdateSession(true, "replaced Windows guest session", "99");
        var second = logger.LogDirectory;
        Check(first != second, "Changed Windows guest IDs must rotate the active session directory.");
        Check(SessionLogContractTests.ReadAll(first).Contains("Guest session identity changed"), "Identity rotation needs a clear session-end reason.");
        await logger.StopAsync();
    }

    private static async Task StopsLoggingWhenDisabledAndRestartsWhenEnabled()
    {
        var config = new AppConfig();
        using var logger = new SessionLogger(config, new FakeSnapshot(), true, SessionLogContractTests.NewRoot());
        logger.UpdateSession(true, "active", "42");
        var first = logger.LogDirectory;
        config.SessionLogging.Enabled = false;
        logger.Record("realtime", new { shouldNotAppear = true });
        logger.UpdateSession(true, "config changed", "42");
        Check(!SessionLogContractTests.ReadAll(first).Contains("shouldNotAppear"), "Disabling logging must immediately prevent future writes.");
        Check(SessionLogContractTests.ReadAll(first).Contains("Session logging disabled"), "Disabling needs a session-end marker.");
        config.SessionLogging.Enabled = true;
        logger.UpdateSession(true, "enabled", "42");
        Check(first != logger.LogDirectory, "Re-enabling during an active guest session must create a new session.");
        await logger.StopAsync();
    }

    private static async Task SecretToggleAppliesToFutureRecords()
    {
        using var logger = new SessionLogger(new AppConfig(), new FakeSnapshot(), true, SessionLogContractTests.NewRoot());
        logger.UpdateSession(true, "active");
        logger.Record("realtime", new { token = "redacted-first" });
        logger.IncludeSecrets = true;
        logger.Record("realtime", new { token = "visible-debug-secret" });
        logger.IncludeSecrets = false;
        logger.Record("realtime", new { token = "redacted-last" });
        await logger.StopAsync();
        var logs = SessionLogContractTests.ReadAll(logger.LogDirectory);
        Check(logs.Contains("visible-debug-secret") && !logs.Contains("redacted-first") && !logs.Contains("redacted-last"),
            "The debug override must apply to future records and reset immediately when disabled.");
        Check(logs.Contains("secretCaptureChanged"), "Secret-policy transitions must be auditable in session logs.");
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for a logged snapshot.");
            await Task.Delay(20);
        }
    }

    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);

    private sealed class FakeSnapshot : ISessionSnapshotSource
    {
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<object> CaptureAsync(CancellationToken cancellationToken)
        {
            Captured.TrySetResult();
            return ValueTask.FromResult<object>(new
            {
                installedGames = new[] { new { name = "Game A" } },
                runningGames = new[] { new { name = "Game A", status = "detected" } },
                installedGameSources = new[] { new { status = "partial" } },
                inventoryLimitations = new[] { "manual installs need native catalogs" },
                runningGameDetectionStatus = "partial", targetSessionIds = new[] { 42 },
            });
        }
    }
}

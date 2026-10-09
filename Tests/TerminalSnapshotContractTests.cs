using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PlaycastCompanion.Tests;

public static class TerminalSnapshotContractTests
{
    public static async Task RunAsync()
    {
        await CapturesFreshReleasedStateBeforeSessionEnd();
        await TimesOutToUnknownInventoryAndFreshResources();
        await LateOldCaptureCannotEnterReplacementSession();
        await FailedTerminalCapturePreservesBackendFallback();
    }

    private static async Task CapturesFreshReleasedStateBeforeSessionEnd()
    {
        var source = new MutableSnapshot();
        using var logger = new SessionLogger(new AppConfig(), source, true, SessionLogContractTests.NewRoot());
        logger.UpdateSession(true, "active", "first");
        await WaitFor(() => Records(logger.LogDirectory, "diagnostics").Any(IsPeriodic));
        source.State = 22;
        source.Released = true;
        var clock = Stopwatch.StartNew();
        logger.UpdateSession(false, "guest ended");
        Check(clock.Elapsed < TimeSpan.FromMilliseconds(500), "Ending a session blocked on terminal inventory.");
        await logger.StopAsync();
        var machine = Records(logger.LogDirectory, "diagnostics").Single(IsTerminal);
        var games = Records(logger.LogDirectory, "games").Single(IsTerminal);
        var ended = Records(logger.LogDirectory, "session").Single(row => Event(row) == "sessionEnded");
        Check(machine["payload"]?["snapshot"]?["companion"]?["cpuSeconds"]?.GetValue<int>() == 22,
            "Terminal resources were copied from the earlier periodic sample.");
        Check(machine["payload"]?["snapshot"]?["companionBackends"]?["holding"]?.GetValue<bool>() == false,
            "Terminal sample did not observe the backend's released state.");
        Check(games["payload"]?["installedGames"]?[0]?["name"]?.GetValue<string>() == "Game 22",
            "Terminal game inventory was not captured freshly.");
        Check(Time(machine) <= Time(ended) && Time(games) <= Time(ended), "Session ended before its terminal snapshots.");
    }

    private static async Task TimesOutToUnknownInventoryAndFreshResources()
    {
        var source = new DelayedSnapshot();
        using var logger = new SessionLogger(new AppConfig(), source, true, SessionLogContractTests.NewRoot(), TimeSpan.FromMilliseconds(40));
        logger.UpdateSession(true, "active");
        await WaitFor(() => Records(logger.LogDirectory, "diagnostics").Any(IsPeriodic));
        source.Stall = true;
        logger.UpdateSession(false, "guest ended");
        var clock = Stopwatch.StartNew();
        await logger.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(clock.Elapsed < TimeSpan.FromSeconds(1), "Terminal capture exceeded its bounded cancellation budget.");
        var sample = Records(logger.LogDirectory, "diagnostics").Single(IsTerminal)["payload"]?["snapshot"];
        Check(sample?["installedGames"] == null && sample?["runningGames"] == null,
            "A failed inventory capture implied zero installed/running games.");
        Check(sample?["companion"]?["pid"]?.GetValue<int>() == Environment.ProcessId,
            "Failed terminal inventory omitted fresh local Companion resources.");
        Check(Records(logger.LogDirectory, "diagnostics").Any(row => Event(row) == "snapshotFailed"),
            "Terminal timeout did not record explicit capture failure.");
        Check(Records(logger.LogDirectory, "session").Any(row => Event(row) == "sessionEnded"), "Timeout prevented session closure.");
        var ended = Records(logger.LogDirectory, "session").Single(row => Event(row) == "sessionEnded");
        Check(Time(ended) - ended["payload"]!["observedEndAtUtc"]!.GetValue<DateTimeOffset>() >= TimeSpan.FromMilliseconds(20),
            "Observed session end time included the terminal capture delay.");
    }

    private static async Task LateOldCaptureCannotEnterReplacementSession()
    {
        var source = new LateSnapshot();
        using var logger = new SessionLogger(new AppConfig(), source, true, SessionLogContractTests.NewRoot(), TimeSpan.FromMilliseconds(40));
        logger.UpdateSession(true, "first", "1");
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = logger.LogDirectory;
        logger.UpdateSession(true, "replacement", "2");
        var replacement = logger.LogDirectory;
        await WaitFor(() => Records(first, "session").Any(row => Event(row) == "sessionEnded"));
        Check(source.Calls == 1, "A still-pending provider was overlapped by terminal or replacement capture.");
        source.Release.TrySetResult(new { marker = "OLD_ONLY", installedGames = Array.Empty<object>(), runningGames = Array.Empty<object>() });
        await WaitFor(() => Records(replacement, "diagnostics").Any(IsPeriodic));
        await logger.StopAsync();
        Check(!SessionLogContractTests.ReadAll(replacement).Contains("OLD_ONLY"), "A late old-session sample entered the new session writer.");
        Check(Records(replacement, "diagnostics").Any(IsTerminal), "Replacement session missed its own terminal capture.");
    }

    private static async Task FailedTerminalCapturePreservesBackendFallback()
    {
        var source = new MutableSnapshot();
        using var logger = new SessionLogger(new AppConfig(), source, true, SessionLogContractTests.NewRoot());
        logger.UpdateSession(true, "active");
        await WaitFor(() => Records(logger.LogDirectory, "diagnostics").Any(IsPeriodic));
        source.Released = true;
        source.Fail = true;
        await logger.StopAsync();
        var sample = Records(logger.LogDirectory, "diagnostics").Single(IsTerminal)["payload"]?["snapshot"];
        Check(sample?["companionBackends"]?["holding"]?.GetValue<bool>() == false,
            "Fallback failed to read the released backend state.");
        Check(sample?["snapshotFailed"]?.GetValue<string>() == "synthetic inventory failure", "Failure detail was lost.");
    }

    private static JsonObject[] Records(string directory, string category)
    {
        var path = Path.Combine(directory, category + ".jsonl");
        if (!File.Exists(path)) return [];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        var text = reader.ReadToEnd();
        // A running writer may still be completing its last line.
        return text.Split('\n').SkipLast(1).Where(line => line.Trim().Length > 0)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }

    private static string? Event(JsonObject row) => row["payload"]?["event"]?.GetValue<string>();
    private static bool IsPeriodic(JsonObject row) => row["payload"]?["phase"]?.GetValue<string>() == "periodic";
    private static bool IsTerminal(JsonObject row) => row["payload"]?["phase"]?.GetValue<string>() == "sessionEnd"
        && Event(row) is "machineSnapshot" or "gameSnapshot";
    private static DateTimeOffset Time(JsonObject row) => row["recordedAtUtc"]!.GetValue<DateTimeOffset>();
    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);
    private static async Task WaitFor(Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, budget.Token);
    }

    private sealed class MutableSnapshot : ISessionSnapshotSource, ITerminalSnapshotFallbackSource
    {
        public int State = 1;
        public bool Released, Fail;
        public ValueTask<object> CaptureAsync(CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("synthetic inventory failure");
            return ValueTask.FromResult<object>(new { companion = new { cpuSeconds = State },
                companionBackends = new { holding = !Released }, installedGames = new[] { new { name = "Game " + State } },
                runningGames = Array.Empty<object>() });
        }
        public object CaptureTerminalFallback(string error) => new { snapshotFailed = error,
            companionBackends = new { holding = !Released }, installedGames = (object?)null, runningGames = (object?)null };
    }

    private sealed class DelayedSnapshot : ISessionSnapshotSource
    {
        public bool Stall;
        public async ValueTask<object> CaptureAsync(CancellationToken cancellationToken)
        {
            if (Stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new { installedGames = Array.Empty<object>(), runningGames = Array.Empty<object>() };
        }
    }

    private sealed class LateSnapshot : ISessionSnapshotSource
    {
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<object> CaptureAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1) { Started.TrySetResult(); return await Release.Task; }
            return new { marker = "NEW", installedGames = Array.Empty<object>(), runningGames = Array.Empty<object>() };
        }
    }
}

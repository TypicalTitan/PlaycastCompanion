using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

internal sealed class MachineLogProjection
{
    public GameSnapshotView? LatestGames { get; }
    public IReadOnlyList<HealthSample> HealthSamples { get; }

    public MachineLogProjection(IEnumerable<LogRecord> records)
    {
        var health = new List<HealthSample>();
        foreach (var record in records)
        {
            if (record.EventType is not ("machineSnapshot" or "gameSnapshot")) continue;
            var snapshot = record.EventType == "machineSnapshot" ? LogJson.Get(record.Payload, "snapshot") : record.Payload;
            if (snapshot.ValueKind != JsonValueKind.Object) continue;
            if (LatestGames == null || record.RecordedAtUtc >= LatestGames.RecordedAtUtc)
                LatestGames = GameSnapshot(record, snapshot);
            if (record.EventType == "machineSnapshot")
            {
                var companion = LogJson.Get(snapshot, "companion");
                if (companion.ValueKind != JsonValueKind.Object) continue;
                health.Add(new(record.RecordedAtUtc, record.SessionKey,
                    LogJson.Integer(LogJson.Get(companion, "pid")),
                    LogJson.Metric(LogJson.Get(companion, "workingSetBytes")),
                    LogJson.Metric(LogJson.Get(companion, "cpuSeconds")), record));
            }
        }
        HealthSamples = health.OrderBy(sample => sample.RecordedAtUtc).ToArray();
    }

    private static GameSnapshotView GameSnapshot(LogRecord record, JsonElement snapshot)
    {
        var running = LogJson.Items(LogJson.Get(snapshot, "runningGames")).Select(RunningGame).ToArray();
        var runningKeys = running.Select(game => Key(game.Store, game.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = LogJson.Items(LogJson.Get(snapshot, "installedGames"))
            .Select(game => new InstalledGameView(LogJson.Text(game, "Store") ?? "unknown",
                LogJson.Text(game, "Id") ?? "", LogJson.Text(game, "Name") ?? "Unnamed game",
                LogJson.Text(game, "InstallRoot"), false))
            .GroupBy(game => Key(game.Store, game.Id), StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .Select(game => game with { IsRunning = runningKeys.Contains(Key(game.Store, game.Id)) }).ToArray();
        return new(record.RecordedAtUtc, record.SessionKey, installed, running,
            LogJson.Text(snapshot, "runningGameDetectionStatus") ?? "unknown", Coverage(snapshot), record);
    }

    private static RunningGameView RunningGame(JsonElement game) => new(
        LogJson.Text(game, "GameStore") ?? "unknown", LogJson.Text(game, "MatchedGameId") ?? "",
        LogJson.Text(game, "MatchedGameName") ?? LogJson.Text(game, "Name") ?? "Unnamed game",
        LogJson.Integer(LogJson.Get(game, "Pid")), LogJson.Metric(LogJson.Get(game, "WorkingSetBytes")),
        LogJson.Metric(LogJson.Get(game, "CpuSeconds")));

    private static string Key(string store, string id) => store + "|" + id;

    private static IReadOnlyList<string> Coverage(JsonElement snapshot)
    {
        var notes = new List<string>();
        foreach (var source in LogJson.Items(LogJson.Get(snapshot, "installedGameSources")))
        {
            var name = LogJson.Text(source, "Source") ?? "Inventory source";
            notes.Add(name + ": " + (LogJson.Text(source, "Status") ?? "unknown"));
            foreach (var error in LogJson.Items(LogJson.Get(source, "Errors")))
                if (LogJson.Text(error, "Message") is string message) notes.Add(name + ": " + message);
        }
        notes.AddRange(LogJson.Items(LogJson.Get(snapshot, "inventoryLimitations"))
            .Select(LogJson.Text).OfType<string>());
        var coverage = LogJson.Get(snapshot, "processCoverage");
        if (LogJson.Text(coverage, "Status") is string status) notes.Add("Process coverage: " + status);
        foreach (var error in LogJson.Items(LogJson.Get(coverage, "Errors")))
            if (LogJson.Text(error, "Message") is string message) notes.Add("Process coverage: " + message);
        var ids = LogJson.Items(LogJson.Get(snapshot, "targetSessionIds"))
            .Select(LogJson.Integer).OfType<int>().ToArray();
        if (ids.Length > 0) notes.Add("Observed Windows sessions: " + string.Join(", ", ids));
        return notes.Distinct(StringComparer.Ordinal).ToArray();
    }
}

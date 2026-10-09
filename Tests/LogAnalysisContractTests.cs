using System.Text;
using System.Text.Json.Nodes;
using PlaycastCompanion.LogAnalysis;

namespace PlaycastCompanion.Tests;

public static class LogAnalysisContractTests
{
    public static async Task RunAsync()
    {
        await ImportsRotationsProvenanceAndInvalidLines();
        await BoundsImportAndRecoversAfterOversizeLines();
        await RejectsOversizeSelectionAndStopsGrowingFiles();
        await CancellationPropagatesAndClosesStreams();
        await CorrelatesScriptsAndDeduplicatesCopies();
        await FindsNestedHttpScriptsAndPreservesUnknownRuntime();
        await DistinguishesNativeAcknowledgementLaunchAndCompletion();
        await ProjectsGamesAndUnavailableHealthMetrics();
    }

    private static async Task ImportsRotationsProvenanceAndInvalidLines()
    {
        var files = new FakeLogFiles();
        var payload = Envelope(new JsonObject { ["type"] = "MATCH_FOUND" }, 1735689600100);
        files.Add("realtime.002.jsonl", "\uFEFF" + Record(payload, "2025-01-01T00:00:02Z", true) + "\r\ninvalid\n"
            + Record(payload, "99999-01-01T00:00:00Z") + "\n");
        files.Add("diagnostics.jsonl", Record(new JsonObject { ["event"] = "machineSnapshot" }, "2025-01-01T00:00:01Z"));
        var result = await new SessionLogImporter(files).ImportAsync([files.Path("realtime.002.jsonl"),
            files.Path("REALTIME.002.JSONL"), files.Path("diagnostics.jsonl")]);
        Check(result.FilesImported == 2 && result.Records.Count == 2, "Duplicate paths must be read once and invalid records skipped.");
        var network = result.Records.Single(record => record.Category == "realtime");
        Check(network.LineNumber == 1 && network.SourceFile.EndsWith("realtime.002.jsonl")
            && network.ObservedCategory == "realtime" && network.EventType == "MATCH_FOUND", "Rotations and source provenance must be normalized.");
        Check(network.ObservedAtUtc == DateTimeOffset.FromUnixTimeMilliseconds(1735689600100)
            && network.IncludeSecrets && result.RawSecretRecordCount == 1, "Source and recording times and secret policy must be preserved.");
        Check(result.IssueCount == 2 && result.IsPartial && result.Issues[0].LineNumber == 2, "Invalid lines must have explicit line-numbered warnings.");
        Check(network.Raw.GetProperty("includeSecrets").GetBoolean(), "Imported JSON must remain valid after source documents are disposed.");
        Check(new SessionLogProjection(result).Records[0].Category == "diagnostics", "Timeline must use recording order rather than remote event order.");
    }

    private static async Task BoundsImportAndRecoversAfterOversizeLines()
    {
        var files = new FakeLogFiles();
        var valid = Record(new JsonObject { ["event"] = "observer_started" });
        files.Add("diagnostics.jsonl", new string('x', 501) + "\n" + valid + "\n" + valid + "\n");
        var result = await new SessionLogImporter(files, new(4096, 1, 300, 1)).ImportAsync([files.Path("diagnostics.jsonl")]);
        Check(result.Records.Count == 1 && result.Records[0].LineNumber == 2, "An oversized line must be skipped without losing the following valid line.");
        Check(result.IssueCount == 2 && result.Issues.Count == 1 && result.IsPartial,
            "Record and warning limits must be bounded while retaining the total warning count.");
    }

    private static async Task RejectsOversizeSelectionAndStopsGrowingFiles()
    {
        var files = new FakeLogFiles();
        files.Add("diagnostics.jsonl", new string('x', 301));
        var rejected = await new SessionLogImporter(files, new(300, 20, 100)).ImportAsync([files.Path("diagnostics.jsonl")]);
        Check(rejected.FilesImported == 0 && rejected.Records.Count == 0 && rejected.IsPartial,
            "Oversized selections must be rejected before opening files.");
        files.ReportedLength = 1;
        var growing = await new SessionLogImporter(files, new(300, 20, 100)).ImportAsync([files.Path("diagnostics.jsonl")]);
        Check(growing.BytesRead == 300 && growing.IsPartial && growing.Issues.Any(issue => issue.Message.Contains("growing")),
            "A file growing after preflight must still obey the total byte limit.");
        Check(files.LastStream?.WasDisposed == true, "Imported file streams must always close.");
    }

    private static async Task CancellationPropagatesAndClosesStreams()
    {
        var files = new FakeLogFiles();
        files.Add("session.jsonl", Record(new JsonObject { ["event"] = "sessionStarted" }));
        using var cancelled = new CancellationTokenSource();
        files.CancelOnOpen = cancelled;
        try { await new SessionLogImporter(files).ImportAsync([files.Path("session.jsonl")], cancelled.Token); }
        catch (OperationCanceledException)
        { Check(files.LastStream?.WasDisposed == true, "Cancelled imports must dispose open streams."); return; }
        throw new InvalidOperationException("Import cancellation must propagate to the caller.");
    }

    private static async Task CorrelatesScriptsAndDeduplicatesCopies()
    {
        var files = new FakeLogFiles();
        var deploy = new JsonObject { ["type"] = "PROVISIONING_SCRIPT_DEPLOY", ["deploymentId"] = "d1",
            ["executionId"] = "e1", ["stepIndex"] = 0, ["scriptName"] = "Install game", ["completeScript"] = "Write-Output 'hello'" };
        var started = new JsonObject { ["type"] = "PROVISIONING_SCRIPT_STARTED", ["deploymentId"] = "d1", ["executionId"] = "e1", ["stepIndex"] = 0 };
        var result = new JsonObject { ["type"] = "PROVISIONING_SCRIPT_RESULT", ["deploymentId"] = "d1", ["executionId"] = "e1", ["stepIndex"] = 0,
            ["status"] = "succeeded", ["exitCode"] = 0, ["stdout"] = "hello", ["stderr"] = "" };
        var revoke = new JsonObject { ["type"] = "PROVISIONING_SCRIPT_REVOKE", ["deploymentId"] = "d1" };
        files.Add("realtime.jsonl", Lines(Record(Envelope(deploy, 1735689600000)),
            Record(Envelope(started, 1735689601000), "2025-01-01T00:00:10Z"),
            Record(Envelope(result, 1735689606000), "2025-01-01T00:00:30Z"), Record(Envelope(revoke, 1735689607000))));
        files.Add("scripts.001.jsonl", Record(Copy(deploy, 1735689600000), "2025-01-01T00:00:01Z"));
        files.Add("second.jsonl", Record(Envelope(deploy, 1735689600000)), "session-b");
        var imported = await new SessionLogImporter(files).ImportAsync(files.Paths);
        var view = new SessionLogProjection(imported);
        var group = view.Scripts.Single(group => group.SessionKey.EndsWith("session-a"));
        Check(view.Scripts.Count == 2 && group.Records.Count == 4 && group.Records[0].Category == "scripts",
            "Dedicated script copies must deduplicate, prefer the script file, and remain isolated by session folder.");
        Check(group.ScriptText == "Write-Output 'hello'" && group.Status == "succeeded" && group.Stdout == "hello"
            && group.ObservedRuntime == TimeSpan.FromSeconds(5), "Full script bodies, explicit results, and observed runtime must survive projection.");
        Check(view.ScriptCancellationEvents.Count == 1 && group.Records.Any(record => record.EventType == "PROVISIONING_SCRIPT_REVOKE"),
            "Deployment-only cancellation must associate with existing groups without inventing an execution.");
        Check(view.CommunicationRecords.Count == 5, "Communication counts must describe stored realtime/IPC records, including source copies.");
    }

    private static async Task DistinguishesNativeAcknowledgementLaunchAndCompletion()
    {
        var files = new FakeLogFiles();
        files.Add("ipc.jsonl", Lines(
            Record(Envelope(Native("launchElevatedPowerShellScriptReceived", "ack", true, new() { ["received"] = true }), 1735689600000)),
            Record(Envelope(Native("launchElevatedPowerShellScript", "launch", true, new() { ["success"] = true, ["processId"] = 14 }), 1735689601000)),
            Record(Envelope(Native("launchElevatedPowerShellScript", "bad-launch", true, new() { ["success"] = false, ["error"] = "access denied" }), 1735689602000)),
            Record(Envelope(Native("launchElevatedPowerShellScript", "complete", true, new() { ["success"] = false, ["exitCode"] = 5, ["stderr"] = "bad" }), 1735689603000)),
            Record(Envelope(Native("cancelProvisioningScript", "cancel", true, new() { ["success"] = true, ["wasCancelled"] = false }), 1735689604000))));
        var view = new SessionLogProjection(await new SessionLogImporter(files).ImportAsync(files.Paths));
        Check(view.Scripts.Count == 4 && view.ScriptCancellationEvents.Count == 1, "Native cancellation replies must not be counted as executions.");
        Check(view.Scripts.Single(group => group.ExecutionId == "ack").Status == "Acknowledged"
            && view.Scripts.Single(group => group.ExecutionId == "launch").Status == "Launch accepted"
            && view.Scripts.Single(group => group.ExecutionId == "bad-launch").Status == "Launch failed",
            "An ACK and launch operation must not imply script completion.");
        Check(view.Scripts.Single(group => group.ExecutionId == "complete").Status == "failed"
            && view.Scripts.Single(group => group.ExecutionId == "launch").EndedAtUtc == null
            && view.Scripts.All(group => group.ObservedRuntime == null),
            "Terminal exit codes must indicate completion without guessing a native start time or joining different reply tags.");
    }

    private static async Task FindsNestedHttpScriptsAndPreservesUnknownRuntime()
    {
        var files = new FakeLogFiles();
        var deploy = new JsonObject { ["type"] = "PROVISIONING_SCRIPT_DEPLOY", ["deploymentId"] = "http-deployment",
            ["executionId"] = "http-execution", ["completeScript"] = "Get-Process" };
        var http = new JsonObject { ["method"] = "POST", ["url"] = "https://localhost/realtime",
            ["body"] = new JsonObject { ["messages"] = new JsonArray(deploy.DeepClone()) } };
        files.Add("realtime.jsonl", Record(Envelope(http, 1735689600000)));
        files.Add("scripts.jsonl", Lines(Record(Copy(deploy, 1735689600000)),
            Record(new JsonObject { ["event"] = "scriptCommunication", ["message"] = new JsonObject
            { ["type"] = "PROVISIONING_SCRIPT_STARTED", ["deploymentId"] = "http-deployment", ["executionId"] = "http-execution" } }, "2025-01-01T00:00:04Z"),
            Record(new JsonObject { ["event"] = "scriptCommunication", ["message"] = new JsonObject
            { ["type"] = "PROVISIONING_SCRIPT_RESULT", ["deploymentId"] = "http-deployment", ["executionId"] = "http-execution", ["status"] = "failed" } }, "2025-01-01T00:00:10Z")));
        var view = new SessionLogProjection(await new SessionLogImporter(files).ImportAsync(files.Paths));
        Check(view.Scripts.Count == 1 && view.Scripts[0].Records.Count == 3 && view.Scripts[0].ScriptText == "Get-Process",
            "Nested HTTP messages must correlate with extracted script copies.");
        Check(view.Scripts[0].Status == "failed" && view.Scripts[0].ObservedRuntime == null,
            "Missing source timestamps must not turn capture latency into measured script runtime.");
    }

    private static async Task ProjectsGamesAndUnavailableHealthMetrics()
    {
        var files = new FakeLogFiles();
        var snapshot = JsonNode.Parse("""
            {"installedGames":[{"Store":"steam","Id":"42","Name":"Game","InstallRoot":"C:\\games"}],
             "runningGames":[{"GameStore":"steam","MatchedGameId":"42","MatchedGameName":"Game","Pid":7,"WorkingSetBytes":{"Value":1048576,"Status":"available"},"CpuSeconds":{"Value":12,"Status":"available"}},
                             {"GameStore":"steam","MatchedGameId":"42","MatchedGameName":"Game","Pid":8,"WorkingSetBytes":{"Value":null,"Status":"access_denied"}}],
             "runningGameDetectionStatus":"partial","installedGameSources":[{"Source":"Steam","Status":"available","EntriesRead":1,"Errors":[]}],
             "inventoryLimitations":["Other stores are not scanned."],"processCoverage":{"Status":"partial","Errors":[{"Message":"Access denied for one process."}]},
             "companion":{"pid":19,"workingSetBytes":{"Value":null,"Status":"access_denied"},"cpuSeconds":{"Value":99,"Status":"unavailable"}}}
            """)!.AsObject();
        files.Add("diagnostics.jsonl", Record(new JsonObject { ["event"] = "machineSnapshot", ["snapshot"] = snapshot }));
        var view = new SessionLogProjection(await new SessionLogImporter(files).ImportAsync(files.Paths));
        Check(view.LatestGames is { RunningGameCount: 1, RunningProcessCount: 2, DetectionStatus: "partial" }
            && view.LatestGames.InstalledGames[0].IsRunning, "Game counts must distinguish unique games from matching processes.");
        Check(view.LatestGames!.CoverageNotes.Any(note => note.Contains("Access denied"))
            && view.HealthSamples.Count == 1 && view.HealthSamples[0].WorkingSetBytes == null && view.HealthSamples[0].CpuSeconds == null,
            "Partial coverage and unavailable metrics must remain unknown rather than becoming zero.");
        files.Add("games.003.jsonl", Record(new JsonObject { ["event"] = "gameSnapshot", ["installedGames"] = new JsonArray(),
            ["runningGames"] = new JsonArray(), ["runningGameDetectionStatus"] = "unknown_session_inventory" }, "2025-01-01T00:00:02Z"));
        view = new SessionLogProjection(await new SessionLogImporter(files).ImportAsync(files.Paths));
        Check(view.LatestGames is { RunningProcessCount: 0, DetectionStatus: "unknown_session_inventory" },
            "Latest unavailable snapshots must display their status instead of silently falling back to old inventory.");
    }

    private static JsonObject Envelope(JsonObject message, long time) => new()
    { ["version"] = 1, ["category"] = "realtime", ["direction"] = "received", ["timestamp"] = time,
        ["transport"] = "websocket", ["payload"] = message.DeepClone() };
    private static JsonObject Copy(JsonObject message, long time)
    {
        var source = Envelope(message, time); source.Remove("payload");
        return new() { ["event"] = "scriptCommunication", ["source"] = source, ["message"] = message.DeepClone() };
    }
    private static JsonObject Native(string action, string tag, bool reply, JsonObject message) => new()
    { ["header"] = new JsonObject { ["action"] = action, ["tag"] = tag, ["isReply"] = reply },
        ["body"] = new JsonObject { ["message"] = message } };
    private static string Record(JsonObject payload, string time = "2025-01-01T00:00:00Z", bool raw = false) =>
        new JsonObject { ["recordedAtUtc"] = time, ["includeSecrets"] = raw, ["payload"] = payload.DeepClone() }.ToJsonString();
    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";
    private static void Check(bool condition, string message) => SessionLogContractTests.Check(condition, message);
}

internal sealed class FakeLogFiles : ILogImportFileSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Paths => _files.Keys;
    public long? ReportedLength { get; set; }
    public CancellationTokenSource? CancelOnOpen { get; set; }
    public TrackingLogStream? LastStream { get; private set; }
    public string Path(string name, string session = "session-a") => System.IO.Path.Combine("C:\\fixtures", session, name);
    public void Add(string name, string content, string session = "session-a") => _files[Path(name, session)] = Encoding.UTF8.GetBytes(content);
    public long GetLength(string path) => ReportedLength ?? _files[path].Length;
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
    {
        LastStream = new(_files[path]);
        CancelOnOpen?.Cancel();
        return ValueTask.FromResult<Stream>(LastStream);
    }
}

internal sealed class TrackingLogStream(byte[] content) : MemoryStream(content, false)
{
    public bool WasDisposed { get; private set; }
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
}

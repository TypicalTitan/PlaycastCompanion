using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaycastCompanion.Tests;

public static class SessionLogContractTests
{
    public static void Run()
    {
        RedactsNestedSecretsAndPreservesUsefulFields();
        RedactsScriptAssignmentsArgumentsAndHereStrings();
        IncludesSecretsOnlyWhenRequested();
        RedactsSecureStorageValues();
        RedactsCredentialUrlsAndSignedCapabilities();
        PreservesLargeSafeText();
        RotatesRecordsAndBoundsSessionStorage();
        RejectsOversizeRecordsWithExplicitMarker();
        SurfacesStorageFailures();
        RetainsUnrelatedDirectories();
        ClassifiesScriptsAndNativeEvents();
    }

    private static void RedactsCredentialUrlsAndSignedCapabilities()
    {
        var text = "https://username:opaque-password@localhost/game?X-Amz-Credential=credential-value&X-Amz-Signature=signature-value&X-Amz-Security-Token=security-value&game=keep";
        var safe = new SessionLogRedactor().RedactText(text);
        foreach (var value in new[] { "opaque-password", "credential-value", "signature-value", "security-value" })
            Check(!safe.Contains(value), "Capability URLs must redact " + value);
        Check(safe.Contains("localhost/game") && safe.Contains("game=keep"), "Capability redaction must preserve safe URL context.");
        var policy = new SessionLogRedactor().Redact(new { includeSecrets = true })!.ToJsonString();
        Check(policy.Contains("true"), "Boolean secret-capture policy metadata must remain readable.");
    }

    private static void PreservesLargeSafeText()
    {
        var text = new string('x', 100000);
        Check(new SessionLogRedactor().RedactText(text) == text, "Large safe script/output text must not be lost to expensive regex scanning.");
    }

    private static void RedactsSecureStorageValues()
    {
        var message = JsonNode.Parse("""
            {"header":{"action":"secureStorageGet"},"body":{"message":{"key":"auth","succeeded":true,"value":"opaque-credential-value"}}}
            """)!;
        var redactor = new SessionLogRedactor();
        var safe = redactor.Redact(message)!.ToJsonString();
        Check(!safe.Contains("opaque-credential-value") && safe.Contains("secureStorageGet") && safe.Contains("succeeded"),
            "Secure-storage results must redact opaque values and retain useful reply metadata.");
        Check(redactor.Redact(message, true)!.ToJsonString().Contains("opaque-credential-value"), "Debug capture must retain requested secure-storage values.");
    }

    private static void RedactsNestedSecretsAndPreservesUsefulFields()
    {
        var secret = new
        {
            accessToken = "secret-access", auth = new { password = "secret-password" },
            wire = "{\"clientSecret\":\"secret-client\",\"game\":\"Game Name\"}",
            header = "Authorization: Bearer secret-header",
            url = "https://localhost/path?api_key=secret-key&game=useful",
            jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.SignatureXYZ",
            users = new[] { new { username = "guest", auth = "opaque-auth-value", token = "secret-array-token" } },
            game = "Game Name", executionId = "execution-123",
        };
        var result = new SessionLogRedactor().Redact(secret)!.ToJsonString();
        foreach (var value in new[] { "secret-access", "secret-password", "secret-client", "secret-header", "secret-key", "SignatureXYZ", "opaque-auth-value", "secret-array-token" })
            Check(!result.Contains(value), "Default capture must remove " + value);
        Check(result.Contains("Game Name") && result.Contains("execution-123") && result.Contains("useful"),
            "Redaction must preserve games, execution correlation, and safe query values.");
    }

    private static void RedactsScriptAssignmentsArgumentsAndHereStrings()
    {
        var script = """
            $env:PLAYCAST_PROV_VAR_API_KEY = 'secret-one'
            $Password = "secret-two"
            Write-Output -Token 'secret-three' -Game 'Useful Game'
            $ClientSecret = @'
            secret-four
            multiple lines
            '@
            Invoke-Command --password=secret-five
            $AuthHeader = 'Bearer secret-six'
            """;
        var result = new SessionLogRedactor().RedactText(script);
        foreach (var value in new[] { "secret-one", "secret-two", "secret-three", "secret-four", "secret-five", "secret-six" })
            Check(!result.Contains(value), "Script secret must be removed: " + value);
        Check(result.Contains("Useful Game") && result.Contains("Invoke-Command"), "Script structure must remain inspectable.");
    }

    private static void IncludesSecretsOnlyWhenRequested()
    {
        var payload = new { token = "raw-value", completeScript = "$Password = 'raw-password'" };
        var redactor = new SessionLogRedactor();
        Check(redactor.Redact(payload, true)!.ToJsonString().Contains("raw-value"), "Debug override must retain requested secrets.");
        Check(!redactor.Redact(payload)!.ToJsonString().Contains("raw-password"), "Override must not change the default policy.");
    }

    private static void RotatesRecordsAndBoundsSessionStorage()
    {
        var root = NewRoot();
        var config = new SessionLoggingConfig { MaxFileBytes = 5000, MaxSessionBytes = 22000 };
        using var writer = new SessionLogWriter(config, root);
        for (var index = 0; index < 40; index++)
            writer.Write("realtime", new { index, content = new string('x', 1700) });
        var files = Directory.GetFiles(writer.DirectoryPath);
        Check(files.Any(file => Path.GetFileName(file).StartsWith("realtime.001")), "Large category streams must rotate.");
        Check(files.All(file => new FileInfo(file).Length <= config.MaxFileBytes), "Every file must remain within its cap.");
        Check(files.Sum(file => new FileInfo(file).Length) <= config.MaxSessionBytes, "Session storage must remain bounded.");
        Check(ReadAll(writer.DirectoryPath).Contains("sessionStorageLimitReached"), "Storage limits must be recorded explicitly.");
        Check(writer.LastError?.Contains("limit reached") == true, "Storage-limit loss must be visible to the UI.");
        writer.Dispose();
        foreach (var file in files)
            foreach (var line in File.ReadLines(file)) using (JsonDocument.Parse(line)) { }
    }

    private static void RejectsOversizeRecordsWithExplicitMarker()
    {
        using var writer = new SessionLogWriter(new SessionLoggingConfig { MaxFileBytes = 4096, MaxSessionBytes = 20000 }, NewRoot());
        Check(!writer.Write("realtime", new { content = new string('x', 5000) }), "Oversize records must not be accepted.");
        Check(ReadAll(writer.DirectoryPath).Contains("recordRejected"), "Oversize records need an explicit loss marker.");
        Check(writer.Write("../../bad", new { safe = true }), "Unknown categories should fall back to diagnostics.");
        Check(Directory.GetFiles(writer.DirectoryPath).All(path => Path.GetDirectoryName(path) == writer.DirectoryPath),
            "Untrusted category text must never influence file paths.");
    }

    private static void SurfacesStorageFailures()
    {
        var root = NewRoot();
        var file = Path.Combine(root, "blocked");
        File.WriteAllText(file, "file");
        using var writer = new SessionLogWriter(new SessionLoggingConfig(), file);
        Check(!writer.Write("session", new { active = true }) && writer.LastError != null,
            "Storage errors must be observable and must not crash the app.");
    }

    private static void RetainsUnrelatedDirectories()
    {
        var root = NewRoot();
        var unrelated = Directory.CreateDirectory(Path.Combine(root, "unrelated-old-folder"));
        File.WriteAllText(Path.Combine(unrelated.FullName, "keep.txt"), "keep");
        unrelated.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-60);
        var old = Directory.CreateDirectory(Path.Combine(root, "20200101T000000.000Z_" + Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(old.FullName, "old.jsonl"), "{}");
        old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-60);
        using var writer = new SessionLogWriter(new SessionLoggingConfig { RetentionDays = 14 }, root);
        Check(Directory.Exists(unrelated.FullName), "Retention must preserve unrelated directories.");
        Check(!Directory.Exists(old.FullName), "Expired logger-generated sessions should be removed.");
    }

    private static void ClassifiesScriptsAndNativeEvents()
    {
        var envelope = JsonNode.Parse("""
            {"version":1,"category":"realtime","direction":"received","timestamp":1,"payload":{"body":"{\"type\":\"PROVISIONING_SCRIPT_DEPLOY\",\"executionId\":\"run1\",\"completeScript\":\"Write-Output hello\"}"}}
            """)!.AsObject();
        var records = new DiagnosticEventClassifier().RelatedRecords(envelope).ToArray();
        Check(records.Length == 1 && records[0].Category == "scripts", "Nested wire JSON scripts must receive a scripts record.");
        Check(records[0].Record.ToJsonString().Contains("run1") && records[0].Record.ToJsonString().Contains("Write-Output hello"),
            "Script records must retain the complete script and execution correlation.");
        envelope["payload"] = new JsonObject { ["action"] = "enumApplications" };
        Check(new DiagnosticEventClassifier().RelatedRecords(envelope).Single().Category == "games", "Native inventories need a games record.");
        envelope["payload"] = JsonNode.Parse("""
            {"header":{"action":"enumRunning","messageId":"native-id"},"body":{"message":{"applications":[{"name":"Running Game"}]}}}
            """);
        var native = new DiagnosticEventClassifier().RelatedRecords(envelope).Single();
        Check(native.Category == "games" && native.Record.ToJsonString().Contains("Running Game")
            && native.Record.ToJsonString().Contains("native-id"), "Native game records must preserve header correlation and reply body together.");
        envelope["payload"] = JsonNode.Parse("""
            {"type":"PROVISIONING_SCRIPT_DEPLOY","executionId":"full-run","body":{"completeScript":"Write-Host whole-script"}}
            """);
        var scripts = new DiagnosticEventClassifier().RelatedRecords(envelope).ToArray();
        Check(scripts.Length == 1 && scripts[0].Record.ToJsonString().Contains("full-run")
            && scripts[0].Record.ToJsonString().Contains("whole-script"), "A complete provisioning wire message must produce one correlated script record.");
        envelope["payload"] = new JsonObject { ["channel"] = "processCatalogState", ["value"] = "catalog" };
        Check(new DiagnosticEventClassifier().RelatedRecords(envelope).Single().Category == "games", "Native state catalogs need a games record.");
    }

    internal static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "playcast-logger-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string ReadAll(string directory) => string.Join("\n", Directory.GetFiles(directory, "*.jsonl").Select(ReadLog));
    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

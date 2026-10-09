using System.Security.Cryptography;
using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

internal sealed class ScriptLogProjection
{
    public IReadOnlyList<ScriptExecutionGroup> Groups { get; }
    public IReadOnlyList<LogRecord> Cancellations { get; }

    public ScriptLogProjection(IEnumerable<LogRecord> records)
    {
        var events = UniqueEvents(records);
        var groups = new Dictionary<string, ScriptGroupBuilder>(StringComparer.Ordinal);
        var cancellations = new List<LogRecord>();
        foreach (var item in events)
        {
            var execution = LogJson.Text(LogJson.Find(item.Message, "executionId"));
            if (item.Type == "cancelProvisioningScript" || item.Type == "PROVISIONING_SCRIPT_REVOKE")
            { cancellations.Add(item.Record); continue; }
            var key = Key(item, execution);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new ScriptGroupBuilder(key, item, execution);
            group.Apply(item);
        }
        foreach (var revoke in events.Where(item => item.Type == "PROVISIONING_SCRIPT_REVOKE"))
        {
            var deployment = LogJson.Text(LogJson.Find(revoke.Message, "deploymentId"));
            if (deployment == null) continue;
            foreach (var group in groups.Values.Where(group => group.SessionKey == revoke.Record.SessionKey
                && group.DeploymentId == deployment)) group.ApplyRevoke(revoke);
        }
        Groups = groups.Values.Select(group => group.Build()).OrderBy(group => group.FirstAtUtc).ToArray();
        Cancellations = cancellations;
    }

    private static string Key(ScriptObservedEvent item, string? execution)
    {
        var deployment = LogJson.Text(LogJson.Find(item.Message, "deploymentId")) ?? "";
        var step = LogJson.Integer(LogJson.Find(item.Message, "stepIndex"));
        var tag = LogJson.Text(LogJson.Get(item.Message, "header"), "tag");
        var correlation = execution != null ? $"execution:{deployment}:{execution}:{step}"
            : tag != null ? "native:" + tag : "unmatched:" + item.Record.Id;
        return item.Record.SessionKey + "|" + correlation;
    }

    private static IReadOnlyList<ScriptObservedEvent> UniqueEvents(IEnumerable<LogRecord> records)
    {
        var events = new Dictionary<string, ScriptObservedEvent>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var message = FindScriptMessage(record.Message);
            if (message.ValueKind == JsonValueKind.Undefined) continue;
            var type = LogRecordNormalizer.MessageType(message) ?? "";
            var envelope = LogJson.Get(record.Payload, "source");
            if (LogJson.Get(record.Payload, "version").ValueKind != JsonValueKind.Undefined) envelope = record.Payload;
            var stamp = record.ObservedAtUtc?.ToString("O") ?? record.RecordedAtUtc.ToString("O");
            var identity = record.SessionKey + "|" + stamp + "|" + LogJson.Text(envelope, "direction")
                + "|" + LogJson.Text(envelope, "transport") + "|" + Fingerprint(message);
            if (!events.TryGetValue(identity, out var prior) || record.Category == "scripts" && prior.Record.Category != "scripts")
                events[identity] = new(record, message, type);
        }
        return events.Values.OrderBy(item => item.Record.EventTimeUtc).ThenBy(item => item.Record.Id).ToArray();
    }

    private static JsonElement FindScriptMessage(JsonElement root)
    {
        foreach (var message in LogJson.Walk(root))
        {
            var type = LogRecordNormalizer.MessageType(message);
            if (type?.StartsWith("PROVISIONING_SCRIPT_", StringComparison.Ordinal) == true
                || type is "launchElevatedPowerShellScript" or "launchElevatedPowerShellScriptReceived"
                    or "cancelProvisioningScript") return message;
        }
        return default;
    }

    private static string Fingerprint(JsonElement message)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory)) WriteCanonical(writer, message);
        return Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}

internal sealed record ScriptObservedEvent(LogRecord Record, JsonElement Message, string Type);

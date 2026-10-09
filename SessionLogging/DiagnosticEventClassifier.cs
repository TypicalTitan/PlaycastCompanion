using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaycastCompanion;

/// <summary>Finds script and native application events inside wire-format or embedded JSON messages.</summary>
public sealed class DiagnosticEventClassifier
{
    private static readonly string[] EventProperties = ["type", "action", "event", "method", "operation", "channel"];

    public IEnumerable<(string Category, JsonObject Record)> RelatedRecords(JsonObject envelope)
    {
        foreach (var message in Walk(envelope["payload"], 0))
        {
            var names = EventNames(message).ToArray();
            var hasScript = message["completeScript"] is JsonValue || message["script"] is JsonValue;
            if (hasScript || names.Any(IsScriptEvent))
                yield return ("scripts", Related(envelope, message, "scriptCommunication"));
            if (names.Any(IsGameEvent))
                yield return ("games", Related(envelope, message, "nativeApplicationInventory"));
            if (names.Any(name => name.Equals("modeChanged", StringComparison.OrdinalIgnoreCase)
                || name.Equals("playjectorHostModeState", StringComparison.OrdinalIgnoreCase)))
                yield return ("session", Related(envelope, message, "nativeModeChanged"));
        }
    }

    private static IEnumerable<string> EventNames(JsonObject message)
    {
        foreach (var property in EventProperties)
            if (Text(message[property]) is string name) yield return name;
        var bodyMessage = message["body"] is JsonObject body ? body["message"] : null;
        foreach (var node in new[] { message["header"], message["message"], bodyMessage })
            if (node is JsonObject obj && Text(obj["action"]) is string action) yield return action;
    }

    private static bool IsGameEvent(string name) =>
        name.Equals("enumApplications", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("enumRunning", StringComparison.OrdinalIgnoreCase)
        || name.Equals("listApplications", StringComparison.OrdinalIgnoreCase)
        || name.Contains("runningApplications", StringComparison.OrdinalIgnoreCase)
        || name is "processCatalogState" or "runningApplicationState" or "playjectorHostModeState";

    private static JsonObject Related(JsonObject envelope, JsonObject message, string eventName)
    {
        var source = new JsonObject();
        foreach (var property in envelope.Where(property => property.Key != "payload"))
            source[property.Key] = property.Value?.DeepClone();
        return new JsonObject
        {
            ["event"] = eventName,
            ["source"] = source,
            ["message"] = message.DeepClone(),
        };
    }

    private static bool IsScriptEvent(string name) =>
        name.StartsWith("PROVISIONING_SCRIPT_", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cancelProvisioningScript", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonObject> Walk(JsonNode? node, int depth)
    {
        if (depth > 32) yield break;
        if (node is JsonObject obj)
        {
            yield return obj;
            // Keep the wire message whole: classifying header/body separately loses
            // native reply data and creates duplicate provisioning-script records.
            if (obj["header"] is JsonObject || EventProperties.Any(property => Text(obj[property]) != null))
                yield break;
            foreach (var child in obj.Select(property => property.Value))
                foreach (var nested in Walk(child, depth + 1)) yield return nested;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                foreach (var nested in Walk(child, depth + 1)) yield return nested;
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var trimmed = text.AsSpan().Trim();
            if (trimmed.Length < 2 || trimmed[0] is not ('{' or '[')) yield break;
            JsonNode? embedded = null;
            try { embedded = JsonNode.Parse(text); }
            catch (JsonException) { }
            foreach (var nested in Walk(embedded, depth + 1)) yield return nested;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

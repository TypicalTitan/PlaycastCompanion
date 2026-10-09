using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

internal sealed class LogRecordNormalizer
{
    public LogRecord Normalize(JsonElement raw, string file, int line, int id)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON record object.");
        var owned = raw.Clone();
        var payload = LogJson.Get(owned, "payload");
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) payload = owned;
        var envelope = LogJson.Integer(LogJson.Get(payload, "version")) == 1 ? payload : LogJson.Get(payload, "source");
        var message = LogJson.Get(payload, "message");
        if (message.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            message = LogJson.Text(payload, "category") != null ? LogJson.Get(payload, "payload") : payload;
        message = LogJson.ParseEmbedded(message);
        var observed = LogJson.Time(LogJson.Get(envelope, "timestamp"));
        var storedValue = LogJson.Get(owned, "recordedAtUtc");
        var stored = storedValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? LogJson.Time(storedValue) : observed ?? LogJson.Time(LogJson.Get(LogJson.Get(payload, "snapshot"), "capturedAtUtc"));
        if (stored == null) throw new FormatException("Missing or invalid timestamp.");
        var eventName = LogJson.Text(payload, "event");
        var observedCategory = LogJson.Text(envelope, "category") ?? "diagnostics";
        var category = FilenameCategory(file) ?? InferCategory(eventName, observedCategory);
        var type = MessageType(message) ?? eventName ?? "Record";
        return new(id, category, observedCategory, stored.Value, observed, type, file, line,
            Path.GetDirectoryName(file) ?? "Imported selection",
            LogJson.Boolean(LogJson.Get(owned, "includeSecrets")) == true, owned, payload, message);
    }

    internal static string? MessageType(JsonElement message) =>
        LogJson.Text(message, "type") ?? LogJson.Text(LogJson.Get(message, "header"), "action")
        ?? LogJson.Text(message, "kind") ?? LogJson.Text(message, "channel") ?? LogJson.Text(message, "event");

    private static string InferCategory(string? name, string category) => name switch
    {
        "scriptCommunication" => "scripts", "gameSnapshot" or "nativeApplicationInventory" => "games",
        "sessionStarted" or "sessionEnded" or "nativeModeChanged" or "secretCaptureChanged" => "session",
        _ => category == "state" ? "diagnostics" : category,
    };

    private static string? FilenameCategory(string file)
    {
        if (!Path.GetExtension(file).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = Path.GetFileNameWithoutExtension(file).Split('.');
        if (parts.Length > 2 || parts.Length == 2 && (!int.TryParse(parts[1], out var rotation) || rotation < 0)) return null;
        var category = parts[0].ToLowerInvariant();
        return category is "realtime" or "ipc" or "scripts" or "games" or "diagnostics" or "session" ? category : null;
    }
}

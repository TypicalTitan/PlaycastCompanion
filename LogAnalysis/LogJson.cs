using System.Globalization;
using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

internal static class LogJson
{
    public static JsonElement Get(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    public static string? Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    public static string? Text(JsonElement element, string name) => Text(Get(element, name));
    public static bool? Boolean(JsonElement element) => element.ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    public static int? Integer(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

    public static double? Number(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;

    public static double? Metric(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return Number(element);
        var status = Text(element, "Status");
        return status != null && !status.Equals("available", StringComparison.OrdinalIgnoreCase)
            ? null : Number(Get(element, "Value"));
    }

    public static JsonElement Find(JsonElement root, string property)
    {
        foreach (var item in Walk(root))
        {
            var value = Get(item, property);
            if (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) return value;
        }
        return default;
    }

    public static IEnumerable<JsonElement> Walk(JsonElement root)
    {
        var queue = new Queue<JsonElement>();
        queue.Enqueue(root);
        for (var count = 0; queue.Count > 0 && count < 150; count++)
        {
            var item = ParseEmbedded(queue.Dequeue());
            yield return item;
            IEnumerable<JsonElement> children = item.ValueKind switch
            {
                JsonValueKind.Object => item.EnumerateObject().Select(property => property.Value),
                JsonValueKind.Array => item.EnumerateArray(), _ => [],
            };
            foreach (var child in children)
            {
                if (queue.Count >= 150) break;
                if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
                    queue.Enqueue(child);
            }
        }
    }

    public static JsonElement ParseEmbedded(JsonElement element)
    {
        var text = Text(element);
        if (string.IsNullOrWhiteSpace(text)) return element;
        var trimmed = text.AsSpan().Trim();
        if (trimmed[0] is not ('{' or '[')) return element;
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return json.RootElement.Clone();
        }
        catch (JsonException) { return element; }
    }

    public static DateTimeOffset? Time(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return date;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var milliseconds)) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}

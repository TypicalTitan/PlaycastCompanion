using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlaycastCompanion;

/// <summary>Transforms structured and embedded JSON, HTTP credentials, and common script secrets.</summary>
public sealed class SessionLogRedactor
{
    public const string Redacted = "[REDACTED]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex SensitiveName = Pattern(
        @"(?:password|passwd|pwd|secret|credential|token|authorization|authentication|cookie|privatekey|apikey|accesskey|connectionstring|clientkey|signingkey|encryptionkey|^auth$|^key$)");
    private static readonly Regex Bearer = Pattern(@"\b(Bearer|Basic)\s+[A-Za-z0-9+/_.=\-]+");
    private static readonly Regex Jwt = Pattern(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b");
    private const string SecretWord = @"(?:password|passwd|pwd|secret|credential|token|authorization|api[_-]?key|access[_-]?key|private[_-]?key|client[_-]?key)";
    private static readonly Regex HereString = Pattern(
        "(?<prefix>(?<![\\w$.:])[\\$\\w.:]*" + SecretWord + "[\\w.:]*\\s*=\\s*)@(?<quote>['\"])\\r?\\n[\\s\\S]*?\\r?\\n\\k<quote>@");
    private static readonly Regex Assignment = Pattern(
        "(?<prefix>(?<![\\w$.:])[\\$\\w.:]*" + SecretWord + "[\\w.:]*\\s*(?:=|:)\\s*)(?<value>\"(?:`.|\\\\.|[^\"])*\"|'(?:''|[^'])*'|[^\\s;,}&]+)");
    private static readonly Regex Argument = Pattern(
        "(?<prefix>(?<![\\w?&/:-])(?:-{1,2}|/)" + SecretWord + "[\\w_-]*\\s*(?:[=:]|\\s)\\s*)(?<value>\"(?:`.|\\\\.|[^\"])*\"|'(?:''|[^'])*'|[^\\s;,}&]+)");
    private static readonly Regex QuerySecret = Pattern(
        @"(?<prefix>[?&](?:password|secret|token|access_token|refresh_token|api_key|apikey|key|signature|sig|credential|x-amz-(?:credential|signature|security-token)|x-goog-(?:credential|signature))=)[^&\s\""']+");
    private static readonly Regex UrlUserInfo = Pattern(@"(?<prefix>(?<![a-z0-9+.-])[a-z][a-z0-9+.-]*://)[^/\s@]+@");
    private static readonly Regex PrivateKey = Pattern(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----");

    public JsonNode? Redact(object? value, bool includeSecrets = false)
    {
        var node = value is JsonNode json ? json.DeepClone() : JsonSerializer.SerializeToNode(value);
        return includeSecrets ? node : Visit(node, 0);
    }

    public string RedactText(string text)
    {
        try
        {
            var result = PrivateKey.Replace(text, Redacted);
            result = UrlUserInfo.Replace(result, "${prefix}" + Redacted + "@");
            result = Bearer.Replace(result, "$1 " + Redacted);
            result = Jwt.Replace(result, Redacted);
            result = HereString.Replace(result, "${prefix}" + Redacted);
            result = Assignment.Replace(result, "${prefix}" + Redacted);
            result = Argument.Replace(result, "${prefix}" + Redacted);
            return QuerySecret.Replace(result, "${prefix}" + Redacted);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED: text exceeded safe redaction time]";
        }
    }

    private JsonNode? Visit(JsonNode? node, int depth)
    {
        if (depth > 64)
            return JsonValue.Create("[REDACTED: nested payload depth limit]");
        if (node is JsonObject obj)
        {
            RedactSecureStorage(obj);
            foreach (var name in obj.Select(pair => pair.Key).ToArray())
            {
                var original = obj[name];
                var policyFlag = name.Equals("includeSecrets", StringComparison.OrdinalIgnoreCase)
                    && original is JsonValue flag && flag.TryGetValue<bool>(out _);
                var replacement = IsSensitive(name) && !policyFlag ? JsonValue.Create(Redacted) : Visit(original, depth + 1);
                if (!ReferenceEquals(original, replacement)) obj[name] = replacement;
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var original = array[index];
                var replacement = Visit(original, depth + 1);
                if (!ReferenceEquals(original, replacement)) array[index] = replacement;
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(RedactString(text, depth));
        return node;
    }

    private static void RedactSecureStorage(JsonObject obj)
    {
        var header = obj["header"] as JsonObject;
        var action = StringValue(header?["action"]) ?? StringValue(obj["action"]);
        if (action?.Contains("secureStorage", StringComparison.OrdinalIgnoreCase) != true) return;
        var body = obj["body"] as JsonObject;
        foreach (var message in new[] { obj, obj["message"] as JsonObject, body, body?["message"] as JsonObject })
            if (message?.ContainsKey("value") == true) message["value"] = Redacted;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private string RedactString(string text, int depth)
    {
        if (text.Length <= SessionDiagnosticsListener.MaxFrameBytes)
        {
            var trimmed = text.AsSpan().Trim();
            if (trimmed.Length > 1 && (trimmed[0] is '{' or '[' or '"'))
            {
                try
                {
                    var embedded = JsonNode.Parse(text);
                    return Visit(embedded, depth + 1)?.ToJsonString() ?? "null";
                }
                catch (JsonException) { }
            }
        }
        return RedactText(text);
    }

    private static bool IsSensitive(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return SensitiveName.IsMatch(normalized);
    }

    private static Regex Pattern(string expression) =>
        new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
}

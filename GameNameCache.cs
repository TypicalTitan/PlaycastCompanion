using System.Text.Json;

namespace PlaycastCompanion;

/// <summary>
/// Owner-side resolver: turns a captured Discord Application ID into the display
/// name Discord itself would show, with a four-step ladder — persistent cache,
/// the unauthenticated Discord app-registry endpoint, an exe-name hint, then a
/// configured fallback. App-id → name is effectively immutable, so the cache is
/// long-lived and collapses network calls to ~one per distinct game ever seen
/// (and resolves previously-seen games entirely offline).
///
/// The cache is persisted to %LOCALAPPDATA% (writable), NOT config.json — the
/// installed config lives under Program Files and only saves via a
/// UAC-prompting elevated path, which a background resolver must never trigger.
/// </summary>
public sealed class GameNameCache
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlaycastCompanion", "resolved-names.json");

    // Version-info strings that are engines/launchers/OS, not the game itself.
    private static readonly string[] GenericHints =
    [
        "microsoft windows operating system", "unreal engine", "unity",
        "steam", "steamworks", "epic games", "launcher", "godot",
    ];

    private readonly DiscordConfig _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public GameNameCache(DiscordConfig cfg)
    {
        _cfg = cfg;
        // seed (admin-provisioned, read-only) sits UNDER the live cache
        foreach (var kv in cfg.ResolvedNames)
            _cache[kv.Key] = kv.Value;
        LoadDisk();
    }

    /// <summary>Resolve an Application ID (with an optional exe-name hint) to a display name.</summary>
    public async Task<string> ResolveAsync(string? clientId, string? procHint)
    {
        clientId = clientId?.Trim();
        if (!string.IsNullOrEmpty(clientId))
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(clientId, out var cached))
                    return cached;
            }
            var fromApi = await QueryDiscordAsync(clientId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fromApi))
            {
                fromApi = AppConfig.Sanitize(fromApi, 64, "a game");
                lock (_gate) { _cache[clientId] = fromApi!; }
                SaveDisk();
                return fromApi!;
            }
        }

        var hint = CleanHint(procHint);
        if (hint != null)
            return AppConfig.Sanitize(hint, 64, "a game");

        return string.IsNullOrWhiteSpace(_cfg.ShimFallbackGame) ? "a game" : _cfg.ShimFallbackGame.Trim();
    }

    private async Task<string?> QueryDiscordAsync(string clientId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v9/applications/{clientId}/rpc");
            req.Headers.TryAddWithoutValidation("User-Agent", "PlaycastCompanion/1.0");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null; // 404 for non-app ids, 429 rate-limit — just fall through
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("name", out var name))
            {
                var s = name.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s!.Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Info($"game name lookup failed for {clientId}: {ex.Message}");
        }
        return null;
    }

    private static string? CleanHint(string? procHint)
    {
        var hint = procHint?.Trim();
        if (string.IsNullOrEmpty(hint))
            return null;
        var lower = hint.ToLowerInvariant();
        return GenericHints.Any(g => lower.Contains(g)) ? null : hint;
    }

    private void LoadDisk()
    {
        try
        {
            if (!File.Exists(CachePath))
                return;
            var disk = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            if (disk == null)
                return;
            lock (_gate)
            {
                foreach (var kv in disk)
                    _cache[kv.Key] = kv.Value; // disk cache wins over the seed
            }
        }
        catch (Exception ex)
        {
            Log.Info($"resolved-names cache load failed: {ex.Message}");
        }
    }

    private void SaveDisk()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            Dictionary<string, string> snapshot;
            lock (_gate) { snapshot = new Dictionary<string, string>(_cache); }
            File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Info($"resolved-names cache save failed: {ex.Message}");
        }
    }
}

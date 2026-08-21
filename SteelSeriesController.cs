using System.Text;
using System.Text.Json;

namespace PlaycastCompanion;

/// <summary>
/// SteelSeries blackout via GameSense — the local REST API exposed by
/// SteelSeries GG / Engine (address discovered from coreProps.json in
/// ProgramData). We register a "game", bind one color-handler event per
/// device type (independently, so an unsupported zone on one device type
/// can't take the others down), then fire the events and heartbeat.
/// remove_game on release hands lighting back to the user's SteelSeries
/// config.
/// </summary>
public sealed class SteelSeriesController : LightingBackendBase
{
    private const string Game = "PLAYCAST_COMPANION";
    private static readonly string[] DeviceTypes = ["keyboard", "mouse", "headset", "mousepad", "indicator"];

    private readonly SteelSeriesConfig _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string? _baseUrl;
    private List<string> _events = [];

    public SteelSeriesController(SteelSeriesConfig cfg) => _cfg = cfg;

    public override string DisplayName => "SteelSeries GameSense";
    public override bool Enabled => _cfg.Enabled;
    protected override string UnavailableText(Exception ex) => "SteelSeries GG not reachable — retrying";
    protected override void OnConnectionLost() => _baseUrl = null;

    protected override async Task ApplyTickAsync(CancellationToken ct)
    {
        if (_baseUrl == null)
            await RegisterAsync(ct).ConfigureAwait(false);

        foreach (var evt in _events)
        {
            await PostAsync(_baseUrl!, "/game_event", new Dictionary<string, object?>
            {
                ["game"] = Game,
                ["event"] = evt,
                ["data"] = new Dictionary<string, object?> { ["value"] = 1 },
            }, ct).ConfigureAwait(false);
        }
        await PostAsync(_baseUrl!, "/game_heartbeat",
            new Dictionary<string, object?> { ["game"] = Game }, ct).ConfigureAwait(false);
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        var props = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries", "SteelSeries Engine 3", "coreProps.json");
        if (!File.Exists(props))
            throw new FileNotFoundException("coreProps.json not found (is SteelSeries GG installed?)");
        using var doc = JsonDocument.Parse(File.ReadAllText(props));
        var address = doc.RootElement.GetProperty("address").GetString()
            ?? throw new InvalidOperationException("no address in coreProps.json");
        var url = $"http://{address}";

        await PostAsync(url, "/game_metadata", new Dictionary<string, object?>
        {
            ["game"] = Game,
            ["game_display_name"] = "Playcast Companion",
            ["developer"] = "personal",
        }, ct).ConfigureAwait(false);

        var bound = new List<string>();
        foreach (var deviceType in DeviceTypes)
        {
            var evt = $"BLACKOUT_{deviceType.ToUpperInvariant()}";
            try
            {
                await PostAsync(url, "/bind_game_event", new Dictionary<string, object?>
                {
                    ["game"] = Game,
                    ["event"] = evt,
                    ["min_value"] = 0,
                    ["max_value"] = 1,
                    ["handlers"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["device-type"] = deviceType,
                            ["zone"] = "all",
                            ["mode"] = "color",
                            ["color"] = new Dictionary<string, object?> { ["red"] = 0, ["green"] = 0, ["blue"] = 0 },
                        },
                    },
                }, ct).ConfigureAwait(false);
                bound.Add(evt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // a device type GG doesn't like shouldn't sink the rest
                Log.Info($"GameSense: binding {deviceType} failed ({ex.Message})");
            }
        }
        if (bound.Count == 0)
            throw new InvalidOperationException("GameSense rejected every handler binding");
        _events = bound;
        _baseUrl = url;
        Log.Info($"GameSense registered ({bound.Count} device types) at {url}");
    }

    protected override async Task ReleaseAsync()
    {
        var url = _baseUrl;
        _baseUrl = null;
        if (url == null)
            return;
        using var timeout = new CancellationTokenSource(3000);
        await PostAsync(url, "/remove_game",
            new Dictionary<string, object?> { ["game"] = Game }, timeout.Token).ConfigureAwait(false);
        Log.Info("GameSense game removed; SteelSeries lighting restored");
    }

    private async Task PostAsync(string baseUrl, string path, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var resp = await _http.PostAsync(baseUrl + path,
            new StringContent(json, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 160)
                body = body[..160];
            throw new InvalidOperationException($"{path} -> HTTP {(int)resp.StatusCode} {body}");
        }
    }
}

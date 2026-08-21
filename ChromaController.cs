using System.Text;
using System.Text.Json;

namespace PlaycastCompanion;

/// <summary>
/// Razer Chroma blackout via the local REST API. Holds a Chroma session that
/// forces every device type to static black; heartbeats keep the session
/// alive (the SDK drops it after ~15 s of silence) and the periodic re-apply
/// beats Synapse or other SDK apps reasserting their effects. Releasing
/// DELETEs the session so Synapse's normal profile returns.
/// </summary>
public sealed class ChromaController : LightingBackendBase
{
    private static readonly string[] Devices =
        ["keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink"];

    private const string BlackEffectJson = """{"effect":"CHROMA_STATIC","param":{"color":0}}""";

    // Init can legitimately take several seconds while the SDK enumerates devices.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly AppConfig _cfg;
    private string? _sessionUri;

    public ChromaController(AppConfig cfg) => _cfg = cfg;

    public override string DisplayName => "Razer Chroma";
    public override bool Enabled => _cfg.RazerEnabled;
    protected override TimeSpan Tick => TimeSpan.FromSeconds(Math.Clamp(_cfg.TickSeconds, 1, 10));
    protected override TimeSpan RetryDelay => TimeSpan.FromSeconds(Math.Max(5, _cfg.RetryInitSeconds));
    protected override string UnavailableText(Exception ex) => "Unreachable — retrying (is Razer Synapse running?)";
    protected override void OnConnectionLost() => _sessionUri = null;

    protected override async Task ApplyTickAsync(CancellationToken ct)
    {
        if (_sessionUri == null)
        {
            await InitSessionAsync(ct).ConfigureAwait(false);
            await Task.Delay(750, ct).ConfigureAwait(false); // let the SDK finish registering the app
        }
        else
        {
            await PutAsync($"{_sessionUri}/heartbeat", "", ct).ConfigureAwait(false);
        }
        foreach (var device in Devices)
        {
            try
            {
                await PutAsync($"{_sessionUri}/{device}", BlackEffectJson, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // device types the user doesn't own can fail (and a per-device
                // timeout is retried next tick anyway); that's fine
            }
        }
    }

    private async Task InitSessionAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            title = "Playcast Companion",
            description = "Turns all Razer lighting off while a guest session is active",
            author = new { name = "personal", contact = "local" },
            device_supported = Devices,
            category = "application",
        });
        using var resp = await _http.PostAsync(_cfg.ChromaInitUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        _sessionUri = doc.RootElement.GetProperty("uri").GetString()
            ?? throw new InvalidOperationException("Chroma init response had no uri");
        Log.Info($"Chroma session opened: {_sessionUri}");
    }

    private async Task PutAsync(string url, string json, CancellationToken ct)
    {
        using var resp = await _http.PutAsync(url,
            new StringContent(json, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    protected override async Task ReleaseAsync()
    {
        var uri = _sessionUri;
        _sessionUri = null;
        if (uri == null)
            return;
        try
        {
            using var resp = await _http.DeleteAsync(uri).ConfigureAwait(false);
            Log.Info($"Chroma session released (HTTP {(int)resp.StatusCode}); Synapse lighting restored");
        }
        catch (Exception ex)
        {
            Log.Info($"Chroma release failed ({ex.Message}); the session will time out on its own in ~15 s");
        }
    }
}

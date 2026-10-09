using System.Text.Json;

namespace PlaycastCompanion;

public sealed class AppConfig
{
    /// <summary>Windows account whose logon turns all Razer lighting off.</summary>
    public string TargetUsername { get; set; } = "NonsoleMode";

    /// <summary>Seconds between heartbeat + re-apply ticks while blackout is held. Must stay below the Chroma SDK's ~15 s session timeout.</summary>
    public int TickSeconds { get; set; } = 5;

    /// <summary>Seconds to wait before retrying when the Chroma REST API is unreachable (Synapse not running, etc.).</summary>
    public int RetryInitSeconds { get; set; } = 30;

    /// <summary>Whether a Disconnected session of the target user still counts as "guest active". Active/Connected always count.</summary>
    public bool IncludeDisconnectedSessions { get; set; } = true;

    /// <summary>Chroma SDK REST initialization endpoint.</summary>
    public string ChromaInitUrl { get; set; } = "http://localhost:54235/razer/chromasdk";

    /// <summary>Razer Chroma participates in the guest blackout.</summary>
    public bool RazerEnabled { get; set; } = true;

    public SteelSeriesConfig SteelSeries { get; set; } = new();
    public LogitechConfig Logitech { get; set; } = new();
    public CorsairConfig Corsair { get; set; } = new();
    public OpenRgbConfig OpenRgb { get; set; } = new();
    public DynamicLightingConfig DynamicLighting { get; set; } = new();

    public RegistryWatchConfig RegistryWatch { get; set; } = new();

    public DiscordConfig Discord { get; set; } = new();

    public SessionLoggingConfig SessionLogging { get; set; } = new();

    public static AppConfig Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"config.json load failed, using defaults: {ex.Message}");
        }
        return new AppConfig();
    }

    /// <summary>Clamp every user-editable value into a safe range so a hand-edited
    /// config can never drive the app into nonsense (or point its REST calls off-box).</summary>
    public void Normalize()
    {
        SessionLogging ??= new();
        SessionLogging.Normalize();
        TargetUsername = Sanitize(TargetUsername, 104, "NonsoleMode");
        TickSeconds = Math.Clamp(TickSeconds, 1, 10);
        RetryInitSeconds = Math.Clamp(RetryInitSeconds, 5, 600);
        if (!Uri.TryCreate(ChromaInitUrl, UriKind.Absolute, out var chroma) || !chroma.IsLoopback)
            ChromaInitUrl = "http://localhost:54235/razer/chromasdk";
        OpenRgb.Host = Sanitize(OpenRgb.Host, 253, "127.0.0.1");
        OpenRgb.Port = Math.Clamp(OpenRgb.Port, 1, 65535);
        OpenRgb.BlackoutProfile = Sanitize(OpenRgb.BlackoutProfile, 64, "Blackout");
        Discord.ApplicationId = Discord.ApplicationId?.Trim() ?? "";
        Discord.Details = Sanitize(Discord.Details, 128, "");
        Discord.State = Sanitize(Discord.State, 128, "");
        Discord.LargeImageKey = Sanitize(Discord.LargeImageKey, 64, "");
        Discord.LargeImageText = Sanitize(Discord.LargeImageText, 128, "");
        Discord.HostingTemplate = Sanitize(Discord.HostingTemplate, 128, "Hosting {game} in Nonsole Mode");
        Discord.ShimFallbackGame = Sanitize(Discord.ShimFallbackGame, 64, "a game");
        Discord.ShimPipeName = Sanitize(Discord.ShimPipeName, 64, "playcast-companion-shim");
    }

    /// <summary>Strip control characters, trim, and cap length; empty → fallback.</summary>
    public static string Sanitize(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return fallback;
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}

public sealed class SteelSeriesConfig
{
    public bool Enabled { get; set; } = false;
}

public sealed class LogitechConfig
{
    public bool Enabled { get; set; } = false;
}

public sealed class CorsairConfig
{
    public bool Enabled { get; set; } = false;
}

public sealed class OpenRgbConfig
{
    public bool Enabled { get; set; } = false;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 6742;

    /// <summary>Name of the all-black profile created once in OpenRGB.</summary>
    public string BlackoutProfile { get; set; } = "Blackout";
}

public sealed class DynamicLightingConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true (default), Windows Dynamic Lighting skips LampArray devices
    /// whose vendor already has a dedicated engine enabled — so WDL and (e.g.)
    /// the Razer engine don't fight over the same physical device. WDL then
    /// covers only devices no other enabled engine owns.
    /// </summary>
    public bool ExcludeVendorOwnedDevices { get; set; } = true;
}

/// <summary>
/// Discord Rich Presence shown on the user's profile while the guest session
/// is active. The ApplicationId is deliberately config-file-only — it is set
/// once and not exposed in the settings window.
/// </summary>
public sealed class DiscordConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Application ID from discord.com/developers/applications. Empty = integration off. Config-file only.</summary>
    public string ApplicationId { get; set; } = "";

    /// <summary>First text line under the app name.</summary>
    public string Details { get; set; } = "In Nonsole Mode: Accruing Rewards by Hosting";

    /// <summary>Optional second text line.</summary>
    public string State { get; set; } = "";

    /// <summary>Art-asset key of the uploaded image (the Playcast squid).</summary>
    public string LargeImageKey { get; set; } = "playcast_squid";

    /// <summary>Tooltip shown when hovering the image.</summary>
    public string LargeImageText { get; set; } = "Playcast";

    /// <summary>
    /// Master switch for game pass-through: a shim in the guest session captures
    /// the Application ID of Discord-integrated games the guest launches, and the
    /// owner instance folds the resolved game name into the presence. Default OFF;
    /// inherently partial (games whose Discord client gives up on a denied
    /// discord-ipc-0 are never seen — see README).
    /// </summary>
    public bool GamePassthroughEnabled { get; set; } = false;

    /// <summary>Presence line while a guest game is detected. Literal {game} is replaced with the resolved name.</summary>
    public string HostingTemplate { get; set; } = "Hosting {game} in Nonsole Mode";

    /// <summary>Fill used when neither the Discord app registry nor the exe name resolves a title.</summary>
    public string ShimFallbackGame { get; set; } = "a game";

    /// <summary>Cross-session guest→owner relay pipe name (distinct from discord-ipc-N).</summary>
    public string ShimPipeName { get; set; } = "playcast-companion-shim";

    /// <summary>Optional read-only seed of Application ID → display name (admin-provisioned; live cache lives in %LOCALAPPDATA%).</summary>
    public Dictionary<string, string> ResolvedNames { get; set; } = new();
}

/// <summary>
/// Optional secondary signal: a registry key/value that indicates guest mode.
/// ORed with the session-based detection.
/// </summary>
public sealed class RegistryWatchConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>"HKLM" or "HKCU".</summary>
    public string Hive { get; set; } = "HKLM";

    public string SubKey { get; set; } = @"SOFTWARE\Playcast\GuestMode";

    /// <summary>Value to inspect. Null/empty means "the subkey existing at all counts as active".
    /// Default VirtualDisplayDeviceName: a value Playcast is observed to write under GuestMode only
    /// while the guest virtual display is up, so its presence tracks the active guest session.</summary>
    public string? ValueName { get; set; } = "VirtualDisplayDeviceName";

    /// <summary>Expected data (string compare) for "active". Null/empty means "any non-empty, non-zero data counts".</summary>
    public string? ActiveValue { get; set; }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaycastCompanion;

/// <summary>Adds the Companion backends' public status to passive machine snapshots.</summary>
public sealed class CompanionSnapshotSource : ISessionSnapshotSource, ITerminalSnapshotFallbackSource
{
    private readonly ISessionSnapshotSource _machine;
    private readonly IReadOnlyList<ILightingBackend> _lighting;
    private readonly DiscordPresence? _discord;
    private readonly AppConfig _configuration;

    public CompanionSnapshotSource(ISessionSnapshotSource machine,
        IReadOnlyList<ILightingBackend> lighting, DiscordPresence? discord, AppConfig configuration)
    {
        _machine = machine;
        _lighting = lighting;
        _discord = discord;
        _configuration = configuration;
    }

    public async ValueTask<object> CaptureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _machine.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return AddBackendStatus(snapshot);
    }

    public object CaptureTerminalFallback(string error) =>
        AddBackendStatus(MachineSnapshotSource.CaptureTerminalFallback(error));

    private object AddBackendStatus(object snapshot)
    {
        var root = JsonSerializer.SerializeToNode(snapshot) as JsonObject ?? new JsonObject();
        root["companionBackends"] = JsonSerializer.SerializeToNode(new
        {
            version = typeof(CompanionSnapshotSource).Assembly.GetName().Version?.ToString(),
            targetUsername = _configuration.TargetUsername,
            includeDisconnectedSessions = _configuration.IncludeDisconnectedSessions,
            lighting = _lighting.Select(backend => new
            {
                name = backend.DisplayName, enabled = backend.Enabled,
                holding = backend.IsHolding, status = backend.StatusText,
            }).ToArray(),
            discord = _discord?.StatusText ?? "Unavailable in this Windows session",
        });
        return root;
    }
}

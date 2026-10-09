using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlaycastCompanion;

/// <summary>Coordinates automatic guest-session logging, authenticated diagnostics, and periodic inventory.</summary>
public sealed class SessionLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly AppConfig _config;
    private readonly ISessionSnapshotSource _snapshots;
    private readonly string? _baseDirectory;
    private readonly DiagnosticEventClassifier _classifier = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _snapshotRequested = new(0, 1);
    private readonly SessionDiagnosticsListener? _listener;
    private readonly Task _snapshotLoop;
    private SessionLogWriter? _writer;
    private string _coverage;
    private string _observerStatus = "";
    private string _directory;
    private string? _lastError;
    private bool _active;
    private bool _stopped;
    private string? _sessionIdentity;
    private DateTime _lastSnapshotRequestUtc;

    public SessionLogger(AppConfig config, ISessionSnapshotSource snapshotSource, bool isHeadless,
        string? baseDirectory = null)
    {
        _config = config;
        _snapshots = snapshotSource;
        _baseDirectory = baseDirectory;
        _directory = baseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaycastCompanion", "sessions");
        _coverage = isHeadless
            ? "Headless guest instance: realtime feed is collected by the owner Companion instance."
            : "Waiting for Playcast diagnostic feed; realtime coverage unavailable until connected.";
        if (!isHeadless)
        {
            _listener = new SessionDiagnosticsListener(config.TargetUsername, HandleDiagnostic, UpdateCoverage);
            _listener.Start();
        }
        _snapshotLoop = Task.Run(() => SnapshotLoopAsync(_cancellation.Token));
    }

    public string LogDirectory { get { lock (_gate) return _directory; } }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                if (!_config.SessionLogging.Enabled) return "Session logging disabled.";
                var state = _active ? "Session logging active." : "Waiting for an active Nonsole Mode session.";
                var error = _writer?.LastError ?? _lastError;
                return state + " " + _coverage + (_observerStatus.Length == 0 ? "" : " " + _observerStatus)
                    + (error == null ? "" : " " + error);
            }
        }
    }

    public bool IncludeSecrets
    {
        get { lock (_gate) return _config.SessionLogging.IncludeSecrets; }
        set
        {
            lock (_gate)
            {
                if (_config.SessionLogging.IncludeSecrets == value) return;
                _config.SessionLogging.IncludeSecrets = value;
                _writer?.Write("session", new { @event = "secretCaptureChanged", includeSecrets = value }, false);
            }
        }
    }

    public void UpdateSession(bool active, string reason, string? sessionIdentity = null)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (!_config.SessionLogging.Enabled)
            {
                if (_writer != null) CloseSession("Session logging disabled");
                _active = false;
                return;
            }
            var identityChanged = active && _active && sessionIdentity != _sessionIdentity;
            if (active == _active && !identityChanged) return;
            if (identityChanged) CloseSession("Guest session identity changed");
            _active = active;
            _sessionIdentity = sessionIdentity;
            if (active)
            {
                _writer = new SessionLogWriter(_config.SessionLogging, _baseDirectory);
                _directory = _writer.DirectoryPath;
                _lastError = null;
                _writer.Write("session", new
                {
                    @event = "sessionStarted", reason, targetUsername = _config.TargetUsername, sessionIdentity,
                    coverage = _coverage, observerStatus = _observerStatus,
                    snapshotIntervalSeconds = _config.SessionLogging.SnapshotIntervalSeconds,
                    maximumSessionBytes = _config.SessionLogging.MaxSessionBytes,
                    maximumFileBytes = _config.SessionLogging.MaxFileBytes,
                    retainedDays = _config.SessionLogging.RetentionDays,
                }, IncludeSecrets);
                RequestSnapshot(force: true);
            }
            else
                CloseSession(reason);
        }
    }

    public void LogAppEvent(string message) =>
        Record("diagnostics", new { @event = "companionLog", message });

    public void Record(string category, object? payload)
    {
        lock (_gate)
        {
            if (!_stopped && _active && _config.SessionLogging.Enabled)
                _writer?.Write(category, payload, _config.SessionLogging.IncludeSecrets);
        }
    }

    private void HandleDiagnostic(JsonObject envelope)
    {
        lock (_gate)
        {
            UpdateObserverStatus(envelope);
            if (_stopped || !_active || !_config.SessionLogging.Enabled || _writer == null) return;
            var category = envelope["category"]?.GetValue<string>() ?? "diagnostics";
            _writer.Write(category == "state" ? "diagnostics" : category, envelope, IncludeSecrets);
            foreach (var related in _classifier.RelatedRecords(envelope))
            {
                _writer.Write(related.Category, related.Record, IncludeSecrets);
                if (related.Category is "games" or "session") RequestSnapshot();
            }
        }
    }

    private void UpdateObserverStatus(JsonObject envelope)
    {
        if (envelope["category"]?.GetValue<string>() != "diagnostics" || envelope["payload"] is not JsonObject payload
            || payload["event"] is not JsonValue eventValue || !eventValue.TryGetValue<string>(out var eventName))
            return;
        if (eventName == "observer_failed")
        {
            _observerStatus = "Host observer failed; realtime capture unavailable.";
            return;
        }
        if (eventName != "observer_started") return;
        var channels = new List<string>();
        foreach (var channel in new[] { ("realtimeWebSocket", "realtime WebSocket"),
            ("realtimeHttp", "realtime HTTP"), ("nativeIpc", "native IPC") })
            if (payload[channel.Item1] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled)
                channels.Add(channel.Item2);
        _observerStatus = channels.Count == 0 ? "Host observers started; capture channels unavailable."
            : "Host observers active: " + string.Join(", ", channels) + ".";
    }

    private void UpdateCoverage(string coverage)
    {
        lock (_gate)
        {
            if (_stopped || _coverage == coverage) return;
            _coverage = coverage;
            if (coverage.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
                || coverage.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase))
                _observerStatus = "";
            if (_config.SessionLogging.Enabled)
                _writer?.Write("diagnostics", new { @event = "diagnosticCoverageChanged", coverage }, IncludeSecrets);
        }
    }

    private void RequestSnapshot(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastSnapshotRequestUtc).TotalSeconds < 5) return;
        _lastSnapshotRequestUtc = now;
        if (_snapshotRequested.CurrentCount == 0)
        {
            try { _snapshotRequested.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task SnapshotLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _snapshotRequested.WaitAsync(TimeSpan.FromSeconds(
                    _config.SessionLogging.SnapshotIntervalSeconds), cancellationToken).ConfigureAwait(false);
                SessionLogWriter? writer;
                lock (_gate) writer = _active && _config.SessionLogging.Enabled ? _writer : null;
                if (writer == null) continue;
                var snapshot = await _snapshots.CaptureAsync(cancellationToken).ConfigureAwait(false);
                WriteSnapshot(writer, snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Record("diagnostics", new { @event = "snapshotFailed", error = ex.Message });
                lock (_gate) _lastError = "Machine snapshot failed: " + ex.Message;
            }
        }
    }

    private void WriteSnapshot(SessionLogWriter writer, object snapshot)
    {
        lock (_gate)
        {
            if (_stopped || !_active || !_config.SessionLogging.Enabled || !ReferenceEquals(writer, _writer)) return;
            writer.Write("diagnostics", new { @event = "machineSnapshot", snapshot }, IncludeSecrets);
            var json = snapshot is JsonObject obj ? obj : JsonSerializer.SerializeToNode(snapshot) as JsonObject;
            if (json == null) return;
            writer.Write("games", new JsonObject
            {
                ["event"] = "gameSnapshot",
                ["installedGames"] = json["installedGames"]?.DeepClone(),
                ["runningGames"] = json["runningGames"]?.DeepClone(),
                ["installedGameSources"] = json["installedGameSources"]?.DeepClone(),
                ["inventoryLimitations"] = json["inventoryLimitations"]?.DeepClone(),
                ["runningGameDetectionStatus"] = json["runningGameDetectionStatus"]?.DeepClone(),
                ["processCoverage"] = json["processCoverage"]?.DeepClone(),
                ["targetSessionIds"] = json["targetSessionIds"]?.DeepClone(),
            }, IncludeSecrets);
        }
    }

    private void CloseSession(string reason)
    {
        _writer?.Write("session", new { @event = "sessionEnded", reason, bytesWritten = _writer.BytesWritten }, IncludeSecrets);
        _lastError = _writer?.LastError;
        _writer?.Dispose();
        _writer = null;
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _active = false;
            CloseSession("Companion shutdown");
        }
        await _cancellation.CancelAsync().ConfigureAwait(false);
        if (_listener != null) await _listener.StopAsync().ConfigureAwait(false);
        await _snapshotLoop.ConfigureAwait(false);
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

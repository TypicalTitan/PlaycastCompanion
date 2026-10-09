using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

internal sealed class ScriptGroupBuilder
{
    private readonly string _key;
    private readonly string _execution;
    private readonly List<LogRecord> _records = [];
    private string _name;
    private string _status = "Observed";
    private string? _script;
    private string? _stdout;
    private string? _stderr;
    private string? _error;
    private int? _exitCode;
    private DateTimeOffset? _started;
    private DateTimeOffset? _ended;
    private bool _terminal;
    public string SessionKey { get; }
    public string? DeploymentId { get; }

    public ScriptGroupBuilder(string key, ScriptObservedEvent first, string? execution)
    {
        _key = key;
        _execution = execution ?? LogJson.Text(LogJson.Get(first.Message, "header"), "tag") ?? "Uncorrelated";
        _name = LogJson.Text(LogJson.Find(first.Message, "scriptName"))
            ?? LogJson.Text(LogJson.Find(first.Message, "stepName")) ?? "PowerShell script";
        SessionKey = first.Record.SessionKey;
        DeploymentId = LogJson.Text(LogJson.Find(first.Message, "deploymentId"));
    }

    public void Apply(ScriptObservedEvent item)
    {
        _records.Add(item.Record);
        _name = LogJson.Text(LogJson.Find(item.Message, "scriptName")) ?? _name;
        _script ??= LogJson.Text(LogJson.Find(item.Message, "completeScript"))
            ?? LogJson.Text(LogJson.Find(item.Message, "script"));
        _stdout = LogJson.Text(LogJson.Find(item.Message, "stdout")) ?? _stdout;
        _stderr = LogJson.Text(LogJson.Find(item.Message, "stderr")) ?? _stderr;
        _error = LogJson.Text(LogJson.Find(item.Message, "error")) ?? _error;
        _exitCode = LogJson.Integer(LogJson.Find(item.Message, "exitCode")) ?? _exitCode;
        switch (item.Type)
        {
            case "PROVISIONING_SCRIPT_DEPLOY": if (_started == null && !_terminal) _status = "Received"; break;
            case "PROVISIONING_SCRIPT_STARTED":
                _started ??= SourceTime(item);
                if (!_terminal) _status = "Started";
                break;
            case "PROVISIONING_SCRIPT_RESULT":
                _status = LogJson.Text(LogJson.Find(item.Message, "status")) ?? TerminalStatus(item.Message);
                _terminal = true;
                _ended = SourceTime(item);
                break;
            case "launchElevatedPowerShellScriptReceived": if (!_terminal) _status = "Acknowledged"; break;
            case "launchElevatedPowerShellScript": ApplyNative(item); break;
        }
    }

    public void ApplyRevoke(ScriptObservedEvent item)
    {
        _records.Add(item.Record);
        if (!_terminal) _status = "Cancellation requested";
    }

    private void ApplyNative(ScriptObservedEvent item)
    {
        var isReply = LogJson.Boolean(LogJson.Get(LogJson.Get(item.Message, "header"), "isReply"));
        if (isReply != true) { if (!_terminal) _status = "Received"; return; }
        if (LogJson.Integer(LogJson.Find(item.Message, "exitCode")) != null)
        { _terminal = true; _status = TerminalStatus(item.Message); _ended = SourceTime(item); return; }
        _status = LogJson.Boolean(LogJson.Find(item.Message, "success")) switch
        { true => "Launch accepted", false => "Launch failed", _ => "Reply received" };
    }

    private static string TerminalStatus(JsonElement message)
    {
        var error = LogJson.Text(LogJson.Find(message, "error")) ?? "";
        if (error.Contains("cancel", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "timeout";
        var exit = LogJson.Integer(LogJson.Find(message, "exitCode"));
        var success = LogJson.Boolean(LogJson.Find(message, "success"));
        if (success == false || exit is int code && code != 0) return "failed";
        return exit == 0 || success == true ? "succeeded" : "Result received";
    }

    private static DateTimeOffset? SourceTime(ScriptObservedEvent item) => item.Record.ObservedAtUtc
        ?? LogJson.Time(LogJson.Get(item.Message, "timestamp"))
        ?? LogJson.Time(LogJson.Get(LogJson.Get(item.Message, "header"), "timestamp"));

    public ScriptExecutionGroup Build()
    {
        var ordered = _records.OrderBy(record => record.EventTimeUtc).ThenBy(record => record.Id).ToArray();
        return new(_key, _name, _execution, DeploymentId, SessionKey, _status,
            ordered[0].EventTimeUtc, ordered[^1].EventTimeUtc, _started, _ended, _script,
            _exitCode, _stdout, _stderr, _error, ordered);
    }
}

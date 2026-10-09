using System.Text.Json;
using PlaycastCompanion.LogAnalysis;

namespace PlaycastCompanion.UI;

public sealed partial class LogAnalysisPanel
{
    public void SelectView(string view)
    {
        _view = view;
        foreach (var (name, button) in _viewButtons)
            button.ForeColor = name == view ? Branding.PlaycastGold : Branding.UiTextDim;
        _list.VirtualListSize = 0;
        _rows.Clear();
        _list.Columns.Clear();
        _inspector.Clear();
        _source.Text = "Select a record to inspect its contents and source.";
        _plot.Visible = false;
        if (_projection == null) return;
        if (view == "Timeline")
        {
            _plot.Visible = true;
            _plot.ShowTimeline(_projection.Records);
            ShowRecords(_projection.Records);
        }
        else if (view == "Communication") ShowRecords(_projection.CommunicationRecords);
        else if (view == "Scripts") ShowScripts();
        else if (view == "Games") ShowGames();
        else if (view == "Health") ShowHealth();
        _list.VirtualListSize = _rows.Count;
        SizePlot();
        ResizeColumns();
    }

    private void ShowRecords(IReadOnlyList<LogRecord> records)
    {
        SetColumns("Time (UTC)", "Category", "Record");
        foreach (var record in records)
            _rows.Add(([record.RecordedAtUtc.ToString("HH:mm:ss.fff"), record.Category, record.EventType], record));
        if (records.Count == 0) _source.Text = "No records for this view in the selected files.";
    }

    private void ShowScripts()
    {
        SetColumns("Script / execution", "Status", "Observed runtime");
        foreach (var script in _projection!.Scripts)
            _rows.Add(([$"{script.Name} · {script.ExecutionId}", script.Status,
                script.ObservedRuntime is TimeSpan elapsed ? $"{elapsed.TotalSeconds:N1} s" : "Not observed"], script));
        foreach (var record in _projection.ScriptCancellationEvents)
            _rows.Add(([record.EventType, "Cancellation event", "Not an execution"], record));
        _source.Text = $"{_projection.Scripts.Count} script groups; matching copies counted once. Native phases without a shared ID remain separate.";
    }

    private void ShowGames()
    {
        SetColumns("Game", "Store", "Observed state");
        var games = _projection!.LatestGames;
        if (games == null) { _source.Text = "No passive inventory snapshot. Import games.jsonl or machine snapshots from diagnostics.jsonl."; return; }
        foreach (var game in games.InstalledGames)
            _rows.Add(([game.Name, game.Store, game.IsRunning ? "Running" : "No process match"], game));
        foreach (var game in games.RunningGames.Where(running => !games.InstalledGames.Any(installed => installed.Store == running.Store && installed.Id == running.Id)))
            _rows.Add(([game.Name, game.Store, $"Running · PID {game.ProcessId}"], game));
        _source.Text = $"{games.RecordedAtUtc:yyyy-MM-dd HH:mm:ss} UTC · {games.InstalledGames.Count} installed · {games.RunningGameCount} matched running games ({games.RunningProcessCount} processes). Detection: {games.DetectionStatus}";
        _inspector.Text = string.Join(Environment.NewLine, games.CoverageNotes);
    }

    private void ShowHealth()
    {
        _plot.Visible = true;
        _plot.ShowHealth(_projection!.HealthSamples);
        ShowRecords(_projection.CaptureFailures);
        _source.Text = "CPU time is cumulative per process. Unavailable measurements are omitted. Select a failure or plotted sample for details.";
    }

    private void SetColumns(params string[] names)
    {
        foreach (var name in names) _list.Columns.Add(name);
        _list.SizeChanged -= OnListSizeChanged;
        _list.SizeChanged += OnListSizeChanged;
    }

    private void OnListSizeChanged(object? sender, EventArgs args) => ResizeColumns();

    private void SizePlot() => _plot.Height = _view == "Health" ? Math.Min(220, Math.Max(170, _recordsHost.Height - 65))
        : Math.Min(155, Math.Max(90, _recordsHost.Height - 80));

    private void ResizeColumns()
    {
        if (_list.Columns.Count == 0) return;
        var width = Math.Max(180, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        var weights = _view is "Scripts" or "Games" ? new[] { .52, .22, .26 } : new[] { .23, .19, .58 };
        for (var index = 0; index < _list.Columns.Count; index++) _list.Columns[index].Width = (int)(width * weights[index]);
    }

    private void Inspect(object value)
    {
        object contents = value;
        if (value is LogRecord record)
        {
            _source.Text = $"{record.SourceFile}:{record.LineNumber} · {record.RecordedAtUtc:O} · {(record.IncludeSecrets ? "Secrets capture enabled" : "Redacted capture policy")}";
            contents = record.Raw;
        }
        else if (value is ScriptExecutionGroup script)
        {
            _source.Text = $"{script.ExecutionId} · {script.Status} · {script.Records.Count} linked phases";
            contents = new { script.ScriptText, script.ExitCode, script.Stdout, script.Stderr, script.Error,
                Sources = script.Records.Select(record => $"{record.SourceFile}:{record.LineNumber}") };
        }
        else if (_view == "Games" && _projection?.LatestGames is { } snapshot)
        {
            _source.Text = $"{snapshot.SourceRecord.SourceFile}:{snapshot.SourceRecord.LineNumber} · {snapshot.DetectionStatus}";
            contents = new { SelectedGame = value, snapshot.CoverageNotes, Snapshot = snapshot.SourceRecord.Raw };
        }
        var text = JsonSerializer.Serialize(contents, new JsonSerializerOptions { WriteIndented = true });
        _inspector.Text = text.Length > 100000 ? text[..100000] + "\r\n[Preview limited to 100,000 characters]" : text;
    }
}

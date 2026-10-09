using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PlaycastCompanion;

public sealed partial class MainForm
{
    private readonly CheckBox _chkSessionLogs = new()
    {
        AutoSize = true,
        Text = "Automatically log active guest sessions",
        ForeColor = Branding.UiText,
    };
    private readonly CheckBox _chkSessionSecrets = new()
    {
        AutoSize = true,
        Text = "Debug: include credentials and tokens in new log entries",
        ForeColor = Branding.UiText,
        Margin = new Padding(3, 14, 3, 3),
    };
    private readonly NumericUpDown _numSnapshotSeconds = new()
    {
        Minimum = 5, Maximum = 600, Width = 70,
        BackColor = Branding.UiSurface, ForeColor = Branding.UiText,
    };
    private readonly Label _sessionLogStatus = SessionLogBlurb("");
    private readonly Label _sessionLogPath = SessionLogBlurb("");

    private Panel BuildSessionLogTab()
    {
        var page = MakePage("Session logs");
        page.AutoScroll = true;
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, Margin = new Padding(0),
        };
        _chkSessionLogs.Checked = _ctx.Config.SessionLogging.Enabled;
        _chkSessionSecrets.Checked = _ctx.SessionLogging.IncludeSecrets;
        _numSnapshotSeconds.Value = Math.Clamp(_ctx.Config.SessionLogging.SnapshotIntervalSeconds, 5, 600);
        layout.Controls.Add(_chkSessionLogs);
        layout.Controls.Add(SessionLogBlurb("Save changes to apply automatic logging and the snapshot interval."));
        layout.Controls.Add(SessionLogBlurb("Each session records realtime messages, PowerShell scripts and results, installed and running games, and app diagnostics."));
        var interval = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        interval.Controls.Add(MakeCaption("Game and process snapshots every (seconds)"));
        interval.Controls.Add(_numSnapshotSeconds);
        layout.Controls.Add(interval);
        layout.Controls.Add(_chkSessionSecrets);
        layout.Controls.Add(SessionLogBlurb("Secrets are redacted by default. The debug toggle applies immediately; existing entries keep their previous contents. Raw logs can contain passwords and authentication tokens."));
        _chkSessionSecrets.CheckedChanged += (_, _) =>
            _ctx.SessionLogging.IncludeSecrets = _chkSessionSecrets.Checked;
        layout.Controls.Add(_sessionLogStatus);
        layout.Controls.Add(_sessionLogPath);
        var open = MakeButton("Open session log folder");
        open.Margin = new Padding(3, 12, 3, 3);
        open.Click += (_, _) => OpenSessionLogs();
        layout.Controls.Add(open);
        page.Controls.Add(layout);
        RefreshSessionLogStatus();
        return page;
    }

    private void RefreshSessionLogStatus()
    {
        _sessionLogStatus.Text = _ctx.SessionLogging.StatusText;
        _sessionLogPath.Text = _ctx.SessionLogging.LogDirectory;
    }

    private static Label SessionLogBlurb(string text)
    {
        var label = MakeBlurb(text);
        label.MaximumSize = new Size(500, 0);
        return label;
    }

    private void OpenSessionLogs()
    {
        try
        {
            var directory = _ctx.SessionLogging.LogDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Info($"open session logs failed: {exception.Message}");
        }
    }
}

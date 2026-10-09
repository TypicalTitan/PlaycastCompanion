using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PlaycastCompanion;

public sealed partial class MainForm
{
    private void SaveSettings()
    {
        var user = _txtUser.Text.Trim();
        if (user.Length == 0)
        {
            MessageBox.Show(this, "The guest account name can't be empty.", "Playcast Companion",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(this,
                $"\"{user}\" is the account you're signed in as right now — your lights would stay off whenever you're logged in.\n\nSave anyway?",
                "Playcast Companion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var cfg = _ctx.Config;
        cfg.TargetUsername = user;
        cfg.TickSeconds = (int)_numTick.Value;
        cfg.IncludeDisconnectedSessions = _chkDisconnected.Checked;
        cfg.RegistryWatch.Enabled = _chkRegistry.Checked;
        cfg.SessionLogging.Enabled = _chkSessionLogs.Checked;
        cfg.SessionLogging.SnapshotIntervalSeconds = (int)_numSnapshotSeconds.Value;
        cfg.RazerEnabled = _chkRazer.Checked;
        cfg.SteelSeries.Enabled = _chkSteel.Checked;
        cfg.Logitech.Enabled = _chkLogi.Checked;
        cfg.Corsair.Enabled = _chkCorsair.Checked;
        cfg.OpenRgb.Enabled = _chkOpenRgb.Checked;
        cfg.OpenRgb.BlackoutProfile =
            _txtOpenRgbProfile.Text.Trim().Length > 0 ? _txtOpenRgbProfile.Text.Trim() : "Blackout";
        cfg.DynamicLighting.Enabled = _chkDynamic.Checked;
        cfg.Discord.Enabled = _chkDiscord.Checked;
        cfg.Discord.Details = _txtLine1.Text.Trim();
        cfg.Discord.State = _txtLine2.Text.Trim();
        cfg.Discord.GamePassthroughEnabled = _chkPassthrough.Checked;
        cfg.Discord.HostingTemplate =
            _txtHostTemplate.Text.Trim().Length > 0 ? _txtHostTemplate.Text.Trim() : "Hosting {game} in Nonsole Mode";
        // Discord.ApplicationId is intentionally untouched: config-file only.

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        try
        {
            try
            {
                File.WriteAllText(path, json);
            }
            catch (UnauthorizedAccessException)
            {
                // Installed under Program Files: the config is deliberately not
                // writable by standard users, so saving takes one UAC prompt.
                SaveElevated(path, json);
            }
        }
        catch (Exception ex)
        {
            _lblFeedback.Text = "Couldn't save: " + ex.Message;
            Log.Info($"settings save failed: {ex.Message}");
            return;
        }

        Log.Info("settings saved from UI");
        MarkSettingsSaved();
        _lblFeedback.Text = "Saved! Lighting toggles apply right away.";
        if (MessageBox.Show(this,
                "Saved! Lighting and Discord toggles apply right away.\n\nRestart Playcast Companion too, so account/registry changes take effect?",
                "Playcast Companion", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _ctx.RestartApp();
    }

    private static void SaveElevated(string path, string json)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "playcast-companion-config.json");
        File.WriteAllText(tmp, json);
        var psi = new ProcessStartInfo("cmd.exe", $"/c copy /y \"{tmp}\" \"{path}\"")
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var p = Process.Start(psi) ?? throw new IOException("could not start elevated copy");
        p.WaitForExit();
        try { File.Delete(tmp); } catch { }
        if (p.ExitCode != 0)
            throw new IOException($"elevated copy exited with code {p.ExitCode}");
    }

    /// <summary>Dev harness (--snapshot): renders every settings tab to PNGs
    /// so the UI can be reviewed without driving the desktop.</summary>
    public void SaveSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        var index = 0;
        foreach (var (title, page) in _pages)
        {
            SelectPage(title);
            Application.DoEvents();
            RefreshStatus();
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"page-{index++}-{title.ToLowerInvariant().Replace(' ', '-')}.png"), ImageFormat.Png);
            if (title == "Analyze logs" && _analysisPanel != null) SaveAnalysisSnapshots(dir);

            // scrollable pages get a second, bottom-scrolled capture
            var scroller = page.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(f => f.AutoScroll);
            if (scroller != null)
            {
                scroller.AutoScrollPosition = new Point(0, scroller.DisplayRectangle.Height);
                Application.DoEvents();
                using var bmp2 = new Bitmap(Width, Height);
                DrawToBitmap(bmp2, new Rectangle(0, 0, Width, Height));
                bmp2.Save(Path.Combine(dir, $"page-{title.ToLowerInvariant().Replace(' ', '-')}-scrolled.png"), ImageFormat.Png);
                scroller.AutoScrollPosition = new Point(0, 0);
            }
        }
    }

    public void LoadCaptureForSnapshot(string path) => _analysisPanel?.LoadCaptureForSnapshot(path);

    private void SaveAnalysisSnapshots(string directory)
    {
        foreach (var view in new[] { "Timeline", "Communication", "Scripts", "Games", "Health" })
        {
            _analysisPanel!.SelectView(view);
            Application.DoEvents();
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(Path.Combine(directory, $"analysis-{view.ToLowerInvariant()}.png"), ImageFormat.Png);
        }
    }

}

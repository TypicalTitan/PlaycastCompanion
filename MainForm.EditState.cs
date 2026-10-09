using PlaycastCompanion.UI;

namespace PlaycastCompanion;

public sealed partial class MainForm
{
    private bool _loadingSettings;
    private bool _settingsDirty;

    private void LoadSettings()
    {
        _loadingSettings = true;
        var settings = _ctx.Config;
        _txtUser.Text = settings.TargetUsername;
        _numTick.Value = Math.Clamp(settings.TickSeconds, 1, 10);
        _chkDisconnected.Checked = settings.IncludeDisconnectedSessions;
        _chkRegistry.Checked = settings.RegistryWatch.Enabled;
        _chkRazer.Checked = settings.RazerEnabled;
        _chkSteel.Checked = settings.SteelSeries.Enabled;
        _chkLogi.Checked = settings.Logitech.Enabled;
        _chkCorsair.Checked = settings.Corsair.Enabled;
        _chkOpenRgb.Checked = settings.OpenRgb.Enabled;
        _chkDynamic.Checked = settings.DynamicLighting.Enabled;
        _txtOpenRgbProfile.Text = settings.OpenRgb.BlackoutProfile;
        _chkDiscord.Checked = settings.Discord.Enabled;
        _txtLine1.Text = settings.Discord.Details;
        _txtLine2.Text = settings.Discord.State;
        _chkPassthrough.Checked = settings.Discord.GamePassthroughEnabled;
        _txtHostTemplate.Text = settings.Discord.HostingTemplate;
        _chkSessionLogs.Checked = settings.SessionLogging.Enabled;
        _numSnapshotSeconds.Value = Math.Clamp(settings.SessionLogging.SnapshotIntervalSeconds, 5, 600);
        _chkSessionSecrets.Checked = _ctx.SessionLogging.IncludeSecrets;
        _loadingSettings = false;
        MarkSettingsSaved();
    }

    private void WireSettingEdits(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is LogAnalysisPanel || child == _chkSessionSecrets) continue;
            if (child is CheckBox checkbox) checkbox.CheckedChanged += (_, _) => MarkSettingsEdited();
            else if (child is TextBox textbox) textbox.TextChanged += (_, _) => MarkSettingsEdited();
            else if (child is NumericUpDown numeric) numeric.ValueChanged += (_, _) => MarkSettingsEdited();
            WireSettingEdits(child);
        }
    }

    private void MarkSettingsEdited()
    {
        if (_loadingSettings) return;
        _settingsDirty = true;
        RefreshEditFeedback();
    }

    private void MarkSettingsSaved()
    {
        _settingsDirty = false;
        RefreshEditFeedback();
    }

    private void RefreshEditFeedback()
    {
        _btnSave.Enabled = _settingsDirty;
        _btnDiscard.Visible = _settingsDirty;
        _lblFeedback.Text = _selectedPage == "Analyze logs" ? "Imported analysis · Live settings unchanged"
            : _settingsDirty ? "Unsaved changes" : "All changes saved";
        _lblFeedback.ForeColor = _settingsDirty ? Branding.Amber : Branding.UiTextDim;
    }
}

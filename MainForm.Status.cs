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
    // ----- behavior ----------------------------------------------------------

    private void RefreshStatus()
    {
        RefreshSessionLogStatus();
        bool guest = _ctx.GuestActive;
        _headerSession.Text = guest ? $"Guest session active · {_ctx.Config.TargetUsername}" : "No active guest session";
        _lblSummary.Text = guest
            ? "A guest session is active."
            : "No guest session detected.";
        _lblSummary.ForeColor = guest ? Branding.Amber : Branding.Green;

        SetRow(_dotGuest, _valGuest,
            guest ? Branding.Amber : Branding.Green,
            guest ? $"Signed in ({_ctx.Config.TargetUsername})" : "No guest signed in");

        var enabled = _ctx.Lighting.Where(b => b.Enabled).ToList();
        if (enabled.Count == 0)
        {
            SetRow(_dotLights, _valLights, Branding.Slate, "No lighting systems enabled");
        }
        else if (!guest)
        {
            SetRow(_dotLights, _valLights, Branding.Green,
                $"Normal — vendor software in control ({enabled.Count} system{(enabled.Count == 1 ? "" : "s")} armed)");
        }
        else
        {
            int settled = enabled.Count(b => b.IsHolding && !IsWarnStatus(b.StatusText));
            SetRow(_dotLights, _valLights,
                settled == enabled.Count ? Branding.Slate : Branding.Amber,
                settled == enabled.Count
                    ? $"Off — all {settled} system{(settled == 1 ? "" : "s")} dark"
                    : $"{settled} of {enabled.Count} systems held — see Lighting");
        }

        var discord = _ctx.Discord;
        if (discord == null)
        {
            SetRow(_dotDiscord, _valDiscord, Branding.Slate, "—");
        }
        else
        {
            var color = discord.StatusText.StartsWith("Live", StringComparison.Ordinal) ? Branding.Green
                : discord.StatusText.Contains("retrying") ? Branding.Amber
                : Branding.Slate;
            SetRow(_dotDiscord, _valDiscord, color, discord.StatusText);
        }

        var (autoColor, autoText) = ReadAutostartState();
        SetRow(_dotAutostart, _valAutostart, autoColor, autoText);

        foreach (var (backend, dot, text) in _cards)
        {
            if (!backend.Enabled)
            {
                dot.ForeColor = Branding.Slate;
                text.Text = "Disabled";
            }
            else if (backend.IsHolding)
            {
                dot.ForeColor = IsWarnStatus(backend.StatusText) ? Branding.Amber : Branding.Slate;
                text.Text = backend.StatusText;
            }
            else
            {
                dot.ForeColor = Branding.Green;
                text.Text = string.IsNullOrWhiteSpace(backend.StatusText) ? "Ready" : backend.StatusText;
            }
        }
    }

    private static bool IsWarnStatus(string s) =>
        s.Contains("retry", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Unreachable", StringComparison.OrdinalIgnoreCase)
        || s.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || s.Contains("error", StringComparison.OrdinalIgnoreCase);

    private static (Color, string) ReadAutostartState()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (machine?.GetValue("PlaycastCompanion") is string s && s.Length > 0)
                return (Branding.Green, "On for every account");
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return user?.GetValue("PlaycastCompanion") != null
                ? (Branding.Green, "On for this account only")
                : (Branding.Amber, "Off — run the installer to turn it on");
        }
        catch
        {
            return (Branding.Slate, "Unknown");
        }
    }

}

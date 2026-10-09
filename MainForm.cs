using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PlaycastCompanion;

/// <summary>
/// Settings and diagnostics window with persistent sidebar navigation.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly TrayAppContext _ctx;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private readonly List<(ILightingBackend Backend, Label Dot, Label Text)> _cards = [];

    // Status tab
    private readonly Label _lblSummary = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 11.5f),
        Margin = new Padding(3, 4, 3, 12),
    };
    private readonly Label _dotGuest = MakeDot();
    private readonly Label _dotLights = MakeDot();
    private readonly Label _dotDiscord = MakeDot();
    private readonly Label _dotAutostart = MakeDot();
    private readonly Label _valGuest = MakeValue();
    private readonly Label _valLights = MakeValue();
    private readonly Label _valDiscord = MakeValue();
    private readonly Label _valAutostart = MakeValue();
    private readonly Button _btnPreview = MakeButton("Preview guest mode (10 s)");

    // Lighting tab (per-brand controls)
    private readonly CheckBox _chkRazer = MakeCardCheck();
    private readonly CheckBox _chkSteel = MakeCardCheck();
    private readonly CheckBox _chkLogi = MakeCardCheck();
    private readonly CheckBox _chkCorsair = MakeCardCheck();
    private readonly CheckBox _chkOpenRgb = MakeCardCheck();
    private readonly CheckBox _chkDynamic = MakeCardCheck();
    private readonly NumericUpDown _numTick = new()
    {
        Minimum = 1,
        Maximum = 10,
        Width = 52,
        BackColor = Color.FromArgb(32, 34, 37),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly TextBox _txtOpenRgbProfile = new()
    {
        Width = 110,
        BackColor = Color.FromArgb(32, 34, 37),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
    };

    // Discord tab
    private readonly CheckBox _chkDiscord = new()
    {
        AutoSize = true,
        Text = "Show a Discord status while a guest is hosting",
        Margin = new Padding(3, 3, 3, 8),
        ForeColor = Branding.UiText,
    };
    private readonly TextBox _txtLine1 = MakeDarkTextBox(320);
    private readonly TextBox _txtLine2 = MakeDarkTextBox(320);
    private readonly CheckBox _chkPassthrough = new()
    {
        AutoSize = true,
        Text = "Pass the guest's current game through (“Hosting {game} in Nonsole Mode”)",
        Margin = new Padding(3, 10, 3, 3),
        ForeColor = Branding.UiText,
    };
    private readonly TextBox _txtHostTemplate = MakeDarkTextBox(320);
    private readonly Label _lblAppIdState = new()
    {
        AutoSize = true,
        ForeColor = Branding.UiTextDim,
        Margin = new Padding(3, 10, 3, 3),
    };
    private readonly Label _prevLine1 = new();

    // Advanced tab
    private readonly TextBox _txtUser = MakeDarkTextBox(200);
    private readonly CheckBox _chkDisconnected = new()
    {
        AutoSize = true,
        Text = "Stay stealthy while the guest is disconnected but still signed in",
        Margin = new Padding(3, 10, 3, 3),
        ForeColor = Branding.UiText,
    };
    private readonly CheckBox _chkRegistry = new()
    {
        AutoSize = true,
        Text = "Also watch the Playcast GuestMode registry key (extra signal)",
        Margin = new Padding(3, 4, 3, 3),
        ForeColor = Branding.UiText,
    };

    // bottom bar
    private readonly Button _btnSave = MakeButton("Save changes");
    private readonly Label _lblFeedback = new()
    {
        AutoSize = true,
        ForeColor = Branding.UiTextDim,
        Text = "",
    };

    public MainForm(TrayAppContext ctx)
    {
        _ctx = ctx;
        Text = "Playcast Companion";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Branding.UiBack;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(860, 640);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 720);
        Icon = ctx.AppIcon;

        var shell = BuildNavigationShell();
        Controls.Add(shell);
        Controls.Add(BuildBottomBar());
        Controls.Add(BuildHeader());
        shell.BringToFront();

        _btnPreview.Click += async (_, _) =>
        {
            _btnPreview.Enabled = false;
            var old = _btnPreview.Text;
            _btnPreview.Text = "Previewing…";
            try { await _ctx.TestGuestModeAsync(); }
            finally
            {
                _btnPreview.Text = old;
                _btnPreview.Enabled = true;
            }
        };
        _btnSave.Click += (_, _) => SaveSettings();
        _txtLine1.TextChanged += (_, _) =>
            _prevLine1.Text = _txtLine1.Text.Trim().Length > 0 ? _txtLine1.Text.Trim() : "…";
        _refresh.Tick += (_, _) => RefreshStatus();

        Load += (_, _) =>
        {
            LoadSettings();
            WireSettingEdits(this);
            _lblAppIdState.Text = _ctx.Discord?.IsConfigured == true
                ? "Linked to your Discord app ✓   (the ID lives in config.json)"
                : "Not linked yet — add your Application ID to config.json (see README)";
            RefreshStatus();
            _refresh.Start();
        };
        FormClosed += (_, _) => { _refresh.Stop(); _analysisPanel?.Dispose(); };
    }

    // ----- chrome ------------------------------------------------------------

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Branding.UiSurface };
        var pic = new PictureBox
        {
            Image = _ctx.SquidImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(18, 9, 36, 36),
            BackColor = Color.Transparent,
        };
        var title = new Label
        {
            Text = "Playcast Companion",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 12f),
            Location = new Point(65, 16),
            AutoSize = true,
        };
        _headerSession.Dock = DockStyle.Right;
        header.Controls.AddRange(new Control[] { pic, title, _headerSession });
        return header;
    }

    private Panel BuildBottomBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Branding.UiSurface, Padding = new Padding(16, 10, 16, 10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _btnSave.AutoSize = false;
        _btnSave.Dock = DockStyle.Fill;
        _btnSave.Margin = Padding.Empty;
        _btnSave.BackColor = Branding.PlaycastGold;
        _btnSave.ForeColor = Color.FromArgb(26, 26, 26);
        _btnSave.FlatAppearance.BorderColor = Branding.PlaycastGold;
        _btnDiscard.AutoSize = false;
        _btnDiscard.Dock = DockStyle.Fill;
        _btnDiscard.Margin = new Padding(0, 0, 12, 0);
        _footerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _footerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _footerActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _footerActions.Controls.Add(_btnDiscard, 0, 0);
        _footerActions.Controls.Add(_btnSave, 1, 0);
        _btnDiscard.Click += (_, _) => LoadSettings();
        _lblFeedback.Dock = DockStyle.Fill;
        _lblFeedback.AutoSize = false;
        _lblFeedback.TextAlign = ContentAlignment.MiddleLeft;
        _lblFeedback.Margin = Padding.Empty;
        layout.Controls.Add(_lblFeedback, 0, 0);
        layout.Controls.Add(_footerActions, 1, 0);
        bar.Controls.Add(layout);
        return bar;
    }

    // ----- Status tab ----------------------------------------------------------

    private Panel BuildStatusTab()
    {
        var page = MakePage("Status");
        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
        AddDotRow(grid, 0, _dotGuest, "Guest mode", _valGuest);
        AddDotRow(grid, 1, _dotLights, "Lighting", _valLights);
        AddDotRow(grid, 2, _dotDiscord, "Discord", _valDiscord);
        AddDotRow(grid, 3, _dotAutostart, "Starts with Windows", _valAutostart);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
        layout.Controls.Add(_lblSummary, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        _btnPreview.Margin = new Padding(3, 18, 3, 3);
        layout.Controls.Add(_btnPreview, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

}

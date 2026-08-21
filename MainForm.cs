using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PlaycastCompanion;

/// <summary>
/// Settings/status window: branded header plus tabbed pages (Status,
/// Lighting, Discord, Advanced). The Lighting tab is a stack of brand-styled
/// cards — one per supported RGB ecosystem, each in that vendor's colors and
/// type, with its own toggle, live status and options.
/// </summary>
public sealed class MainForm : Form
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
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(584, 568);
        Icon = ctx.AppIcon;

        var tabs = BuildTabs();
        Controls.Add(tabs);
        Controls.Add(BuildBottomBar());
        Controls.Add(BuildHeader());
        tabs.BringToFront(); // fill takes the space left by header/bottom

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
            var c = _ctx.Config;
            _txtUser.Text = c.TargetUsername;
            _numTick.Value = Math.Clamp(c.TickSeconds, 1, 10);
            _chkDisconnected.Checked = c.IncludeDisconnectedSessions;
            _chkRegistry.Checked = c.RegistryWatch.Enabled;
            _chkRazer.Checked = c.RazerEnabled;
            _chkSteel.Checked = c.SteelSeries.Enabled;
            _chkLogi.Checked = c.Logitech.Enabled;
            _chkCorsair.Checked = c.Corsair.Enabled;
            _chkOpenRgb.Checked = c.OpenRgb.Enabled;
            _chkDynamic.Checked = c.DynamicLighting.Enabled;
            _txtOpenRgbProfile.Text = c.OpenRgb.BlackoutProfile;
            _chkDiscord.Checked = c.Discord.Enabled;
            _txtLine1.Text = c.Discord.Details;
            _txtLine2.Text = c.Discord.State;
            _chkPassthrough.Checked = c.Discord.GamePassthroughEnabled;
            _txtHostTemplate.Text = c.Discord.HostingTemplate;
            _lblAppIdState.Text = _ctx.Discord?.IsConfigured == true
                ? "Linked to your Discord app ✓   (the ID lives in config.json)"
                : "Not linked yet — add your Application ID to config.json (see README)";
            RefreshStatus();
            _refresh.Start();
        };
        FormClosed += (_, _) => _refresh.Stop();
    }

    // ----- chrome ------------------------------------------------------------

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Branding.Navy };
        var pic = new PictureBox
        {
            Image = _ctx.SquidImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(20, 14, 56, 56),
            BackColor = Color.Transparent,
        };
        var title = new Label
        {
            Text = "Playcast Companion",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 15f),
            Location = new Point(88, 16),
            AutoSize = true,
        };
        var subtitle = new Label
        {
            Text = "Lights out and status up while guests play on your PC.",
            ForeColor = Color.FromArgb(196, 208, 222),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.25f),
            Location = new Point(90, 48),
            AutoSize = true,
        };
        header.Controls.AddRange(new Control[] { pic, title, subtitle });
        return header;
    }

    private Control BuildTabs()
    {
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 4), BackColor = Branding.UiBack };
        var tabs = new DarkTabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildLightingTab());
        tabs.TabPages.Add(BuildDiscordTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        host.Controls.Add(tabs);
        return host;
    }

    private Panel BuildBottomBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Branding.UiBack };
        _btnSave.AutoSize = false;
        _btnSave.Size = new Size(124, 30);
        _btnSave.BackColor = Branding.PlaycastGold;
        _btnSave.ForeColor = Color.FromArgb(26, 26, 26);
        _btnSave.FlatAppearance.BorderColor = Branding.PlaycastGold;
        _btnSave.Location = new Point(ClientSize.Width - 138, 10);
        _btnSave.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _lblFeedback.Location = new Point(16, 16);
        bar.Controls.AddRange(new Control[] { _lblFeedback, _btnSave });
        return bar;
    }

    // ----- Status tab ----------------------------------------------------------

    private TabPage BuildStatusTab()
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

    // ----- Lighting tab (brand cards) -------------------------------------------

    private TabPage BuildLightingTab()
    {
        var page = new TabPage("Lighting") { BackColor = Branding.UiBack, Padding = new Padding(6, 6, 0, 6) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };

        var backends = _ctx.Lighting;
        // order matches TrayAppContext: Chroma, SteelSeries, Logitech, Corsair, OpenRGB, DynamicLighting
        flow.Controls.Add(RazerCard(backends[0]));
        flow.Controls.Add(SteelSeriesCard(backends[1]));
        flow.Controls.Add(LogitechCard(backends[2]));
        flow.Controls.Add(CorsairCard(backends[3]));
        flow.Controls.Add(OpenRgbCard(backends[4]));
        flow.Controls.Add(DynamicLightingCard(backends[5]));

        page.Controls.Add(flow);
        return page;
    }

    private Panel RazerCard(ILightingBackend backend)
    {
        // Razer-styled card: black surface, Chroma-green accent.
        var card = NewCard(Branding.RazerBlack);
        var official = Branding.LoadBrandImage("razer");
        if (official != null)
            AddLogoImage(card, official, new Rectangle(16, 26, 56, 56));
        else
            AddLogo(card, PaintRazerMark);
        AddWordmark(card, new Label
        {
            Text = "Razer",
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Branding.RazerGreen,
        });
        AddCardBody(card, backend, _chkRazer, Branding.RazerText,
            "Chroma SDK — keyboards, mice, headsets, pads & ChromaLink");

        var caption = CardSmallLabel("re-assert every (s)", Branding.RazerTextDim);
        caption.Location = new Point(316, 94);
        _numTick.BackColor = Branding.RazerSurface;
        _numTick.Location = new Point(438, 90);
        card.Controls.AddRange(new Control[] { caption, _numTick });
        return card;
    }

    private Panel SteelSeriesCard(ILightingBackend backend)
    {
        var card = NewCard(Branding.SteelDark);
        var official = Branding.LoadBrandImage("steelseries");
        if (official != null)
            AddLogoImage(card, official, new Rectangle(16, 26, 56, 56)); // optional user-supplied art (not shipped)
        else
            AddLogo(card, PaintSteelSeriesMark);
        AddWordmark(card, new Label
        {
            Text = "SteelSeries",
            Font = new Font("Segoe UI Black", 12.5f),
            ForeColor = Branding.SteelOrange,
        });
        AddCardBody(card, backend, _chkSteel, Branding.CardLightText,
            "GameSense — needs SteelSeries GG running");
        return card;
    }

    private Panel LogitechCard(ILightingBackend backend)
    {
        var card = NewCard(Branding.LogiDark);
        var official = Branding.LoadBrandImage("logitechg");
        if (official != null)
            AddLogoImage(card, official, new Rectangle(16, 26, 56, 56));
        else
            AddLogo(card, PaintLogitechMark);
        var logi = new Label
        {
            Text = "Logitech",
            Font = new Font("Segoe UI", 14.5f),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Location = new Point(92, 14),
            AutoSize = true,
        };
        var g = new Label
        {
            Text = "G",
            Font = new Font("Segoe UI", 14.5f, FontStyle.Bold),
            ForeColor = Branding.LogiBlue,
            BackColor = Color.Transparent,
            Location = new Point(184, 14),
            AutoSize = true,
        };
        card.Controls.AddRange(new Control[] { logi, g });
        AddCardBody(card, backend, _chkLogi, Branding.CardLightText,
            "LED SDK — via G HUB, or drop the wrapper DLL next to the app");
        return card;
    }

    private Panel CorsairCard(ILightingBackend backend)
    {
        var card = NewCard(Branding.CorsairBlack);
        var official = Branding.LoadBrandImage("corsair");
        if (official != null)
            AddLogoImage(card, official, new Rectangle(16, 26, 56, 56)); // optional user-supplied art (not shipped)
        else
            AddLogo(card, PaintCorsairMark);
        AddWordmark(card, new Label
        {
            Text = "Corsair",
            Font = new Font("Bahnschrift", 14f, FontStyle.Bold),
            ForeColor = Branding.CorsairYellow,
        });
        AddCardBody(card, backend, _chkCorsair, Branding.CardLightText,
            "iCUE — needs CUESDK.x64_2017.dll next to the app (from the CUE SDK)");
        return card;
    }

    private Panel OpenRgbCard(ILightingBackend backend)
    {
        var card = NewCard(Branding.OpenRgbDark);
        var official = Branding.LoadBrandImage("openrgb");
        if (official != null)
            AddLogoImage(card, official, new Rectangle(16, 26, 56, 56));
        else
            AddLogo(card, PaintOpenRgbMark);
        AddWordmark(card, new Label
        {
            Text = "OpenRGB",
            Font = new Font("Consolas", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(224, 224, 224),
        });
        AddCardBody(card, backend, _chkOpenRgb, Branding.CardLightText,
            "One server for motherboards, RAM, GPUs & more");

        var caption = CardSmallLabel("blackout profile", Branding.CardLightText);
        caption.Location = new Point(300, 94);
        _txtOpenRgbProfile.Location = new Point(392, 90);
        card.Controls.AddRange(new Control[] { caption, _txtOpenRgbProfile });
        return card;
    }

    private Panel DynamicLightingCard(ILightingBackend backend)
    {
        var card = NewCard(Color.White);
        card.BorderStyle = BorderStyle.FixedSingle;
        var official = Branding.LoadBrandImage("windows");
        if (official != null)
            AddLogoImage(card, official, new Rectangle(20, 32, 46, 46));
        else
            AddLogo(card, PaintWindowsMark);
        AddWordmark(card, new Label
        {
            Text = "Dynamic Lighting",
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Branding.WindowsBlue,
        });
        AddCardBody(card, backend, _chkDynamic, Color.FromArgb(70, 70, 70),
            "Built into Windows 11 — covers devices no other engine owns");
        return card;
    }

    // card skeleton helpers

    private static Panel NewCard(Color back) => new()
    {
        Size = new Size(506, 130),
        BackColor = back,
        Margin = new Padding(4, 4, 4, 8),
    };

    private static void AddLogoImage(Panel card, Image image, Rectangle bounds)
    {
        card.Controls.Add(new PictureBox
        {
            Image = image,
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = bounds,
            BackColor = Color.Transparent,
        });
    }

    private static void AddLogo(Panel card, Action<Graphics, Rectangle> painter)
    {
        var logo = new Panel { Bounds = new Rectangle(16, 26, 60, 60), BackColor = Color.Transparent };
        logo.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            painter(e.Graphics, logo.ClientRectangle);
        };
        card.Controls.Add(logo);
    }

    private static void AddWordmark(Panel card, Label wordmark)
    {
        wordmark.BackColor = Color.Transparent;
        wordmark.Location = new Point(92, 14);
        wordmark.AutoSize = true;
        card.Controls.Add(wordmark);
    }

    private void AddCardBody(Panel card, ILightingBackend backend, CheckBox enable, Color fore, string tagline)
    {
        // Roboto is Razer's own app body font (they install it system-wide);
        // it also reads cleanly on the other vendors' dark cards.
        var tag = CardSmallLabel(tagline, fore);
        tag.Font = new Font("Roboto", 8.5f);
        tag.Location = new Point(93, 44);
        tag.MaximumSize = new Size(400, 0);

        var dot = MakeDot();
        dot.BackColor = Color.Transparent;
        dot.AutoSize = true;
        dot.Location = new Point(92, 68);

        var status = new Label
        {
            AutoSize = true,
            ForeColor = fore,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 9f),
            Location = new Point(110, 69),
            Text = "—",
        };

        enable.Text = "Include in guest blackout";
        enable.ForeColor = fore;
        enable.BackColor = Color.Transparent;
        enable.Location = new Point(92, 92);

        card.Controls.AddRange(new Control[] { tag, dot, status, enable });
        _cards.Add((backend, dot, status));
    }

    private static Label CardSmallLabel(string text, Color fore) => new()
    {
        Text = text,
        ForeColor = fore,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 8.5f),
        AutoSize = true,
    };

    private static CheckBox MakeCardCheck() => new() { AutoSize = true };

    // brand marks (drawn, brand-styled — official logo files aren't bundled)

    private static void PaintRazerMark(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Branding.RazerGreen, 5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Triangle,
        };
        int cx = r.Width / 2;
        int bottom = r.Bottom - 8;
        g.DrawBezier(pen, cx, bottom, cx - 14, bottom - 18, cx - 16, bottom - 34, cx - 22, r.Top + 8);
        g.DrawBezier(pen, cx, bottom, cx, bottom - 20, cx, bottom - 38, cx, r.Top + 4);
        g.DrawBezier(pen, cx, bottom, cx + 14, bottom - 18, cx + 16, bottom - 34, cx + 22, r.Top + 8);
    }

    private static void PaintSteelSeriesMark(Graphics g, Rectangle r)
    {
        var rect = Rectangle.Inflate(r, -8, -8);
        using var pen = new Pen(Branding.SteelOrange, 6f);
        g.DrawEllipse(pen, rect);
        using var blade = new Pen(Color.White, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(blade, rect.Left + 10, rect.Bottom - 10, rect.Right - 10, rect.Top + 10);
    }

    private static void PaintLogitechMark(Graphics g, Rectangle r)
    {
        var rect = Rectangle.Inflate(r, -6, -6);
        using var pen = new Pen(Branding.LogiBlue, 4.5f);
        g.DrawEllipse(pen, rect);
        using var font = new Font("Segoe UI", 22f, FontStyle.Bold);
        var size = g.MeasureString("G", font);
        g.DrawString("G", font, Brushes.White,
            rect.Left + (rect.Width - size.Width) / 2 + 1,
            rect.Top + (rect.Height - size.Height) / 2 + 1);
    }

    private static void PaintCorsairMark(Graphics g, Rectangle r)
    {
        using var brush = new SolidBrush(Branding.CorsairYellow);
        for (int i = 0; i < 3; i++)
        {
            int y = r.Top + 10 + i * 15;
            g.FillPolygon(brush, new[]
            {
                new Point(r.Left + 8 + i * 5, y + 12),
                new Point(r.Right - 12, y),
                new Point(r.Left + 20 + i * 5, y + 16),
            });
        }
    }

    private static void PaintOpenRgbMark(Graphics g, Rectangle r)
    {
        using var red = new SolidBrush(Color.FromArgb(210, 235, 60, 60));
        using var green = new SolidBrush(Color.FromArgb(210, 60, 220, 90));
        using var blue = new SolidBrush(Color.FromArgb(210, 70, 110, 245));
        int d = 30;
        g.FillEllipse(red, r.Left + 15, r.Top + 4, d, d);
        g.FillEllipse(green, r.Left + 4, r.Top + 24, d, d);
        g.FillEllipse(blue, r.Left + 26, r.Top + 24, d, d);
    }

    private static void PaintWindowsMark(Graphics g, Rectangle r)
    {
        using var brush = new SolidBrush(Branding.WindowsBlue);
        int s = 26, gap = 4;
        int x = r.Left + (r.Width - (2 * s + gap)) / 2;
        int y = r.Top + (r.Height - (2 * s + gap)) / 2;
        g.FillRectangle(brush, x, y, s, s);
        g.FillRectangle(brush, x + s + gap, y, s, s);
        g.FillRectangle(brush, x, y + s + gap, s, s);
        g.FillRectangle(brush, x + s + gap, y + s + gap, s, s);
    }

    // ----- Discord tab -----------------------------------------------------------

    private TabPage BuildDiscordTab()
    {
        var page = MakePage("Discord");
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
        layout.Controls.Add(_chkDiscord, 0, 0);
        layout.Controls.Add(BuildDiscordPreview(), 0, 1);

        var rows = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        rows.Controls.Add(MakeCaption("Status line"), 0, 0);
        rows.Controls.Add(_txtLine1, 1, 0);
        rows.Controls.Add(MakeCaption("Second line (optional)"), 0, 1);
        rows.Controls.Add(_txtLine2, 1, 1);
        rows.Controls.Add(MakeCaption("Hosting template"), 0, 2);
        rows.Controls.Add(_txtHostTemplate, 1, 2);
        layout.Controls.Add(rows, 0, 2);
        layout.Controls.Add(_chkPassthrough, 0, 3);
        layout.Controls.Add(_lblAppIdState, 0, 4);
        page.Controls.Add(layout);
        return page;
    }

    private Panel BuildDiscordPreview()
    {
        var panel = new Panel
        {
            BackColor = Color.FromArgb(43, 45, 49),
            Size = new Size(380, 80),
            Margin = new Padding(3, 4, 3, 0),
        };
        var pic = new PictureBox
        {
            Image = _ctx.SquidImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(14, 16, 48, 48),
            BackColor = Color.Transparent,
        };
        var playing = new Label
        {
            Text = "Playing Playcast",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Location = new Point(74, 14),
            AutoSize = true,
        };
        _prevLine1.ForeColor = Color.FromArgb(219, 222, 225);
        _prevLine1.BackColor = Color.Transparent;
        _prevLine1.Font = new Font("Segoe UI", 9f);
        _prevLine1.Location = new Point(74, 36);
        _prevLine1.AutoSize = false;
        _prevLine1.Size = new Size(292, 18);
        _prevLine1.AutoEllipsis = true;
        var elapsed = new Label
        {
            Text = "12:34 elapsed",
            ForeColor = Color.FromArgb(148, 155, 164),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(74, 56),
            AutoSize = true,
        };
        panel.Controls.AddRange(new Control[] { pic, playing, _prevLine1, elapsed });
        return panel;
    }

    // ----- Advanced tab ----------------------------------------------------------

    private TabPage BuildAdvancedTab()
    {
        var page = MakePage("Advanced");
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };

        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        row.Controls.Add(MakeCaption("Guest account name"), 0, 0);
        row.Controls.Add(_txtUser, 1, 0);
        layout.Controls.Add(row, 0, 0);
        layout.Controls.Add(MakeBlurb("The Windows account Playcast signs guests into (default: NonsoleMode)."), 0, 1);
        layout.Controls.Add(_chkDisconnected, 0, 2);
        layout.Controls.Add(_chkRegistry, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 16, 0, 0),
        };
        var btnLog = MakeButton("Open log");
        btnLog.Click += (_, _) => _ctx.OpenLog();
        var btnFolder = MakeButton("Open install folder");
        btnFolder.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true }); }
            catch { }
        };
        buttons.Controls.AddRange(new Control[] { btnLog, btnFolder });
        layout.Controls.Add(buttons, 0, 4);

        var version = new Label
        {
            Text = $"Playcast Companion v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}",
            ForeColor = Branding.UiTextDim,
            AutoSize = true,
            Margin = new Padding(3, 18, 3, 3),
        };
        layout.Controls.Add(version, 0, 5);
        page.Controls.Add(layout);
        return page;
    }

    // ----- behavior ----------------------------------------------------------

    private void RefreshStatus()
    {
        bool guest = _ctx.GuestActive;
        _lblSummary.Text = guest
            ? "A guest is hosting — stealth mode is on."
            : "All quiet — your PC is yours.";
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
                    : $"{settled} of {enabled.Count} systems dark — see the Lighting tab");
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
                text.Text = "Off";
            }
            else if (backend.IsHolding)
            {
                dot.ForeColor = IsWarnStatus(backend.StatusText) ? Branding.Amber : Branding.Slate;
                text.Text = backend.StatusText;
            }
            else
            {
                dot.ForeColor = Branding.Green;
                text.Text = "Ready";
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
        var tabs = Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TabControl>()).First();
        for (int i = 0; i < tabs.TabCount; i++)
        {
            tabs.SelectedIndex = i;
            Application.DoEvents();
            RefreshStatus();
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"tab-{i}-{tabs.TabPages[i].Text.ToLowerInvariant()}.png"), ImageFormat.Png);

            // scrollable pages get a second, bottom-scrolled capture
            var scroller = tabs.TabPages[i].Controls.OfType<FlowLayoutPanel>().FirstOrDefault(f => f.AutoScroll);
            if (scroller != null)
            {
                scroller.AutoScrollPosition = new Point(0, scroller.DisplayRectangle.Height);
                Application.DoEvents();
                using var bmp2 = new Bitmap(Width, Height);
                DrawToBitmap(bmp2, new Rectangle(0, 0, Width, Height));
                bmp2.Save(Path.Combine(dir, $"tab-{i}-{tabs.TabPages[i].Text.ToLowerInvariant()}-scrolled.png"), ImageFormat.Png);
                scroller.AutoScrollPosition = new Point(0, 0);
            }
        }
    }

    // ----- small helpers -----------------------------------------------------

    private static void AddDotRow(TableLayoutPanel grid, int row, Label dot, string caption, Label value)
    {
        grid.Controls.Add(dot, 0, row);
        grid.Controls.Add(MakeCaption(caption), 1, row);
        grid.Controls.Add(value, 2, row);
    }

    private static void SetRow(Label dot, Label value, Color color, string text)
    {
        dot.ForeColor = color;
        value.Text = text;
    }

    private static TabPage MakePage(string title) => new(title)
    {
        BackColor = Branding.UiBack,
        Padding = new Padding(14, 12, 14, 12),
    };

    /// <summary>Owner-drawn TabControl — the stock one paints a light strip
    /// and border that can't otherwise be themed dark.</summary>
    private sealed class DarkTabControl : TabControl
    {
        public DarkTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(96, 30);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Branding.UiBack);
            for (int i = 0; i < TabCount; i++)
            {
                var rect = GetTabRect(i);
                bool selected = i == SelectedIndex;
                if (selected)
                {
                    using var surface = new SolidBrush(Branding.UiSurface);
                    e.Graphics.FillRectangle(surface, rect);
                    using var accent = new SolidBrush(Branding.PlaycastGold);
                    e.Graphics.FillRectangle(accent, rect.X, rect.Bottom - 2, rect.Width, 2);
                }
                TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, rect,
                    selected ? Color.White : Branding.UiTextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using var border = new Pen(Branding.UiBorder);
            var page = DisplayRectangle;
            page.Inflate(1, 1);
            e.Graphics.DrawRectangle(border, page);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }
    }

    private static Label MakeDot() => new()
    {
        AutoSize = true,
        Text = "●",
        Font = new Font("Segoe UI", 9.75f),
        Margin = new Padding(3, 6, 0, 6),
    };

    private static Label MakeValue() => new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.75f),
        ForeColor = Branding.UiText,
        Text = "—",
        Margin = new Padding(3, 6, 3, 6),
    };

    private static Label MakeCaption(string text) => new()
    {
        AutoSize = true,
        ForeColor = Branding.UiTextDim,
        Text = text,
        Margin = new Padding(6, 6, 14, 6),
    };

    private static Label MakeBlurb(string text) => new()
    {
        AutoSize = true,
        ForeColor = Branding.UiTextDim,
        Text = text,
        Margin = new Padding(3, 2, 3, 2),
    };

    private static Button MakeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(3, 0, 6, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Branding.UiSurface,
            ForeColor = Branding.UiText,
        };
        button.FlatAppearance.BorderColor = Branding.UiBorder;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 45, 45);
        return button;
    }

    private static TextBox MakeDarkTextBox(int width) => new()
    {
        Width = width,
        BackColor = Branding.UiSurface,
        ForeColor = Branding.UiText,
        BorderStyle = BorderStyle.FixedSingle,
    };
}

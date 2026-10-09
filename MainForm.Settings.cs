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
    // ----- Discord tab -----------------------------------------------------------

    private Panel BuildDiscordTab()
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

    private Panel BuildAdvancedTab()
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

}

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

}

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

    private static Panel MakePage(string title) => new()
    {
        Text = title,
        BackColor = Branding.UiBack,
        Padding = new Padding(14, 12, 14, 12),
        AutoScroll = true,
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

namespace PlaycastCompanion;

public sealed partial class MainForm
{
    private Panel BuildLightingTab()
    {
        var page = MakePage("Lighting");
        var list = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        var names = new[] { "Razer Chroma", "SteelSeries", "Logitech G", "Corsair iCUE", "OpenRGB", "Windows Dynamic Lighting" };
        var descriptions = new[] { "Chroma SDK · Synapse", "GameSense · SteelSeries GG", "LED SDK · G HUB",
            "CUE SDK · iCUE", "SDK server · motherboards, RAM and GPUs", "Windows 11 · devices not owned by another enabled engine" };
        var toggles = new[] { _chkRazer, _chkSteel, _chkLogi, _chkCorsair, _chkOpenRgb, _chkDynamic };
        for (var index = 0; index < names.Length; index++)
            list.Controls.Add(BuildLightingRow(_ctx.Lighting[index], toggles[index], names[index], descriptions[index]));
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 18, 0, 0) };
        options.Controls.Add(MakeCaption("Re-assert every (seconds)"));
        options.Controls.Add(_numTick);
        options.Controls.Add(MakeCaption("OpenRGB blackout profile"));
        options.Controls.Add(_txtOpenRgbProfile);
        list.Controls.Add(options);
        page.Controls.Add(list);
        return page;
    }

    private Control BuildLightingRow(ILightingBackend backend, CheckBox toggle, string name, string description)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, Height = 70, Dock = DockStyle.Top,
            Padding = new Padding(0, 10, 0, 10), Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        var labels = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        labels.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = Branding.UiText, Font = new Font("Segoe UI Semibold", 10) });
        labels.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = Branding.UiTextDim, Font = new Font("Segoe UI", 9) });
        var status = MakeValue();
        status.AutoSize = false;
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        var dot = MakeDot();
        dot.Visible = false;
        toggle.Text = "Include";
        toggle.Dock = DockStyle.Fill;
        toggle.ForeColor = Branding.UiText;
        row.Controls.Add(labels, 0, 0);
        row.Controls.Add(status, 1, 0);
        row.Controls.Add(toggle, 2, 0);
        row.Paint += (_, args) => { using var pen = new Pen(Branding.UiBorder); args.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); };
        _cards.Add((backend, dot, status));
        return row;
    }
}

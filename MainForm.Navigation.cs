using PlaycastCompanion.UI;

namespace PlaycastCompanion;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Label _headerSession = new()
    {
        Width = 270, ForeColor = Branding.UiTextDim,
        TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 20, 0),
    };
    private readonly TableLayoutPanel _footerActions = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = false,
        Margin = Padding.Empty, Padding = Padding.Empty,
    };
    private readonly Button _btnDiscard = MakeButton("Discard");
    private LogAnalysisPanel? _analysisPanel;
    private string _selectedPage = "Overview";

    private Control BuildNavigationShell()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Branding.UiSurface, Padding = new Padding(10, 22, 10, 0), Margin = Padding.Empty,
        };
        sidebar.Controls.Add(new Label { Text = "Playcast\nCompanion", AutoSize = true, ForeColor = Branding.UiText,
            Font = new Font("Segoe UI Semibold", 13), Margin = new Padding(10, 0, 0, 24) });
        AddNavigation(sidebar, "Overview", BuildStatusTab(), "Session detection and your enabled integrations.");
        AddNavigation(sidebar, "Lighting", BuildLightingTab(), "Choose which systems join the guest blackout.");
        AddNavigation(sidebar, "Discord", BuildDiscordTab(), "Presence settings and a preview of the hosting message.");
        AddNavigation(sidebar, "Session logs", BuildSessionLogTab(), "Automatic local captures for active guest sessions.");
        _analysisPanel = new LogAnalysisPanel();
        AddNavigation(sidebar, "Analyze logs", _analysisPanel, "Explore saved captures without changing live settings.");
        AddNavigation(sidebar, "Settings", BuildAdvancedTab(), "Guest detection, startup and application settings.");
        shell.Controls.Add(sidebar, 0, 0);
        shell.Controls.Add(_pageHost, 1, 0);
        SelectPage("Overview");
        return shell;
    }

    private void AddNavigation(FlowLayoutPanel sidebar, string title, Control content, string subtitle)
    {
        var button = MakeButton(title);
        button.AutoSize = false;
        button.Size = new Size(150, 42);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Margin = new Padding(0, 2, 0, 3);
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => SelectPage(title);
        sidebar.Controls.Add(button);
        _navigation.Add(title, button);
        var page = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(20, 18, 20, 12) };
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 36, ForeColor = Branding.UiText,
            Font = new Font("Segoe UI Semibold", 18) };
        var description = new Label { Text = subtitle, Dock = DockStyle.Top, Height = 35,
            ForeColor = Branding.UiTextDim, AutoEllipsis = true };
        content.Dock = DockStyle.Fill;
        content.Padding = new Padding(0, 12, 0, 0);
        page.Controls.Add(content);
        page.Controls.Add(description);
        page.Controls.Add(heading);
        _pageHost.Controls.Add(page);
        _pages.Add(title, page);
    }

    private void SelectPage(string title)
    {
        _selectedPage = title;
        foreach (var (name, page) in _pages) page.Visible = name == title;
        foreach (var (name, button) in _navigation)
        {
            button.BackColor = name == title ? Color.FromArgb(42, 53, 69) : Branding.UiSurface;
            button.ForeColor = name == title ? Branding.PlaycastGold : Branding.UiTextDim;
        }
        _footerActions.Visible = title != "Analyze logs";
        RefreshEditFeedback();
    }
}

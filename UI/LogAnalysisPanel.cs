using System.Text.Json;
using PlaycastCompanion.LogAnalysis;

namespace PlaycastCompanion.UI;

public sealed partial class LogAnalysisPanel : UserControl
{
    private readonly SessionLogImporter _importer;
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 42, ForeColor = Branding.UiTextDim };
    private readonly Label _warning = new() { Dock = DockStyle.Top, Height = 40, ForeColor = Branding.Amber, Visible = false };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        HideSelection = false, MultiSelect = false, VirtualMode = true, BorderStyle = BorderStyle.None };
    private readonly TextBox _inspector = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9), BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _source = new() { Dock = DockStyle.Top, Height = 38, AutoEllipsis = true };
    private readonly LogPlot _plot = new() { Dock = DockStyle.Top, Height = 190, Visible = false };
    private readonly FlowLayoutPanel _importBar = new() { Dock = DockStyle.Top, Height = 44, WrapContents = false };
    private readonly FlowLayoutPanel _viewBar = new() { Dock = DockStyle.Top, Height = 40, WrapContents = true };
    private readonly Panel _recordsHost = new() { Dock = DockStyle.Fill };
    private readonly List<(string[] Columns, object Value)> _rows = [];
    private readonly Dictionary<string, Button> _viewButtons = [];
    private SessionLogProjection? _projection;
    private LogImportResult? _capture;
    private CancellationTokenSource? _importCancellation;
    private string _view = "Timeline";

    public LogAnalysisPanel() : this(new SessionLogImporter()) { }

    public LogAnalysisPanel(SessionLogImporter importer)
    {
        _importer = importer;
        BackColor = Branding.UiBack;
        ForeColor = Branding.UiText;
        AllowDrop = true;
        BuildLayout();
        AddImportButton("Import log files", ChooseFiles, true);
        AddImportButton("Import folder", ChooseFolder);
        AddImportButton("Sample session", async () => await LoadSampleAsync());
        foreach (var view in new[] { "Timeline", "Communication", "Scripts", "Games", "Health" })
        {
            var button = NewButton(view);
            button.Click += (_, _) => SelectView(view);
            _viewButtons.Add(view, button);
            _viewBar.Controls.Add(button);
        }
        _list.RetrieveVirtualItem += (_, args) => args.Item = args.ItemIndex < _rows.Count ? new ListViewItem(_rows[args.ItemIndex].Columns) : new ListViewItem("");
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndices.Count > 0) Inspect(_rows[_list.SelectedIndices[0]].Value); };
        _plot.RecordSelected += record => Inspect(record);
        DragEnter += (_, args) => args.Effect = args.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, args) => { if (args.Data?.GetData(DataFormats.FileDrop) is string[] paths) await ImportAsync(paths); };
        _summary.Text = "Import JSONL files or drop a session folder here. Up to 128 MiB / 50,000 records. Files stay on this computer.";
        SelectView("Timeline");
    }

    private void BuildLayout()
    {
        var details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        _inspector.BackColor = Branding.UiSurface;
        _inspector.ForeColor = Branding.UiText;
        _source.ForeColor = Branding.UiTextDim;
        details.Controls.Add(_inspector);
        details.Controls.Add(_source);
        _source.BringToFront();
        _list.BackColor = Branding.UiBack;
        _list.ForeColor = Branding.UiText;
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += (_, args) =>
        {
            using var brush = new SolidBrush(Branding.UiSurface);
            args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, args.Header?.Text, Font, args.Bounds, Branding.UiTextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        _list.DrawSubItem += (_, args) =>
        {
            using var brush = new SolidBrush(args.Item?.Selected == true ? Color.FromArgb(42, 53, 69) : Branding.UiBack);
            args.Graphics.FillRectangle(brush, args.Bounds);
            TextRenderer.DrawText(args.Graphics, args.SubItem?.Text ?? "", Font, args.Bounds, Branding.UiText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _recordsHost.Controls.Add(_list);
        _recordsHost.Controls.Add(_plot);
        _recordsHost.SizeChanged += (_, _) => SizePlot();
        _list.BringToFront();
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        body.Controls.Add(_recordsHost, 0, 0);
        body.Controls.Add(details, 0, 1);
        Controls.Add(body);
        Controls.Add(_viewBar);
        Controls.Add(_warning);
        Controls.Add(_summary);
        Controls.Add(_importBar);
        body.BringToFront();
    }

    private static Button NewButton(string text) => new() { Text = text, AutoSize = true, Height = 34,
        FlatStyle = FlatStyle.Flat, BackColor = Branding.UiSurface, ForeColor = Branding.UiText, Margin = new Padding(0, 0, 8, 0) };

    private void AddImportButton(string text, Action action, bool primary = false)
    {
        var button = NewButton(text);
        if (primary) { button.BackColor = Branding.PlaycastGold; button.ForeColor = Color.FromArgb(32, 26, 12); }
        button.Click += (_, _) => action();
        _importBar.Controls.Add(button);
    }

    private async void ChooseFiles()
    {
        using var dialog = new OpenFileDialog { Title = "Import Companion logs", Multiselect = true,
            Filter = "JSONL log files|*.jsonl;*.ndjson;*.log|All files|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await ImportAsync(dialog.FileNames);
    }

    private async void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a Companion session folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) await ImportAsync([dialog.SelectedPath]);
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        _importCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        _summary.Text = "Reading local log files…";
        try
        {
            var (capture, projection) = await Task.Run(async () =>
            {
                var imported = await _importer.ImportAsync(ExpandPaths(paths), cancellation.Token);
                return (imported, imported.Records.Count > 0 ? new SessionLogProjection(imported) : null);
            }, cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed) return;
            ApplyCapture(capture, projection);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!IsDisposed && !cancellation.IsCancellationRequested) { _warning.Visible = true; _warning.Text = $"Import failed; previous view retained. {exception.Message}"; } }
        finally { if (_importCancellation == cancellation) _importCancellation = null; cancellation.Dispose(); }
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => new[] { ".jsonl", ".ndjson", ".log" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Take(5001));
            else files.Add(path);
            if (files.Count > 5000) throw new IOException("Choose a smaller folder (maximum 5,000 log files).");
        }
        return files;
    }

    private void ApplyCapture(LogImportResult capture, SessionLogProjection? projection = null)
    {
        if (capture.Records.Count == 0)
        {
            _summary.Text = "No valid records imported. Previous view retained.";
            _warning.Visible = true;
            _warning.Text = string.Join(" · ", capture.Issues.Take(2).Select(issue => issue.Message));
            return;
        }
        _capture = capture;
        _projection = projection ?? new SessionLogProjection(capture);
        var first = capture.Records.Min(record => record.RecordedAtUtc);
        var last = capture.Records.Max(record => record.RecordedAtUtc);
        _summary.Text = $"{capture.FilesImported} files · {capture.Records.Count:N0} records · {first:yyyy-MM-dd HH:mm:ss}–{last:yyyy-MM-dd HH:mm:ss} UTC\n{(last - first).TotalSeconds:N1} s recorded range";
        _warning.Text = string.Join(" · ", new[] { capture.IssueCount > 0 ? $"{capture.IssueCount} import issues; partial capture" : "",
            capture.RawSecretRecordCount > 0 ? $"{capture.RawSecretRecordCount} records captured with secrets enabled" : "",
            string.Join("; ", capture.Issues.Take(2).Select(issue => $"{Path.GetFileName(issue.SourceFile)}:{issue.LineNumber} {issue.Message}")) }.Where(text => text.Length > 0));
        _warning.Visible = _warning.Text.Length > 0;
        SelectView(_view);
    }

    private async Task LoadSampleAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PlaycastCompanionSample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample-session.jsonl");
        try
        {
            using var stream = typeof(LogAnalysisPanel).Assembly.GetManifestResourceStream("PlaycastCompanion.Assets.sample-session.jsonl")!;
            await using (var file = File.Create(path)) await stream.CopyToAsync(file);
            await ImportAsync([path]);
            if (_capture?.Records.FirstOrDefault()?.SourceFile == path) _summary.Text = "Synthetic sample session · " + _summary.Text;
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    public void LoadCaptureForSnapshot(string path)
    {
        ApplyCapture(Task.Run(() => _importer.ImportAsync([path], CancellationToken.None)).GetAwaiter().GetResult());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _importCancellation?.Cancel();
        base.Dispose(disposing);
    }
}

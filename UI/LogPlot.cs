using System.Drawing.Drawing2D;
using PlaycastCompanion.LogAnalysis;

namespace PlaycastCompanion.UI;

public sealed class LogPlot : Control
{
    private IReadOnlyList<LogRecord> _timeline = [];
    private IReadOnlyList<HealthSample> _health = [];
    private readonly List<(PointF Point, LogRecord Record, string Description)> _hits = [];
    private readonly ToolTip _tooltip = new();
    public event Action<LogRecord>? RecordSelected;

    public LogPlot()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Branding.UiBack;
        AccessibleRole = AccessibleRole.Graphic;
    }

    public void ShowTimeline(IReadOnlyList<LogRecord> records)
    {
        _timeline = records;
        _health = [];
        AccessibleName = "Stored session records by category over time. The same records are available in the list.";
        Invalidate();
    }

    public void ShowHealth(IReadOnlyList<HealthSample> samples)
    {
        _timeline = [];
        _health = samples;
        AccessibleName = "Companion working set in MiB and cumulative CPU time in seconds. Unavailable measurements are absent.";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        _hits.Clear();
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_timeline.Count > 0) DrawTimeline(args.Graphics);
        else if (_health.Count > 0) DrawHealth(args.Graphics);
        else DrawText(args.Graphics, "No available measurements in this import.", new PointF(0, 20));
    }

    private void DrawTimeline(Graphics graphics)
    {
        var names = new[] { "session", "realtime", "ipc", "scripts", "games", "diagnostics" };
        var bounds = new RectangleF(90, 6, Math.Max(20, Width - 105), Math.Max(60, Height - 38));
        var start = _timeline.Min(record => record.RecordedAtUtc);
        var end = _timeline.Max(record => record.RecordedAtUtc);
        var range = Math.Max(1, (end - start).TotalSeconds);
        Frame(graphics, bounds, start, end);
        using var brush = new SolidBrush(Branding.PlaycastGold);
        for (var index = 0; index < names.Length; index++)
        {
            var y = bounds.Top + (index + .5f) * bounds.Height / names.Length;
            DrawText(graphics, names[index], new PointF(0, y - Font.Height / 2));
            var occupied = new HashSet<int>();
            foreach (var record in _timeline.Where(record => record.Category == names[index]))
            {
                var x = bounds.Left + 5 + (float)((record.RecordedAtUtc - start).TotalSeconds / range) * (bounds.Width - 10);
                if (!occupied.Add((int)(x / 8))) continue;
                graphics.FillEllipse(brush, x - 3, y - 3, 6, 6);
                _hits.Add((new PointF(x, y), record, $"{record.RecordedAtUtc:HH:mm:ss.fff} UTC · {record.Category} · {record.EventType}"));
            }
        }
    }

    private void DrawHealth(Graphics graphics)
    {
        var half = Height / 2f;
        DrawMetric(graphics, new RectangleF(75, 18, Math.Max(20, Width - 90), half - 44),
            _health.Where(sample => sample.WorkingSetBytes.HasValue).Select(sample => (sample, sample.WorkingSetBytes!.Value / 1048576)).ToArray(), "Memory (MiB)");
        DrawMetric(graphics, new RectangleF(75, half + 18, Math.Max(20, Width - 90), half - 44),
            _health.Where(sample => sample.CpuSeconds.HasValue).Select(sample => (sample, sample.CpuSeconds!.Value)).ToArray(), "CPU time (s)");
    }

    private void DrawMetric(Graphics graphics, RectangleF bounds, (HealthSample Sample, double Value)[] points, string unit)
    {
        DrawText(graphics, unit, new PointF(0, bounds.Top - 18));
        if (points.Length == 0) { DrawText(graphics, "Unavailable", new PointF(bounds.Left, bounds.Top + 10)); return; }
        var start = points.Min(point => point.Sample.RecordedAtUtc);
        var end = points.Max(point => point.Sample.RecordedAtUtc);
        var duration = Math.Max(1, (end - start).TotalSeconds);
        var low = points.Min(point => point.Value);
        var high = points.Max(point => point.Value);
        var padding = Math.Max(.1, Math.Max((high - low) * .12, high * .01));
        low = Math.Max(0, low - padding);
        high += padding;
        Frame(graphics, bounds, start, end);
        DrawText(graphics, high.ToString("N1"), new PointF(12, bounds.Top));
        DrawText(graphics, low.ToString("N1"), new PointF(12, bounds.Bottom - Font.Height));
        using var brush = new SolidBrush(Branding.PlaycastGold);
        var stride = Math.Max(1, (int)Math.Ceiling(points.Length / 1500d));
        for (var index = 0; index < points.Length; index += stride)
        {
            var point = points[index];
            var x = bounds.Left + 5 + (float)((point.Sample.RecordedAtUtc - start).TotalSeconds / duration) * (bounds.Width - 10);
            var y = bounds.Bottom - 5 - (float)((point.Value - low) / (high - low)) * (bounds.Height - 10);
            graphics.FillEllipse(brush, x - 3, y - 3, 6, 6);
            _hits.Add((new PointF(x, y), point.Sample.SourceRecord, $"{point.Sample.RecordedAtUtc:HH:mm:ss} UTC · PID {point.Sample.ProcessId} · {point.Value:N2} {unit}"));
        }
    }

    private void Frame(Graphics graphics, RectangleF bounds, DateTimeOffset start, DateTimeOffset end)
    {
        using var pen = new Pen(Branding.UiBorder);
        graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        DrawText(graphics, start.ToString("HH:mm:ss"), new PointF(bounds.Left, bounds.Bottom + 3));
        var label = end.ToString("HH:mm:ss") + " UTC";
        DrawText(graphics, label, new PointF(bounds.Right - graphics.MeasureString(label, Font).Width, bounds.Bottom + 3));
    }

    private void DrawText(Graphics graphics, string text, PointF point)
    {
        using var brush = new SolidBrush(Branding.UiTextDim);
        graphics.DrawString(text, Font, brush, point);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var hit = Closest(args.Location);
        _tooltip.SetToolTip(this, hit?.Description ?? "");
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (Closest(args.Location) is { } hit) RecordSelected?.Invoke(hit.Record);
    }

    private (PointF Point, LogRecord Record, string Description)? Closest(Point point)
    {
        foreach (var hit in _hits)
            if (Math.Abs(hit.Point.X - point.X) < 12 && Math.Abs(hit.Point.Y - point.Y) < 12) return hit;
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tooltip.Dispose();
        base.Dispose(disposing);
    }
}

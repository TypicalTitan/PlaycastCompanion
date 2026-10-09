using System.Text;
using System.Text.Json;

namespace PlaycastCompanion.LogAnalysis;

public interface ILogImportFileSource
{
    long GetLength(string path);
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
}

public sealed class SessionLogImporter
{
    private readonly ILogImportFileSource _files;
    private readonly LogImportLimits _limits;

    public SessionLogImporter(ILogImportFileSource? files = null, LogImportLimits? limits = null)
    {
        _files = files ?? new DiskLogImportFileSource();
        _limits = limits ?? new LogImportLimits();
        if (_limits.MaxTotalBytes < 1 || _limits.MaxRecords < 1 || _limits.MaxLineBytes < 1
            || _limits.MaxReportedIssues < 1) throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<LogImportResult> ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var state = new ImportAccumulator(_limits);
        var selected = SelectFiles(paths, state, cancellationToken);
        if (selected == null) return state.Result;
        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Stopped) break;
            await ImportFileAsync(file, state, cancellationToken).ConfigureAwait(false);
        }
        return state.Result;
    }

    private List<string>? SelectFiles(IEnumerable<string> paths, ImportAccumulator state, CancellationToken token)
    {
        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var full = Path.GetFullPath(path);
                if (!seen.Add(full)) continue;
                var extension = Path.GetExtension(full);
                if (!new[] { ".jsonl", ".ndjson", ".log" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                { state.Issue(full, null, "Unsupported file type; select JSONL, NDJSON, or LOG files."); continue; }
                var length = _files.GetLength(full);
                if (length < 0 || length > _limits.MaxTotalBytes - bytes)
                { state.Issue(full, null, $"Selection exceeds the {_limits.MaxTotalBytes:N0}-byte import limit."); return null; }
                bytes += length;
                selected.Add(full);
            }
            catch (Exception error) when (IsFileError(error)) { state.Issue(path, null, error.Message); }
        }
        return selected;
    }

    private async Task ImportFileAsync(string file, ImportAccumulator state, CancellationToken token)
    {
        try
        {
            await using var stream = await _files.OpenReadAsync(file, token).ConfigureAwait(false);
            state.FilesImported++;
            var reader = new BoundedLogLineReader(_limits.MaxLineBytes);
            await reader.ReadAsync(stream, state, (line, number, oversized) =>
            {
                if (oversized) { state.Issue(file, number, $"Line exceeds the {_limits.MaxLineBytes:N0}-byte line limit."); return; }
                ImportLine(file, number, line, state);
            }, token).ConfigureAwait(false);
            if (state.ByteLimitReached) state.Issue(file, null, "Import byte limit reached while reading a growing file.");
        }
        catch (Exception error) when (IsFileError(error)) { state.Issue(file, null, error.Message); }
    }

    private static void ImportLine(string file, int number, ReadOnlyMemory<byte> line, ImportAccumulator state)
    {
        if (line.IsEmpty) return;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(line.Span).Trim();
            if (number == 1) text = text.TrimStart('\uFEFF');
            if (text.Length == 0) return;
            if (state.RecordLimitReached) { state.Issue(file, number, "Import record limit reached."); state.Stopped = true; return; }
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Record must be a JSON object.");
            state.Records.Add(new LogRecordNormalizer().Normalize(document.RootElement, file, number, state.Records.Count + 1));
        }
        catch (Exception error) when (error is JsonException or FormatException or DecoderFallbackException)
        { state.Issue(file, number, "Invalid record: " + error.Message); }
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}

internal sealed class DiskLogImportFileSource : ILogImportFileSource
{
    public long GetLength(string path) => new FileInfo(path).Length;
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
}

internal sealed class ImportAccumulator(LogImportLimits limits)
{
    public List<LogRecord> Records { get; } = [];
    private readonly List<LogImportIssue> _issues = [];
    private int _issueCount;
    public int FilesImported { get; set; }
    public long BytesRead { get; set; }
    public bool Stopped { get; set; }
    public bool ByteLimitReached { get; set; }
    public bool RecordLimitReached => Records.Count >= limits.MaxRecords;
    public long RemainingBytes => limits.MaxTotalBytes - BytesRead;
    public LogImportResult Result => new(Records.ToArray(), _issues.ToArray(), FilesImported, BytesRead,
        _issueCount > 0 || Stopped, _issueCount);
    public void Issue(string file, int? line, string message)
    {
        _issueCount++;
        if (_issues.Count < limits.MaxReportedIssues) _issues.Add(new(file, line, message));
    }
}

using System.Text.Json;

namespace PlaycastCompanion;

public sealed partial class GameInventorySource
{
    private void ReadLegendary(string? manifest, List<InstalledGame> games,
        List<SourceCoverage> sources, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(manifest)) return;
        var errors = new List<SnapshotError>();
        int scanned = 0;
        try
        {
            if (!_files.FileExists(manifest)) { sources.Add(new(manifest, "not_found", 0, errors)); return; }
            var text = _files.ReadAllText(manifest);
            if (text.Length > 4 * 1024 * 1024) throw new FormatException("Legendary inventory exceeds 4 MiB.");
            using var document = JsonDocument.Parse(text);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                try
                {
                    var item = entry.Value;
                    var id = item.TryGetProperty("app_name", out var app) ? app.GetString() : entry.Name;
                    var root = item.GetProperty("install_path").GetString();
                    if (string.IsNullOrEmpty(id) || root is null || !Path.IsPathFullyQualified(root))
                        throw new FormatException("Legendary installation metadata is invalid.");
                    root = Path.GetFullPath(root);
                    if (!_files.DirectoryExists(root)) throw new DirectoryNotFoundException("Legendary installation directory unavailable: " + root);
                    var name = item.TryGetProperty("title", out var title) ? title.GetString() ?? id : id;
                    games.Add(new("epic", id, name, root, manifest));
                }
                catch (Exception error) when (error is not OperationCanceledException) { errors.Add(SnapshotFailures.From(manifest, error)); }
            }
            sources.Add(new(manifest, errors.Count == 0 ? "available" : "partial", scanned, errors));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            errors.Add(SnapshotFailures.From(manifest, error));
            sources.Add(new(manifest, SnapshotFailures.Status(error), scanned, errors));
        }
    }
}

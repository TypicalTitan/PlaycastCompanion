using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PlaycastCompanion;

/// <summary>Reads launcher manifests without launching games or invoking any scripts.</summary>
public sealed class GameInventorySource : IGameInventorySource
{
    private readonly IInventoryFileSystem _files;
    private readonly IReadOnlyList<string>? _steamRoots;
    private readonly string _epicManifestDirectory;

    public GameInventorySource() : this(new InventoryFileSystem(), null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests")) { }

    public GameInventorySource(IInventoryFileSystem files, IEnumerable<string>? steamRoots,
        string epicManifestDirectory)
    {
        _files = files;
        _steamRoots = steamRoots?.ToArray();
        _epicManifestDirectory = epicManifestDirectory;
    }

    public GameInventorySnapshot Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var games = new List<InstalledGame>();
        var sources = new List<SourceCoverage>();
        var discoveryErrors = new List<SnapshotError>();
        var steamRoots = _steamRoots ?? DiscoverSteamRoots(discoveryErrors);
        sources.Add(new("steam_discovery", discoveryErrors.Count == 0 ? "available" : "partial",
            steamRoots.Count, discoveryErrors));
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoverLibraries(root, libraries, sources);
        }
        foreach (var library in libraries)
            ReadSteamLibrary(library, games, sources, cancellationToken);
        ReadEpic(games, sources, cancellationToken);
        return new(games.DistinctBy(game => (game.Store, game.Id, game.InstallRoot)).ToArray(),
            sources, new[]
            {
                "Steam and Epic manifests only; GOG, Xbox, Ubisoft, EA, standalone and unregistered games are not inventoried.",
                "Steam discovery uses this Companion account's registry and common installation paths; another account's custom libraries may be unavailable.",
                "Manifests indicate launcher installations, not Playcast entitlement or game launchability.",
                "Running-game matching uses executables under known install roots in target WTS sessions; protected paths and games without manifests remain unknown."
            });
    }

    private static IReadOnlyList<string> DiscoverSteamRoots(List<SnapshotError> errors)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (var (hive, view, subkey, value) in new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam", "InstallPath")
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subkey, false);
                if (key?.GetValue(value) is string path && Path.IsPathFullyQualified(path))
                    roots.Add(Path.GetFullPath(path));
            }
            catch (Exception error) { errors.Add(SnapshotFailures.From($"{hive}\\{subkey}", error)); }
        }
        return roots.ToArray();
    }

    private void DiscoverLibraries(string root, HashSet<string> libraries, List<SourceCoverage> sources)
    {
        var errors = new List<SnapshotError>();
        var manifest = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (!Path.IsPathFullyQualified(root)) throw new FormatException("Steam root must be absolute.");
            libraries.Add(Path.GetFullPath(root));
            if (!_files.FileExists(manifest))
            {
                sources.Add(new(manifest, "not_found", 0, errors));
                return;
            }
            var folders = VdfDocument.Parse(_files.ReadAllText(manifest)).Child("libraryfolders");
            if (folders is null) throw new FormatException("Missing libraryfolders section.");
            foreach (var (key, node) in folders.Children)
            {
                if (!int.TryParse(key, out _)) continue;
                var path = node.Value ?? node.Child("path")?.Value;
                if (path is null || !Path.IsPathFullyQualified(path))
                    errors.Add(new(manifest, "invalid_manifest", $"Library {key} has no absolute path."));
                else libraries.Add(Path.GetFullPath(path));
            }
            sources.Add(new(manifest, errors.Count == 0 ? "available" : "partial", folders.Children.Count, errors));
        }
        catch (Exception error)
        {
            errors.Add(SnapshotFailures.From(manifest, error));
            sources.Add(new(manifest, SnapshotFailures.Status(error), 0, errors));
        }
    }

    private void ReadSteamLibrary(string library, List<InstalledGame> games,
        List<SourceCoverage> sources, CancellationToken cancellationToken)
    {
        var source = Path.Combine(library, "steamapps");
        var errors = new List<SnapshotError>();
        int scanned = 0;
        try
        {
            if (!_files.DirectoryExists(source))
            {
                sources.Add(new(source, "not_found", 0, errors));
                return;
            }
            foreach (var manifest in _files.EnumerateFiles(source, "appmanifest_*.acf"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                try
                {
                    var game = ReadSteamManifest(source, manifest, errors);
                    if (game is not null) games.Add(game);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { errors.Add(SnapshotFailures.From(manifest, error)); }
            }
            sources.Add(new(source, errors.Count == 0 ? "available" : "partial", scanned, errors));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            errors.Add(SnapshotFailures.From(source, error));
            sources.Add(new(source, SnapshotFailures.Status(error), scanned, errors));
        }
    }

    private InstalledGame? ReadSteamManifest(string steamApps, string manifest, List<SnapshotError> errors)
    {
        var state = VdfDocument.Parse(_files.ReadAllText(manifest)).Child("AppState")
            ?? throw new FormatException("Missing AppState section.");
        var id = RequireValue(state, "appid");
        var name = RequireValue(state, "name");
        var directory = RequireValue(state, "installdir");
        var common = Path.Combine(steamApps, "common");
        var installRoot = Path.GetFullPath(Path.Combine(common, directory));
        if (Path.IsPathRooted(directory) || !ContainsExecutable(common, installRoot))
            throw new FormatException("Install directory escapes the library common directory.");
        if (!uint.TryParse(RequireValue(state, "StateFlags"), out var flags) || (flags & 4) == 0)
            return null; // Steam FullyInstalled flag; a downloading manifest does not prove installation.
        if (!_files.DirectoryExists(installRoot))
        {
            errors.Add(new(manifest, "missing_install_directory", installRoot));
            return null;
        }
        return new("steam", id, name, installRoot, manifest);
    }

    private void ReadEpic(List<InstalledGame> games, List<SourceCoverage> sources,
        CancellationToken cancellationToken)
    {
        var errors = new List<SnapshotError>();
        int scanned = 0;
        try
        {
            if (!_files.DirectoryExists(_epicManifestDirectory))
            {
                sources.Add(new(_epicManifestDirectory, "not_found", 0, errors));
                return;
            }
            foreach (var manifest in _files.EnumerateFiles(_epicManifestDirectory, "*.item"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                try
                {
                    var game = ReadEpicManifest(manifest, errors);
                    if (game is not null) games.Add(game);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { errors.Add(SnapshotFailures.From(manifest, error)); }
            }
            sources.Add(new(_epicManifestDirectory, errors.Count == 0 ? "available" : "partial", scanned, errors));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            errors.Add(SnapshotFailures.From(_epicManifestDirectory, error));
            sources.Add(new(_epicManifestDirectory, SnapshotFailures.Status(error), scanned, errors));
        }
    }

    private InstalledGame? ReadEpicManifest(string manifest, List<SnapshotError> errors)
    {
        using var document = JsonDocument.Parse(_files.ReadAllText(manifest));
        var item = document.RootElement;
        if (item.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
            return null;
        var root = item.GetProperty("InstallLocation").GetString();
        if (root is null || !Path.IsPathFullyQualified(root))
            throw new FormatException("Epic InstallLocation must be absolute.");
        root = Path.GetFullPath(root);
        if (!_files.DirectoryExists(root))
        {
            errors.Add(new(manifest, "missing_install_directory", root));
            return null;
        }
        var id = item.GetProperty("AppName").GetString() ?? throw new FormatException("Missing Epic AppName.");
        var name = item.GetProperty("DisplayName").GetString() ?? id;
        return new("epic", id, name, root, manifest);
    }

    private static string RequireValue(VdfDocument node, string name) =>
        node.Child(name)?.Value is { Length: > 0 } value ? value
            : throw new FormatException($"Missing {name}.");

    public static bool ContainsExecutable(string installRoot, string executablePath)
    {
        try
        {
            var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(executablePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}

public sealed class InventoryFileSystem : IInventoryFileSystem
{
    public bool DirectoryExists(string path) => Attributes(path) is { } attributes
        && (attributes & FileAttributes.Directory) != 0;
    public bool FileExists(string path) => Attributes(path) is { } attributes
        && (attributes & FileAttributes.Directory) == 0;
    public string ReadAllText(string path) => File.ReadAllText(path);
    public IEnumerable<string> EnumerateFiles(string path, string pattern) => Directory.EnumerateFiles(path, pattern);

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}

internal sealed class VdfDocument
{
    public string? Value { get; private set; }
    public Dictionary<string, VdfDocument> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VdfDocument? Child(string name) => Children.GetValueOrDefault(name);

    public static VdfDocument Parse(string text)
    {
        var tokens = Regex.Matches(text, "\"(?:\\\\.|[^\"\\\\])*\"|//[^\\r\\n]*|[{}]|[^\\s{}\"]+")
            .Select(match => match.Value).Where(token => !token.StartsWith("//")).ToArray();
        int position = 0;
        return Read(tokens, ref position, false, 0);
    }

    private static VdfDocument Read(string[] tokens, ref int position, bool expectClosingBrace, int depth)
    {
        if (depth > 32) throw new FormatException("VDF nesting exceeds 32 levels.");
        var node = new VdfDocument();
        while (position < tokens.Length)
        {
            var key = tokens[position++];
            if (key == "}")
            {
                if (!expectClosingBrace) throw new FormatException("Unexpected VDF closing brace.");
                return node;
            }
            if (key == "{" || position == tokens.Length) throw new FormatException("Invalid VDF key/value pair.");
            var value = tokens[position++];
            if (value == "}") throw new FormatException("Missing VDF value.");
            node.Children[Decode(key)] = value == "{" ? Read(tokens, ref position, true, depth + 1)
                : new VdfDocument { Value = Decode(value) };
        }
        if (expectClosingBrace) throw new FormatException("Unclosed VDF section.");
        return node;
    }

    private static string Decode(string token) => token.StartsWith('"')
        ? token[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : token;
}

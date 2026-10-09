using System.IO.Enumeration;
using System.Text.Json;

namespace PlaycastCompanion.Tests;

public static class GameInventoryContractTests
{
    public static void Run()
    {
        FindsSteamGamesAcrossModernAndLegacyLibraries();
        ExcludesIncompleteAndMissingInstallations();
        ReadsEpicManifestAndRejectsIncompleteInstall();
        PreservesMalformedAndDeniedSourceStatus();
        RejectsEscapedInstallDirectory();
        MatchesOnlyWithinInstallRootBoundary();
        HonorsCancellation();
    }

    private static void FindsSteamGamesAcrossModernAndLegacyLibraries()
    {
        var files = new FakeInventoryFiles();
        files.AddFile(@"C:\Steam\steamapps\libraryfolders.vdf", """
            "libraryfolders" {
                "0" { "path" "C:\\Steam" "apps" { "10" "1024" } }
                "1" "D:\\Library"
                "2" { "path" "E:\\Games" }
            }
            """);
        files.AddSteam(@"C:\Steam", "10", "First Game", "First");
        files.AddSteam(@"D:\Library", "20", "Second Game", "Second");
        files.AddSteam(@"E:\Games", "30", "Third Game", "Third");
        var result = Source(files).Capture(CancellationToken.None);
        Check(result.Games.Count == 3, "Modern and legacy Steam libraries must both be discovered.");
        Check(result.Games.Select(game => game.Id).Order().SequenceEqual(new[] { "10", "20", "30" }), "Steam app IDs must survive parsing.");
        Check(result.Games.Single(game => game.Id == "20").InstallRoot == @"D:\Library\steamapps\common\Second", "Steam game must use its library root.");
    }

    private static void ExcludesIncompleteAndMissingInstallations()
    {
        var files = new FakeInventoryFiles();
        files.AddSteam(@"C:\Steam", "10", "Ready", "Ready");
        files.AddSteam(@"C:\Steam", "11", "Downloading", "Downloading", "2");
        files.AddSteam(@"C:\Steam", "12", "Deleted", "Deleted", createInstall: false);
        var result = Source(files).Capture(CancellationToken.None);
        Check(result.Games.Count == 1 && result.Games[0].Id == "10", "Downloading or deleted game directories are not installed games.");
        Check(result.Sources.SelectMany(source => source.Errors).Any(error => error.Status == "missing_install_directory"), "Missing directories need an explicit observation.");
    }

    private static void ReadsEpicManifestAndRejectsIncompleteInstall()
    {
        var files = new FakeInventoryFiles();
        files.Directories.Add(@"C:\EpicManifests");
        files.Directories.Add(@"D:\Epic\Ready");
        files.AddFile(@"C:\EpicManifests\ready.item", JsonSerializer.Serialize(new
        {
            AppName = "epic-ready", DisplayName = "Epic Ready", InstallLocation = @"D:\Epic\Ready", bIsIncompleteInstall = false
        }));
        files.AddFile(@"C:\EpicManifests\partial.item", JsonSerializer.Serialize(new
        {
            AppName = "epic-partial", DisplayName = "Epic Partial", InstallLocation = @"D:\Epic\Partial", bIsIncompleteInstall = true
        }));
        var result = Source(files).Capture(CancellationToken.None);
        Check(result.Games.Count == 1 && result.Games[0].Store == "epic" && result.Games[0].Id == "epic-ready", "Only complete Epic manifests with a present directory count as installed.");
    }

    private static void PreservesMalformedAndDeniedSourceStatus()
    {
        var files = new FakeInventoryFiles();
        files.AddSteam(@"C:\Steam", "10", "Ready", "Ready");
        files.AddFile(@"C:\Steam\steamapps\appmanifest_11.acf", "\"AppState\" { \"appid\" \"11\"");
        files.Directories.Add(@"C:\EpicManifests");
        files.DeniedDirectories.Add(@"C:\EpicManifests");
        var result = Source(files).Capture(CancellationToken.None);
        Check(result.Games.Count == 1, "A bad manifest must not erase a healthy installation.");
        Check(result.Sources.Any(source => source.Source == @"C:\Steam\steamapps" && source.Status == "partial"), "Malformed manifests must produce partial coverage.");
        Check(result.Sources.Any(source => source.Source == @"C:\EpicManifests" && source.Status == "access_denied"), "Access denial must be distinguishable from an empty library.");
    }

    private static void RejectsEscapedInstallDirectory()
    {
        var files = new FakeInventoryFiles();
        files.AddSteam(@"C:\Steam", "99", "Escaped", @"..\..\Elsewhere");
        var result = Source(files).Capture(CancellationToken.None);
        Check(result.Games.Count == 0, "An install path escaping common must never become a running-game match root.");
        Check(result.Sources.SelectMany(source => source.Errors).Any(), "Rejected install paths must report why coverage failed.");
    }

    private static void MatchesOnlyWithinInstallRootBoundary()
    {
        Check(GameInventorySource.ContainsExecutable(@"C:\Games\Example", @"c:\games\EXAMPLE\bin\game.exe"), "Install-root matching is case insensitive on Windows.");
        Check(!GameInventorySource.ContainsExecutable(@"C:\Games\Example", @"C:\Games\ExampleOther\game.exe"), "A sibling sharing a prefix must not count as the game.");
        Check(!GameInventorySource.ContainsExecutable(@"C:\Games\Example", @"C:\Games\Example\..\Other\game.exe"), "Normalized path traversal must not count as the game.");
        Check(!GameInventorySource.ContainsExecutable(@"C:\Games\Example", @"C:\Games\Example"), "The root directory alone is not a child executable path.");
    }

    private static void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { Source(new FakeInventoryFiles()).Capture(cancellation.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Canceled inventory capture must stop immediately.");
    }

    private static GameInventorySource Source(FakeInventoryFiles files) =>
        new(files, new[] { @"C:\Steam" }, @"C:\EpicManifests");

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private sealed class FakeInventoryFiles : IInventoryFileSystem
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeniedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => Directories.Contains(Path.GetFullPath(path));
        public bool FileExists(string path) => _content.ContainsKey(Path.GetFullPath(path));
        public string ReadAllText(string path) => _content[Path.GetFullPath(path)];

        public IEnumerable<string> EnumerateFiles(string path, string pattern)
        {
            if (DeniedDirectories.Contains(path)) throw new UnauthorizedAccessException("Synthetic denied source.");
            return _content.Keys.Where(file => string.Equals(Path.GetDirectoryName(file), path, StringComparison.OrdinalIgnoreCase)
                && FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), true));
        }

        public void AddFile(string path, string text)
        {
            path = Path.GetFullPath(path);
            _content[path] = text;
            Directories.Add(Path.GetDirectoryName(path)!);
        }

        public void AddSteam(string library, string id, string name, string installDir,
            string flags = "4", bool createInstall = true)
        {
            AddFile(Path.Combine(library, "steamapps", $"appmanifest_{id}.acf"), $$"""
                "AppState" {
                    "appid" "{{id}}" "name" "{{name}}" "installdir" "{{installDir.Replace("\\", "\\\\")}}" "StateFlags" "{{flags}}"
                }
                """);
            if (createInstall) Directories.Add(Path.GetFullPath(Path.Combine(library, "steamapps", "common", installDir)));
        }
    }
}

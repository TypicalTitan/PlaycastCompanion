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
        UsesCurrentGuestProfileAndExcludesOwnerLibraries();
        ReadsGuestLegendaryMetadata();
        DeniedGuestLibraryDoesNotFallBackToOwner();
        ExpandsLauncherPathsUsingGuestVariables();
        ReadsCanonicalLegendaryAndDeduplicatesWindowsPaths();
        PreservesDeniedLegendarySourceAndMidScanCancellation();
        FiltersCommonEpicMetadataEvenWhenRegisteredByGuest();
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

    private static void UsesCurrentGuestProfileAndExcludesOwnerLibraries()
    {
        const string profile = @"C:\Users\NonsoleMode.000";
        var locations = TargetGameLocations.ForProfile(profile);
        Check(locations.SteamRoots.SequenceEqual(new[] { profile + @"\AppData\Local\Playcast\Steam" }),
            "Steam discovery must use the resolved current profile, including a Windows profile suffix.");
        var files = new FakeInventoryFiles();
        files.AddSteam(locations.SteamRoots[0], "guest", "Guest Game", "Guest");
        files.AddSteam(@"C:\Program Files (x86)\Steam", "owner", "Owner Game", "Owner");
        var manifests = @"C:\SharedEpicMetadata";
        files.AddFile(manifests + @"\owner.item", JsonSerializer.Serialize(new
            { AppName = "owner", DisplayName = "Owner Epic", InstallLocation = @"C:\OwnerGames\Epic" }));
        files.AddFile(manifests + @"\guest.item", JsonSerializer.Serialize(new
            { AppName = "guest-epic", DisplayName = "Guest Epic", InstallLocation = profile + @"\Games\Epic" }));
        files.Directories.Add(@"C:\OwnerGames\Epic");
        files.Directories.Add(profile + @"\Games\Epic");
        var result = new GameInventorySource(files, locations with { EpicManifests = manifests }).Capture(CancellationToken.None);
        Check(result.Games.Count == 2 && result.Games.All(game => game.Id != "owner"),
            "Owner Steam and unrelated machine-wide Epic installs must not become guest installations.");
        var registered = TargetGameLocations.ForProfile(profile, @"D:\GuestSteam", manifests);
        Check(registered.SteamRoots.Contains(@"D:\GuestSteam") && !registered.FilterSharedEpic,
            "Launcher paths explicitly registered by the guest must remain usable outside its profile.");
    }

    private static void ReadsGuestLegendaryMetadata()
    {
        var files = new FakeInventoryFiles();
        var locations = TargetGameLocations.ForProfile(@"C:\Users\NonsoleMode.000");
        files.AddFile(locations.LegendaryInstalled, JsonSerializer.Serialize(new
        {
            epicGuest = new { app_name = "epic-guest", title = "Guest Legendary Game", install_path = @"D:\Playcast\Epic\Guest" },
            missing = new { app_name = "missing", title = "Missing", install_path = @"D:\Playcast\Epic\Deleted" }
        }));
        files.Directories.Add(@"D:\Playcast\Epic\Guest");
        var result = new GameInventorySource(files, locations).Capture(CancellationToken.None);
        Check(result.Games.Count == 1 && result.Games[0].ManifestPath == locations.LegendaryInstalled
            && result.Games[0].InstallRoot == @"D:\Playcast\Epic\Guest",
            "Legendary inventory must use the guest's installed.json and its recorded installation location.");
        Check(result.Sources.Any(source => source.Source == locations.LegendaryInstalled && source.Status == "partial"),
            "Missing Legendary installation directories must preserve partial coverage.");
    }

    private static void DeniedGuestLibraryDoesNotFallBackToOwner()
    {
        var files = new FakeInventoryFiles();
        var locations = TargetGameLocations.ForProfile(@"C:\Users\NonsoleMode.000");
        files.AddSteam(locations.SteamRoots[0], "guest", "Guest", "Guest");
        files.AddSteam(@"C:\Program Files (x86)\Steam", "owner", "Owner", "Owner");
        files.DeniedDirectories.Add(locations.SteamRoots[0] + @"\steamapps");
        var result = new GameInventorySource(files, locations).Capture(CancellationToken.None);
        Check(result.Games.Count == 0 && result.Sources.Any(source => source.Status == "access_denied"),
            "A protected guest library must report unavailable data without substituting an owner inventory.");
    }

    private static GameInventorySource Source(FakeInventoryFiles files) =>
        new(files, new[] { @"C:\Steam" }, @"C:\EpicManifests");

    private static void ExpandsLauncherPathsUsingGuestVariables()
    {
        const string profile = @"C:\Users\NonsoleMode.009";
        var paths = TargetGameLocations.ForProfile(profile, @"%LOCALAPPDATA%\GuestSteam", @"%APPDATA%\EpicMetadata");
        Check(paths.SteamRoots.Contains(profile + @"\AppData\Local\GuestSteam")
            && paths.EpicManifests == profile + @"\AppData\Roaming\EpicMetadata",
            "Guest launcher variables expanded to another account's profile.");
        var unknown = TargetGameLocations.ForProfile(profile, @"%TEMP%\Steam", @"%UNKNOWN_GUEST_PATH%\Epic");
        Check(unknown.SteamRoots.Count == 1 && unknown.FilterSharedEpic
            && unknown.Sources.SelectMany(source => source.Errors).Count(error => error.Status == "unavailable") == 2,
            "Unresolved launcher variables were accepted or silently replaced by owner paths.");
        var system = TargetGameLocations.ForProfile(@"%SystemDrive%\Users\NonsoleMode.009");
        Check(system.SteamRoots[0].StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase),
            "SystemDrive profile expansion failed.");
    }

    private static void ReadsCanonicalLegendaryAndDeduplicatesWindowsPaths()
    {
        var files = new FakeInventoryFiles();
        var locations = TargetGameLocations.ForProfile(@"C:\Users\NonsoleMode.009");
        var canonical = locations.LegendaryCandidates.Single(path => path.EndsWith(@"Playcast\LegendaryConfig\installed.json"));
        files.AddFile(locations.LegendaryInstalled, JsonSerializer.Serialize(new
            { guest = new { app_name = "epic-guest", title = "Guest", install_path = @"D:\Playcast\Epic\Guest" } }));
        files.AddFile(canonical, JsonSerializer.Serialize(new
            { guest = new { app_name = "epic-guest", title = "Guest canonical", install_path = @"d:\playcast\EPIC\guest" } }));
        files.Directories.Add(@"D:\Playcast\Epic\Guest");
        var result = new GameInventorySource(files, locations with
        {
            LegendaryInstalled = "", // Explicit guest registry path unavailable after hive unload.
            Sources = [new("target_registry_hive", "not_loaded", 0, [])]
        }).Capture(CancellationToken.None);
        Check(result.Games.Count == 1 && result.Sources.Any(source => source.Source == canonical && source.Status == "available"),
            "Canonical Playcast Legendary metadata was missed or casing duplicated one installation.");
        Check(result.Sources.Any(source => source.Status == "not_loaded"), "Unavailable guest registry coverage was lost.");
    }

    private static void PreservesDeniedLegendarySourceAndMidScanCancellation()
    {
        var files = new FakeInventoryFiles();
        var locations = TargetGameLocations.ForProfile(@"C:\Users\NonsoleMode.009");
        files.AddFile(locations.LegendaryInstalled, "{}");
        files.DeniedFiles.Add(locations.LegendaryInstalled);
        var result = new GameInventorySource(files, locations).Capture(CancellationToken.None);
        Check(result.Sources.Any(source => source.Source == locations.LegendaryInstalled && source.Status == "access_denied"),
            "Denied guest Legendary metadata was reported as an empty inventory.");
        using var stop = new CancellationTokenSource();
        var cancelling = new FakeInventoryFiles();
        cancelling.AddSteam(@"C:\Steam", "1", "One", "One");
        cancelling.OnRead = stop.Cancel;
        try { Source(cancelling).Capture(stop.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancellation during the final manifest was swallowed.");
    }

    private static void FiltersCommonEpicMetadataEvenWhenRegisteredByGuest()
    {
        const string profile = @"C:\Users\NonsoleMode.009";
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var locations = TargetGameLocations.ForProfile(profile, registeredEpic: shared.ToLowerInvariant() + @"\..\Manifests");
        Check(locations.FilterSharedEpic, "Registering machine-wide Epic metadata disabled account ownership filtering.");
        var files = new FakeInventoryFiles();
        files.AddFile(Path.Combine(shared, "owner.item"), JsonSerializer.Serialize(new
            { AppName = "owner", DisplayName = "Owner", InstallLocation = @"D:\OwnerGames\Unrelated" }));
        files.AddFile(Path.Combine(shared, "guest.item"), JsonSerializer.Serialize(new
            { AppName = "guest", DisplayName = "Guest", InstallLocation = profile + @"\Games\One" }));
        files.Directories.Add(@"D:\OwnerGames\Unrelated");
        files.Directories.Add(profile + @"\Games\One");
        var result = new GameInventorySource(files, locations).Capture(CancellationToken.None);
        Check(result.Games.Count == 1 && result.Games[0].Id == "guest", "Owner games leaked from common Epic manifests.");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private sealed class FakeInventoryFiles : IInventoryFileSystem
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeniedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeniedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action? OnRead;
        private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => Directories.Contains(Path.GetFullPath(path));
        public bool FileExists(string path) => _content.ContainsKey(Path.GetFullPath(path));
        public string ReadAllText(string path)
        {
            OnRead?.Invoke();
            if (DeniedFiles.Contains(path)) throw new UnauthorizedAccessException("Synthetic denied manifest.");
            return _content[Path.GetFullPath(path)];
        }

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

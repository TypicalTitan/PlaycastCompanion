using Microsoft.Win32;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace PlaycastCompanion;

public sealed record GameLocations(IReadOnlyList<string> SteamRoots, string EpicManifests,
    string LegendaryInstalled, IReadOnlyList<string> EpicInstallRoots, bool FilterSharedEpic,
    IReadOnlyList<SourceCoverage> Sources)
{
    public IReadOnlyList<string> LegendaryCandidates { get; init; } = [];
}

/// <summary>Resolves launcher metadata for the configured guest, without reading the owner's launchers.</summary>
public static class TargetGameLocations
{
    public static GameLocations ForProfile(string profile, string? registeredSteam = null,
        string? registeredEpic = null)
    {
        var errors = new List<SnapshotError>();
        profile = ExpandPath(profile, "", errors, "target_profile");
        registeredSteam = ExpandPath(registeredSteam, profile, errors, "target_steam_path");
        registeredEpic = ExpandPath(registeredEpic, profile, errors, "target_epic_path");
        var validProfile = Path.IsPathFullyQualified(profile);
        var steam = new List<string>();
        if (validProfile) steam.Add(Path.Combine(profile, "AppData", "Local", "Playcast", "Steam"));
        if (registeredSteam is not null && Path.IsPathFullyQualified(registeredSteam)) steam.Add(registeredSteam);
        var epicRegistered = registeredEpic is not null && Path.IsPathFullyQualified(registeredEpic);
        var sharedEpic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var filterEpic = !epicRegistered || string.Equals(InventoryPath.Resolve(registeredEpic!).TrimEnd('\\', '/'),
            InventoryPath.Resolve(sharedEpic).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var legendary = validProfile ? Path.Combine(profile, ".config", "legendary", "installed.json") : "";
        var canonicalLegendary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Playcast", "LegendaryConfig", "installed.json");
        return new(steam, epicRegistered ? registeredEpic! : sharedEpic,
            legendary,
            validProfile ? [profile] : [], filterEpic,
            errors.Count == 0 ? [] : [new("target_launcher_paths", "partial", 0, errors)])
        {
            // GuestModeManagerEnter explicitly configures this Playcast-owned shared directory.
            LegendaryCandidates = legendary.Length == 0 ? [canonicalLegendary] : [legendary, canonicalLegendary]
        };
    }

    public static GameLocations Discover(string targetUsername)
    {
        var errors = new List<SnapshotError>();
        string? sid = null;
        try
        {
            sid = new NTAccount(Environment.MachineName, targetUsername)
                .Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (Exception error) { errors.Add(SnapshotFailures.From("target_account", error)); }
        var profile = sid is null ? "" : ExpandPath(Read(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid, "ProfileImagePath", errors),
            "", errors, "target_profile");
        var hiveAvailable = sid is not null && HasGuestHive(sid, errors);
        var locations = ForProfile(profile,
            !hiveAvailable ? null : Read(RegistryHive.Users, sid + @"\Software\Valve\Steam", "SteamPath", errors),
            !hiveAvailable ? null : Read(RegistryHive.Users, sid + @"\Software\Epic Games\EOS", "ModSdkMetadataDir", errors));
        errors.AddRange(locations.Sources.SelectMany(source => source.Errors));
        var epicRoots = locations.EpicInstallRoots.ToList();
        var drive = Read(RegistryHive.LocalMachine, @"SOFTWARE\Playcast\GameStorage", "DriveLetter", errors);
        if (drive.Length >= 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':')
            epicRoots.Add(drive[..2] + @"\Playcast\Epic");
        var installed = hiveAvailable ? locations.LegendaryInstalled : "";
        if (hiveAvailable)
        {
            var config = Read(RegistryHive.Users, sid + @"\Environment", "LEGENDARY_CONFIG_PATH", errors);
            if (config.Length == 0)
            {
                var xdg = Read(RegistryHive.Users, sid + @"\Environment", "XDG_CONFIG_HOME", errors);
                if (xdg.Length > 0)
                {
                    xdg = ExpandPath(xdg, profile, errors, "target_xdg_config_home");
                    config = xdg.Length > 0 ? Path.Combine(xdg, "legendary") : "";
                    if (xdg.Length == 0) installed = "";
                }
            }
            if (config.Length > 0)
            {
                config = ExpandPath(config, profile, errors, "target_legendary_config_path");
                installed = config.Length > 0 ? Path.Combine(config, "installed.json") : "";
            }
        }
        if (profile.Length == 0) errors.Add(new("target_profile", "unavailable",
            "Could not resolve the target account's current SID/profile; owner launcher libraries are not substituted."));
        var legendaryCandidates = (installed.Length == 0 ? locations.LegendaryCandidates
            : new[] { installed }.Concat(locations.LegendaryCandidates))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        return locations with { EpicInstallRoots = epicRoots, LegendaryInstalled = installed, LegendaryCandidates = legendaryCandidates,
            Sources = [new(profile.Length == 0 ? "target_profile" : profile,
                errors.Count == 0 && profile.Length > 0 ? "available" : "partial", profile.Length == 0 ? 0 : 1, errors)] };
    }

    private static string Read(RegistryHive hive, string path, string name, List<SnapshotError> errors)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value ? value : "";
        }
        catch (Exception error) { errors.Add(SnapshotFailures.From($"{hive}\\{path}\\{name}", error)); return ""; }
    }

    private static bool HasGuestHive(string sid, List<SnapshotError> errors)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var hive = root.OpenSubKey(sid, false);
            if (hive is not null) return true;
            errors.Add(new("target_registry_hive", "not_loaded",
                "Guest registry hive is not loaded; launcher registrations and Legendary environment are unknown."));
        }
        catch (Exception error) { errors.Add(SnapshotFailures.From("target_registry_hive", error)); }
        return false;
    }

    private static string ExpandPath(string? raw, string profile, List<SnapshotError> errors, string source)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemDrive"] = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "",
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        if (Path.IsPathFullyQualified(profile))
        {
            variables["USERPROFILE"] = profile;
            variables["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
            variables["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
        }
        var expanded = Regex.Replace(raw, "%([^%]+)%", match => variables.GetValueOrDefault(match.Groups[1].Value, match.Value));
        if (Regex.IsMatch(expanded, "%[^%]+%") || !Path.IsPathFullyQualified(expanded))
        {
            errors.Add(new(source, "unavailable", "Launcher path has unresolved guest variables or is not absolute: " + raw));
            return "";
        }
        try { return Path.GetFullPath(expanded); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { errors.Add(new(source, "unavailable", error.Message)); return ""; }
    }
}

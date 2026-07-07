using System.Text.Json.Serialization;

namespace UCHModLoader.Core.Models;

public enum GamePlatform
{
    Windows,
    LinuxProton,   // Windows build of the game running under Proton (e.g. Steam Deck)
    LinuxNative,
    MacOS,
}

public sealed record GameInstall(string GameDirectory)
{
    public GamePlatform Platform { get; init; } = GamePlatform.Windows;

    /// <summary>Name of the game's executable/bundle, needed by the unix BepInEx run script.</summary>
    public string? ExecutableName { get; init; }

    public string PluginsDirectory => Path.Combine(GameDirectory, "BepInEx", "plugins");
    public string BepInExConfigPath => Path.Combine(GameDirectory, "BepInEx", "config", "BepInEx.cfg");

    /// <summary>Windows-style injection (winhttp.dll) — true for Windows and Proton.</summary>
    public bool UsesWindowsBepInEx =>
        Platform is GamePlatform.Windows or GamePlatform.LinuxProton;
}

public sealed class ModIndex
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("mods")] public List<ModEntry> Mods { get; set; } = new();

    public ModEntry? Find(string id) =>
        Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class ModEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("iconVersion")] public string? IconVersion { get; set; }
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; set; }
    [JsonPropertyName("authorVerified")] public bool AuthorVerified { get; set; } = true;
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("upvotes")] public int Upvotes { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("versions")] public List<ModVersionInfo> Versions { get; set; } = new();

    public ModVersionInfo? Latest() =>
        Versions.OrderByDescending(v => v.Revision).ThenByDescending(v => Version.Parse(v.Version))
                .FirstOrDefault();
}

public sealed class ModVersionInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("revision")] public int Revision { get; set; }
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("approved")] public bool Approved { get; set; } = true;
    [JsonPropertyName("changelog")] public string Changelog { get; set; } = "";
    [JsonPropertyName("uploadedUtc")] public DateTime UploadedUtc { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "*";
}

public sealed class InstalledMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public int Revision { get; set; }
    public bool Enabled { get; set; } = true;
    public bool InstalledAsDependency { get; set; }
    public List<string> RelativeFiles { get; set; } = new();
    public Dictionary<string, string> Dependencies { get; set; } = new();
}

public enum InstallActionKind { Install, Upgrade }

public sealed record InstallAction(
    InstallActionKind Kind,
    ModEntry Mod,
    ModVersionInfo TargetVersion,
    string? CurrentVersion,
    bool IsDependency);

public sealed record InstallPlan(IReadOnlyList<InstallAction> Actions)
{
    public bool IsEmpty => Actions.Count == 0;
    public IEnumerable<InstallAction> DependencyActions => Actions.Where(a => a.IsDependency);
}

public sealed class ModPack
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("modIds")] public List<string> ModIds { get; set; } = new();
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("iconVersion")] public string? IconVersion { get; set; }
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; set; }
    [JsonPropertyName("authorVerified")] public bool AuthorVerified { get; set; } = true;
}

/// <summary>
/// Result of verifying installed-mod state against the actual game folder.
/// Lists contain mod ids. Missing = no recorded files present; Partial = some
/// but not all present.
/// </summary>
public sealed record InstallVerificationResult(
    IReadOnlyList<string> MissingMods,
    IReadOnlyList<string> PartialMods)
{
    public int BrokenCount => MissingMods.Count + PartialMods.Count;
}

/// <summary>Shared update-detection rule: revision when available, version fallback for legacy entries.</summary>
public static class UpdateRules
{
    public static bool IsNewer(ModVersionInfo target, string installedVersion, int installedRevision)
    {
        if (target.Revision > 0) return target.Revision > installedRevision;
        return Version.TryParse(target.Version, out var tv) &&
               Version.TryParse(installedVersion, out var iv) && tv > iv;
    }
}
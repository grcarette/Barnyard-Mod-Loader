using System.Text.Json;

namespace UCHModLoader.App.Services;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public List<string> EnabledModIds { get; set; } = new();
}

/// <summary>
/// Local profile storage at %AppData%/UCHModLoader/profiles.json.
/// A profile is a named set of mods that should be enabled.
/// </summary>
public sealed class ProfileService
{
    public const string DefaultProfileName = "Default";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public List<Profile> Profiles { get; private set; } = new();
    public string ActiveProfileName { get; set; } = DefaultProfileName;

    public ProfileService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UCHModLoader", "profiles.json");
        Load();
    }

    public Profile Active =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
        ?? Profiles[0];

    public bool NameExists(string name) =>
        Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var stored = JsonSerializer.Deserialize<StoredProfiles>(File.ReadAllText(_path));
                if (stored is not null && stored.Profiles.Count > 0)
                {
                    Profiles = stored.Profiles;
                    ActiveProfileName = stored.ActiveProfileName;
                    if (!NameExists(ActiveProfileName))
                        ActiveProfileName = Profiles[0].Name;
                    return;
                }
            }
        }
        catch { }

        Profiles = new List<Profile> { new() { Name = DefaultProfileName } };
        ActiveProfileName = DefaultProfileName;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(
            new StoredProfiles { Profiles = Profiles, ActiveProfileName = ActiveProfileName },
            JsonOptions));
    }

    private sealed class StoredProfiles
    {
        public List<Profile> Profiles { get; set; } = new();
        public string ActiveProfileName { get; set; } = DefaultProfileName;
    }
}
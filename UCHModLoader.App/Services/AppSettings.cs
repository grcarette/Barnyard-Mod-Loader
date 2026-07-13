using System.Text.Json;

namespace UCHModLoader.App.Services;

public sealed class AppSettings
{
    public const string DefaultServerUrl =
        "https://barnyard-mod-loader-production.up.railway.app";

    public string ServerUrl { get; set; } = DefaultServerUrl;
    public string ApiToken { get; set; } = "";
    public bool IsMaximized { get; set; }
    public bool SetupCompleted { get; set; }
    public bool ShowBepInExConsole { get; set; }
    public bool DarkMode { get; set; } = true;
    public bool AutoUpdateMods { get; set; }
    public string GamePathOverride { get; set; } = "";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UCHModLoader", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                       ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
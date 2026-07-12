using System.IO.Compression;
using System.Net.Http;
using UCHModLoader.Core.Models;

namespace UCHModLoader.Core.Services;

public sealed class BepInExManager : IBepInExManager
{
    // BepInEx 5.x (Mono) — the correct line for Ultimate Chicken Horse.
    // Pinned so installs are reproducible; bump deliberately when upgrading.
    private const string WindowsUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    private const string LinuxUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_linux_x64_5.4.23.5.zip";
    private const string MacUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_macos_universal_5.4.23.5.zip";

    private readonly HttpClient _http;

    public BepInExManager(HttpClient http) => _http = http;

    public string GetDefaultDownloadUrl(GameInstall game) => game.Platform switch
    {
        GamePlatform.LinuxNative => LinuxUrl,
        GamePlatform.MacOS => MacUrl,
        _ => WindowsUrl, // Windows and Proton both use the Windows package
    };

    /// <summary>
    /// The Steam launch options the user must set for BepInEx to load, or
    /// null when none are needed (plain Windows).
    /// </summary>
    public string? GetRequiredLaunchOptions(GameInstall game) => game.Platform switch
    {
        GamePlatform.LinuxProton => "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%",
        GamePlatform.LinuxNative or GamePlatform.MacOS => "./run_bepinex.sh %command%",
        _ => null,
    };

    public bool IsInstalled(GameInstall game)
    {
        var hasCore = Directory.Exists(Path.Combine(game.GameDirectory, "BepInEx", "core"));
        if (!hasCore) return false;

        return game.UsesWindowsBepInEx
            ? File.Exists(Path.Combine(game.GameDirectory, "winhttp.dll"))
            : File.Exists(Path.Combine(game.GameDirectory, "run_bepinex.sh"));
    }

    public async Task InstallAsync(GameInstall game, string bepInExZipUrl, CancellationToken ct = default)
    {
        var bytes = await _http.GetByteArrayAsync(bepInExZipUrl, ct);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(game.GameDirectory, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(game.GameDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"BepInEx zip entry '{entry.FullName}' escapes the game folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        if (!game.UsesWindowsBepInEx)
            ConfigureUnixRunScript(game);

        // Mods install into BepInEx/plugins; make sure it exists immediately
        // rather than waiting for the game's first modded launch.
        Directory.CreateDirectory(game.PluginsDirectory);
    }

    /// <summary>
    /// The unix packages ship run_bepinex.sh with an empty executable_name;
    /// fill it in and make the script executable.
    /// </summary>
    private static void ConfigureUnixRunScript(GameInstall game)
    {
        var scriptPath = Path.Combine(game.GameDirectory, "run_bepinex.sh");
        if (!File.Exists(scriptPath)) return;

        if (!string.IsNullOrEmpty(game.ExecutableName))
        {
            var lines = File.ReadAllLines(scriptPath);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("executable_name="))
                {
                    lines[i] = $"executable_name=\"{game.ExecutableName}\"";
                    break;
                }
            }
            File.WriteAllLines(scriptPath, lines);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public void SetConsoleEnabled(GameInstall game, bool enabled)
    {
        var path = game.BepInExConfigPath;
        var value = enabled ? "true" : "false";
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        var sectionIndex = lines.FindIndex(l => l.Trim() == "[Logging.Console]");
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[Logging.Console]");
            lines.Add($"Enabled = {value}");
        }
        else
        {
            var enabledIndex = -1;
            for (var i = sectionIndex + 1; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("[")) break;
                if (trimmed.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.Contains('='))
                {
                    enabledIndex = i;
                    break;
                }
            }

            if (enabledIndex >= 0) lines[enabledIndex] = $"Enabled = {value}";
            else lines.Insert(sectionIndex + 1, $"Enabled = {value}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
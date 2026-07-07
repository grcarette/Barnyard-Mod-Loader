using Microsoft.Win32;
using UCHModLoader.Core.Models;

namespace UCHModLoader.Core.Services;

public sealed class SteamGameLocator : IGameLocator
{
    private const string GameFolderName = "Ultimate Chicken Horse";

    public GameInstall? FindGame()
    {
        foreach (var steamRoot in CandidateSteamRoots())
        {
            var gameDir = FindInSteamRoot(steamRoot);
            if (gameDir is not null)
                return DescribeInstall(gameDir);
        }
        return null;
    }

    private static IEnumerable<string> CandidateSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var registryPath = RegistrySteamPath();
            if (registryPath is not null) yield return registryPath;
            yield return @"C:\Program Files (x86)\Steam";
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam");
            yield break;
        }

        // Linux: classic, XDG, and Flatpak Steam locations.
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam",
            ".local", "share", "Steam");
    }

    private static string? RegistrySteamPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInSteamRoot(string steamRoot)
    {
        if (!Directory.Exists(steamRoot)) return null;

        foreach (var library in LibraryFolders(steamRoot))
        {
            var gameDir = Path.Combine(library, "steamapps", "common", GameFolderName);
            if (Directory.Exists(gameDir)) return gameDir;
        }
        return null;
    }

    private static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        yield return steamRoot;

        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        // Minimal vdf scrape: "path" lines contain the library roots.
        foreach (var line in File.ReadLines(vdfPath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var path = parts[^1].Replace(@"\\", @"\");
                if (Directory.Exists(path)) yield return path;
            }
        }
    }

    /// <summary>Also used for manually-chosen game folders (Settings override).</summary>
    public static GameInstall DescribeInstall(string gameDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return new GameInstall(gameDir)
            {
                Platform = GamePlatform.Windows,
                ExecutableName = FirstFileByPattern(gameDir, "*.exe"),
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            var app = Directory.EnumerateDirectories(gameDir, "*.app").FirstOrDefault();
            return new GameInstall(gameDir)
            {
                Platform = GamePlatform.MacOS,
                ExecutableName = app is null ? null : Path.GetFileName(app),
            };
        }

        // Linux: a Windows .exe in the folder means Steam installed the Windows
        // depot (Proton — e.g. Steam Deck); a *.x86_64 binary means native.
        var exe = FirstFileByPattern(gameDir, "*.exe");
        if (exe is not null)
        {
            return new GameInstall(gameDir)
            {
                Platform = GamePlatform.LinuxProton,
                ExecutableName = exe,
            };
        }

        var native = FirstFileByPattern(gameDir, "*.x86_64");
        return new GameInstall(gameDir)
        {
            Platform = GamePlatform.LinuxNative,
            ExecutableName = native,
        };
    }

    private static string? FirstFileByPattern(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern)
                .Select(Path.GetFileName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
using System.IO.Compression;
using System.Text.Json;
using UCHModLoader.Core.Models;

namespace UCHModLoader.Core.Services;

public sealed class InstallManager : IInstallManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _stateFilePath;
    private List<InstalledMod> _installed;

    public InstallManager(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UCHModLoader", "installed.json");
        _installed = LoadState();
    }

    public IReadOnlyList<InstalledMod> GetInstalled() => _installed.AsReadOnly();

    public InstallVerificationResult VerifyInstalls(GameInstall game, bool removeBroken = true)
    {
        var missing = new List<string>();
        var partial = new List<string>();

        foreach (var mod in _installed)
        {
            if (mod.RelativeFiles.Count == 0) continue;

            var present = 0;
            foreach (var relative in mod.RelativeFiles)
            {
                var path = Path.Combine(game.GameDirectory, relative);
                // A disabled mod's dll lives at <path>.disabled — still installed.
                if (File.Exists(path) || File.Exists(path + ".disabled")) present++;
            }

            if (present == 0) missing.Add(mod.Id);
            else if (present < mod.RelativeFiles.Count) partial.Add(mod.Id);
        }

        if (removeBroken && (missing.Count > 0 || partial.Count > 0))
        {
            var broken = new HashSet<string>(missing.Concat(partial), StringComparer.OrdinalIgnoreCase);
            _installed.RemoveAll(m => broken.Contains(m.Id));
            SaveState();
        }

        return new InstallVerificationResult(missing, partial);
    }

    /// <summary>
    /// Syncs each mod's Enabled flag from file reality (dll vs dll.disabled),
    /// so enable/disable changes made outside the loader — e.g. by the
    /// in-game Barnyard config manager — are respected instead of overwritten.
    /// </summary>
    public void ReconcileEnabledFromDisk(GameInstall game)
    {
        var changed = false;
        foreach (var mod in _installed)
        {
            bool? enabledOnDisk = null;
            foreach (var relative in mod.RelativeFiles.Where(f =>
                f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)))
            {
                var basePath = Path.Combine(game.GameDirectory,
                    relative.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                        ? relative[..^".disabled".Length]
                        : relative);
                if (File.Exists(basePath)) { enabledOnDisk = true; break; }
                if (File.Exists(basePath + ".disabled")) enabledOnDisk ??= false;
            }

            if (enabledOnDisk is bool state && mod.Enabled != state)
            {
                mod.Enabled = state;
                changed = true;
            }
        }
        if (changed) SaveState();
    }

    public IReadOnlyList<string> GetDependents(string modId) =>
        _installed
            .Where(m => m.Dependencies.Keys.Contains(modId, StringComparer.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .ToList();

    public InstallPlan PlanInstall(string modId, ModIndex index, int? targetRevision = null)
    {
        var actions = new List<InstallAction>();
        Resolve(modId, index, isDependency: false, actions,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), targetRevision);
        var ordered = actions.OrderByDescending(a => a.IsDependency).ToList();
        return new InstallPlan(ordered);
    }

    private void Resolve(string modId, ModIndex index, bool isDependency,
        List<InstallAction> actions, HashSet<string> visiting, int? targetRevision = null)
    {
        if (!visiting.Add(modId))
            throw new InvalidOperationException($"Circular dependency detected involving '{modId}'.");
        if (actions.Any(a => string.Equals(a.Mod.Id, modId, StringComparison.OrdinalIgnoreCase)))
        {
            visiting.Remove(modId);
            return;
        }

        var entry = index.Find(modId)
            ?? throw new KeyNotFoundException($"Mod '{modId}' was not found in the index.");
        var target = targetRevision is int rev
            ? entry.Versions.FirstOrDefault(v => v.Revision == rev)
              ?? throw new KeyNotFoundException($"Revision {rev} of '{modId}' is not available.")
            : entry.Latest()
              ?? throw new InvalidDataException($"Mod '{modId}' has no versions in the index.");

        var existing = _installed.FirstOrDefault(m =>
            string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));

        // A specific revision request reinstalls whenever it differs from what's
        // installed (rollback included); the default path only moves forward.
        var needsAction = targetRevision is not null
            ? existing is null || existing.Revision != target.Revision
            : existing is null || UpdateRules.IsNewer(target, existing.Version, existing.Revision);

        if (needsAction)
        {
            actions.Add(new InstallAction(
                existing is null ? InstallActionKind.Install : InstallActionKind.Upgrade,
                entry, target, existing?.Version, isDependency));
        }

        foreach (var (depId, constraint) in target.Dependencies)
        {
            var depInstalled = _installed.FirstOrDefault(m =>
                string.Equals(m.Id, depId, StringComparison.OrdinalIgnoreCase));
            var depPlanned = actions.Any(a =>
                string.Equals(a.Mod.Id, depId, StringComparison.OrdinalIgnoreCase));

            var satisfied = depPlanned ||
                (depInstalled is not null && VersionRange.Satisfies(depInstalled.Version, constraint));

            if (!satisfied)
                Resolve(depId, index, isDependency: true, actions, visiting);
        }

        visiting.Remove(modId);
    }

    public async Task ExecuteAsync(InstallPlan plan, IModRepository repository, GameInstall game,
        CancellationToken ct = default)
    {
        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();

            if (action.Kind == InstallActionKind.Upgrade)
                await UninstallFilesOnlyAsync(action.Mod.Id, game);

            await using var zipStream = await repository.DownloadAsync(action.TargetVersion, ct);

            var buffered = new MemoryStream();
            await zipStream.CopyToAsync(buffered, ct);
            buffered.Position = 0;

            if (!string.IsNullOrEmpty(action.TargetVersion.Sha256))
            {
                var actual = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(buffered.ToArray())).ToLowerInvariant();
                if (!string.Equals(actual, action.TargetVersion.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Checksum mismatch for '{action.Mod.Id}' — download may be corrupted or tampered with. Aborting.");
                buffered.Position = 0;
            }

            var folderName = ModNaming.ToFolderName(action.Mod.Name, action.Mod.Id);
            var installedFiles = ExtractModZip(buffered, folderName, game);

            _installed.RemoveAll(m => string.Equals(m.Id, action.Mod.Id, StringComparison.OrdinalIgnoreCase));
            _installed.Add(new InstalledMod
            {
                Id = action.Mod.Id,
                Name = action.Mod.Name,
                FolderName = folderName,
                Version = action.TargetVersion.Version,
                Revision = action.TargetVersion.Revision,
                Enabled = true,
                InstalledAsDependency = action.IsDependency,
                RelativeFiles = installedFiles,
                Dependencies = new Dictionary<string, string>(action.TargetVersion.Dependencies),
                Conflicts = new List<string>(action.Mod.Conflicts),
            });
            SaveState();
        }
    }

    public async Task<InstalledMod> InstallLocalAsync(string sourcePath, string displayName,
        string description, GameInstall game)
    {
        PluginInfo? plugin;
        var isZip = sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (isZip)
        {
            plugin = null;
            using var probe = ZipFile.OpenRead(sourcePath);
            foreach (var entry in probe.Entries.Where(e =>
                e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                using var ms = new MemoryStream();
                await entry.Open().CopyToAsync(ms);
                ms.Position = 0;
                plugin = DllInspector.Inspect(ms);
                if (plugin is not null) break;
            }
        }
        else
        {
            await using var fs = File.OpenRead(sourcePath);
            plugin = DllInspector.Inspect(fs);
        }

        if (plugin is null)
            throw new InvalidDataException("No [BepInPlugin] attribute found — is this a BepInEx plugin?");

        var name = string.IsNullOrWhiteSpace(displayName) ? plugin.Name : displayName.Trim();
        var folderName = ModNaming.ToFolderName(name, plugin.Guid);

        // Replace any existing install of the same plugin (local or server).
        await UninstallFilesOnlyAsync(plugin.Guid, game);

        List<string> installedFiles;
        if (isZip)
        {
            await using var zipStream = File.OpenRead(sourcePath);
            installedFiles = ExtractModZip(zipStream, folderName, game);
        }
        else
        {
            var modRoot = Path.Combine(game.PluginsDirectory, folderName);
            Directory.CreateDirectory(modRoot);
            var destination = Path.Combine(modRoot, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destination, overwrite: true);
            installedFiles = new List<string>
            {
                Path.GetRelativePath(game.GameDirectory, destination),
            };
        }

        var mod = new InstalledMod
        {
            Id = plugin.Guid,
            Name = name,
            FolderName = folderName,
            Version = plugin.Version,
            Enabled = true,
            IsLocal = true,
            Description = description,
            RelativeFiles = installedFiles,
        };
        _installed.RemoveAll(m => string.Equals(m.Id, plugin.Guid, StringComparison.OrdinalIgnoreCase));
        _installed.Add(mod);
        SaveState();
        return mod;
    }

    private static List<string> ExtractModZip(Stream zipStream, string folderName, GameInstall game)
    {
        var modRoot = Path.Combine(game.PluginsDirectory, folderName);
        Directory.CreateDirectory(modRoot);

        var files = new List<string>();
        // Trailing separator matters: without it, "plugins\Foo" would also
        // match "plugins\FooBar\...", letting a crafted entry escape into a
        // sibling folder that shares a name prefix.
        var rootPrefix = Path.GetFullPath(modRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(modRoot, entry.FullName));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry '{entry.FullName}' escapes the mod folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            files.Add(Path.GetRelativePath(game.GameDirectory, destination));
        }
        return files;
    }

    /// <summary>
    /// Finds mods already sitting in BepInEx/plugins that Barnyard doesn't
    /// track, matches them to the index by BepInPlugin GUID, renames their
    /// folders to Barnyard's naming scheme, and registers them as installed.
    /// Mods the server doesn't know become local mods. Returns how many mods
    /// were adopted.
    /// </summary>
    public int AdoptExistingMods(ModIndex index, GameInstall game)
    {
        var pluginsDir = game.PluginsDirectory;
        if (!Directory.Exists(pluginsDir)) return 0;

        var trackedFiles = new HashSet<string>(
            _installed.SelectMany(m => m.RelativeFiles), StringComparer.OrdinalIgnoreCase);
        var adopted = 0;

        // Snapshot first: adoption renames folders while we iterate.
        var dllPaths = Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories).ToList();
        foreach (var dllPath in dllPaths)
        {
            if (!File.Exists(dllPath)) continue; // moved by an earlier adoption
            var relative = Path.GetRelativePath(game.GameDirectory, dllPath);
            if (trackedFiles.Contains(relative)) continue;

            PluginInfo? info;
            try
            {
                using var fs = File.OpenRead(dllPath);
                info = DllInspector.Inspect(fs);
            }
            catch { continue; }
            if (info is null) continue;
            if (_installed.Any(m => string.Equals(m.Id, info.Guid, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Known on the server → adopt as a normal install; unknown → adopt
            // as a local mod so it's still tracked and manageable.
            var entry = index.Find(info.Guid);
            var displayName = entry?.Name ?? (string.IsNullOrWhiteSpace(info.Name) ? info.Guid : info.Name);

            var folderName = ModNaming.ToFolderName(displayName, info.Guid);
            var targetRoot = Path.Combine(pluginsDir, folderName);
            var parent = Path.GetDirectoryName(dllPath)!;
            string modRoot;
            try
            {
                if (PathsEqual(parent, pluginsDir))
                {
                    // Loose dll in the plugins root: move it into its own folder.
                    Directory.CreateDirectory(targetRoot);
                    var destination = Path.Combine(targetRoot, Path.GetFileName(dllPath));
                    if (File.Exists(destination)) continue;
                    File.Move(dllPath, destination);
                    modRoot = targetRoot;
                }
                else
                {
                    // Rename the mod's top-level folder under plugins.
                    var top = parent;
                    while (!PathsEqual(Path.GetDirectoryName(top)!, pluginsDir))
                        top = Path.GetDirectoryName(top)!;

                    if (PathsEqual(top, targetRoot))
                    {
                        modRoot = top;
                    }
                    else if (!Directory.Exists(targetRoot))
                    {
                        Directory.Move(top, targetRoot);
                        modRoot = targetRoot;
                    }
                    else
                    {
                        modRoot = top; // name taken; adopt in place
                    }
                }
            }
            catch { continue; }

            var files = Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(game.GameDirectory, f))
                .ToList();

            var matchingVersion = entry?.Versions.FirstOrDefault(v =>
                string.Equals(v.Version, info.Version, StringComparison.OrdinalIgnoreCase));

            _installed.Add(new InstalledMod
            {
                Id = info.Guid,
                Name = displayName,
                FolderName = Path.GetFileName(modRoot),
                Version = info.Version,
                Revision = matchingVersion?.Revision ?? 0,
                Enabled = true,
                IsLocal = entry is null,
                RelativeFiles = files,
                Dependencies = new Dictionary<string, string>(
                    matchingVersion?.Dependencies
                    ?? entry?.Latest()?.Dependencies
                    ?? new Dictionary<string, string>()),
                Conflicts = entry is null ? new List<string>() : new List<string>(entry.Conflicts),
            });
            trackedFiles.UnionWith(files);
            adopted++;
        }

        if (adopted > 0) SaveState();
        return adopted;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    public async Task UninstallAsync(string modId, GameInstall game)
    {
        var dependents = GetDependents(modId);
        if (dependents.Count > 0)
            throw new InvalidOperationException(
                $"Cannot uninstall '{modId}': required by {string.Join(", ", dependents)}.");

        await UninstallFilesOnlyAsync(modId, game);
        _installed.RemoveAll(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        SaveState();
    }

    private Task UninstallFilesOnlyAsync(string modId, GameInstall game)
    {
        var mod = _installed.FirstOrDefault(m =>
            string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (mod is null) return Task.CompletedTask;

        // Older installs (pre display-name folders) recorded no FolderName;
        // fall back to the GUID folder for those.
        var folderName = string.IsNullOrEmpty(mod.FolderName)
            ? ModNaming.ToFolderName(mod.Name, mod.Id)
            : mod.FolderName;

        // Containment guard: deletion may only ever touch files inside THIS
        // mod's own folder under BepInEx/plugins. Even if the install state
        // is corrupted and lists foreign paths, other mods' files (and
        // anything else in the game folder) are never deleted.
        var allowedRoots = new[] { folderName, mod.Id }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => Path.GetFullPath(Path.Combine(game.PluginsDirectory, c))
                         + Path.DirectorySeparatorChar)
            .ToArray();

        foreach (var relative in mod.RelativeFiles)
        {
            var path = Path.GetFullPath(Path.Combine(game.GameDirectory, relative));
            if (!allowedRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (File.Exists(path)) File.Delete(path);
            var disabled = path + ".disabled";
            if (File.Exists(disabled)) File.Delete(disabled);
        }

        // Remove the mod's folder if it's now empty.
        foreach (var candidate in new[] { folderName, mod.Id }.Distinct())
        {
            var modRoot = Path.Combine(game.PluginsDirectory, candidate);
            if (Directory.Exists(modRoot) && !Directory.EnumerateFileSystemEntries(modRoot).Any())
                Directory.Delete(modRoot);
        }

        return Task.CompletedTask;
    }

    public void SetEnabled(string modId, bool enabled, GameInstall game)
    {
        var mod = _installed.FirstOrDefault(m =>
            string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Mod '{modId}' is not installed.");

        if (enabled)
        {
            // Enabling cascades: a mod only works if its dependencies run too.
            EnableRecursive(mod, game, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            SaveState();
            return;
        }

        var dependents = GetDependents(modId)
            .Where(id => _installed.First(m => m.Id == id).Enabled)
            .ToList();
        if (dependents.Count > 0)
            throw new InvalidOperationException(
                $"Cannot disable '{modId}': required by enabled mods {string.Join(", ", dependents)}.");

        FlipModFiles(mod, enabled: false, game);
        mod.Enabled = false;
        SaveState();
    }

    private void EnableRecursive(InstalledMod mod, GameInstall game, HashSet<string> visiting)
    {
        if (!visiting.Add(mod.Id)) return; // cycle guard

        foreach (var depId in mod.Dependencies.Keys)
        {
            var dep = _installed.FirstOrDefault(m =>
                string.Equals(m.Id, depId, StringComparison.OrdinalIgnoreCase));
            if (dep is not null && !dep.Enabled)
                EnableRecursive(dep, game, visiting);
        }

        if (!mod.Enabled)
        {
            FlipModFiles(mod, enabled: true, game);
            mod.Enabled = true;
        }
    }

    private static void FlipModFiles(InstalledMod mod, bool enabled, GameInstall game)
    {
        foreach (var relative in mod.RelativeFiles.Where(f =>
            f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)))
        {
            var basePath = Path.Combine(game.GameDirectory,
                relative.EndsWith(".disabled") ? relative[..^".disabled".Length] : relative);
            var enabledPath = basePath;
            var disabledPath = basePath + ".disabled";

            if (enabled && File.Exists(disabledPath)) File.Move(disabledPath, enabledPath, overwrite: true);
            if (!enabled && File.Exists(enabledPath)) File.Move(enabledPath, disabledPath, overwrite: true);
        }
    }

    private List<InstalledMod> LoadState()
    {
        if (!File.Exists(_stateFilePath)) return new List<InstalledMod>();
        try
        {
            return JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(_stateFilePath))
                   ?? new List<InstalledMod>();
        }
        catch
        {
            return new List<InstalledMod>();
        }
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(_installed, JsonOptions));
    }
}
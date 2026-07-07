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
            });
            SaveState();
        }
    }

    private static List<string> ExtractModZip(Stream zipStream, string folderName, GameInstall game)
    {
        var modRoot = Path.Combine(game.PluginsDirectory, folderName);
        Directory.CreateDirectory(modRoot);

        var files = new List<string>();
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(modRoot, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(modRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry '{entry.FullName}' escapes the mod folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            files.Add(Path.GetRelativePath(game.GameDirectory, destination));
        }
        return files;
    }

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

        foreach (var relative in mod.RelativeFiles)
        {
            var path = Path.Combine(game.GameDirectory, relative);
            if (File.Exists(path)) File.Delete(path);
            var disabled = path + ".disabled";
            if (File.Exists(disabled)) File.Delete(disabled);
        }

        // Remove the mod's folder if it's now empty. Older installs (pre display-name
        // folders) recorded no FolderName; fall back to the GUID folder for those.
        var folderName = string.IsNullOrEmpty(mod.FolderName)
            ? ModNaming.ToFolderName(mod.Name, mod.Id)
            : mod.FolderName;
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
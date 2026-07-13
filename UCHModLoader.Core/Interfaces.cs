using UCHModLoader.Core.Models;

namespace UCHModLoader.Core;

public interface IGameLocator
{
    GameInstall? FindGame();
}

public interface IBepInExManager
{
    bool IsInstalled(GameInstall game);
    Task InstallAsync(GameInstall game, string bepInExZipUrl, CancellationToken ct = default);

    /// <summary>The correct BepInEx package for this install's platform.</summary>
    string GetDefaultDownloadUrl(GameInstall game);

    /// <summary>Steam launch options the user must set, or null when none are needed.</summary>
    string? GetRequiredLaunchOptions(GameInstall game);

    /// <summary>Enables or disables the BepInEx console window in BepInEx.cfg.</summary>
    void SetConsoleEnabled(GameInstall game, bool enabled);
}

public interface IModRepository
{
    Task<ModIndex> GetIndexAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ModPack>> GetPacksAsync(CancellationToken ct = default);
    Task<Stream> DownloadAsync(ModVersionInfo version, CancellationToken ct = default);
}

public interface IInstallManager
{
    IReadOnlyList<InstalledMod> GetInstalled();

    /// <summary>
    /// Checks that every recorded file of every installed mod actually exists
    /// in the game folder. When removeBroken is true, mods with missing or
    /// partial files are dropped from the install state (they can be
    /// reinstalled from Browse).
    /// </summary>
    InstallVerificationResult VerifyInstalls(GameInstall game, bool removeBroken = true);
    /// <summary>
    /// Syncs Enabled flags from file reality (dll vs dll.disabled), picking up
    /// changes made outside the loader (e.g. the in-game config manager).
    /// </summary>
    void ReconcileEnabledFromDisk(GameInstall game);
    IReadOnlyList<string> GetDependents(string modId);
    /// <summary>
    /// Plans an install of the latest revision, or a specific revision when
    /// targetRevision is provided (enables rollback to an older build).
    /// </summary>
    InstallPlan PlanInstall(string modId, ModIndex index, int? targetRevision = null);
    Task ExecuteAsync(InstallPlan plan, IModRepository repository, GameInstall game, CancellationToken ct = default);
    /// <summary>
    /// Installs a mod directly from a local .dll or .zip, bypassing the server.
    /// The mod is tracked like any other install but marked IsLocal.
    /// </summary>
    Task<InstalledMod> InstallLocalAsync(string sourcePath, string displayName, string description, GameInstall game);
    /// <summary>
    /// Adopts untracked mods found in BepInEx/plugins: matches dlls to the
    /// index by GUID, renames folders to Barnyard's scheme, and registers
    /// them as installed; mods unknown to the server become local mods.
    /// Returns the number adopted.
    /// </summary>
    int AdoptExistingMods(ModIndex index, GameInstall game);
    Task UninstallAsync(string modId, GameInstall game);
    void SetEnabled(string modId, bool enabled, GameInstall game);
}

public interface IGameLauncher
{
    void Launch();
}
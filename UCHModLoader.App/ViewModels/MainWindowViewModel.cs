using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UCHModLoader.App.Services;
using UCHModLoader.Core;
using UCHModLoader.Core.Models;
using UCHModLoader.Core.Services;

namespace UCHModLoader.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public const string LoaderVersion = "1.0.0";

    private const string AllTagsOption = "All tags";
    public const string SortMostDownloaded = "Most downloaded";
    public const string SortMostUpvoted = "Most upvoted";
    public const string SortNewest = "Newest";
    public const string SortName = "Name (A–Z)";

    private readonly IGameLocator _gameLocator;
    private readonly IBepInExManager _bepInEx;
    private readonly IModRepository _repository;
    private readonly IInstallManager _installManager;
    private readonly IGameLauncher _launcher;

    private GameInstall? _game;
    private ModIndex? _index;
    private HashSet<string> _myVotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProfileService _profiles = new();
    private readonly AppSettings _settings;

    [ObservableProperty] private string _selectedTab = "Installed";
    [ObservableProperty] private string _gameStatus = "Locating game…";
    [ObservableProperty] private bool _gameFound;
    [ObservableProperty] private string _gamePath = "";
    [ObservableProperty] private string _bepInExStatus = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _browseSearchText = "";
    [ObservableProperty] private string _selectedTagFilter = AllTagsOption;
    [ObservableProperty] private string _selectedSortOption = SortMostDownloaded;
    [ObservableProperty] private ModRowViewModel? _selectedBrowseMod;
    [ObservableProperty] private ModPackViewModel? _selectedPack;
    [ObservableProperty] private string _activeProfileName = ProfileService.DefaultProfileName;
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private string _privateKeyInput = "";
    [ObservableProperty] private bool _isBepInExPromptVisible;
    [ObservableProperty] private bool _isBepInExInstalling;
    [ObservableProperty] private bool _isLaunchOptionsPromptVisible;
    [ObservableProperty] private string _launchOptionsText = "";
    [ObservableProperty] private bool _isUpdatePromptVisible;
    [ObservableProperty] private string _updatePromptText = "";
    [ObservableProperty] private string _loaderUpdateUrl = "";
    [ObservableProperty] private string _gamePathInput = "";
    [ObservableProperty] private string _reportReason = "";
    [ObservableProperty] private bool _showAllInstalledMods;
    [ObservableProperty] private bool _showOnlyUninstalledBrowseMods;
    [ObservableProperty] private bool _showBepInExConsole;
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _autoUpdateMods;
    [ObservableProperty] private bool _isRedeemingKey;
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private bool _isSetupVisible;
    [ObservableProperty] private double _setupOpacity = 1;
    [ObservableProperty] private string _setupStatus = "";
    [ObservableProperty] private string _setupStage = "Loading";
    private bool _showLaunchOptionsAfterSetup;

    [ObservableProperty] private string _setupSyncedText = "";

    public bool IsSetupLoading => SetupStage == "Loading";
    public bool IsSetupSynced => SetupStage == "Synced";
    public bool IsSetupChoice => SetupStage == "Choice";
    public bool IsSetupCompetitive => SetupStage == "Competitive";
    /// <summary>The logo only decorates the first (loading/synced) screen.</summary>
    public bool IsSetupLogoVisible => IsSetupLoading || IsSetupSynced;

    partial void OnSetupStageChanged(string value)
    {
        OnPropertyChanged(nameof(IsSetupLoading));
        OnPropertyChanged(nameof(IsSetupSynced));
        OnPropertyChanged(nameof(IsSetupChoice));
        OnPropertyChanged(nameof(IsSetupCompetitive));
        OnPropertyChanged(nameof(IsSetupLogoVisible));
    }

    private readonly DispatcherTimer _toastTimer;

    // Mods disabled while viewing the profile filter stay listed until the
    // user navigates away — otherwise the row vanishes mid-click.
    private readonly HashSet<string> _retainedInstalledIds = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ModRowViewModel> InstalledMods { get; } = new();
    public ObservableCollection<ModRowViewModel> DisplayedInstalledMods { get; } = new();
    public ObservableCollection<ModRowViewModel> AvailableMods { get; } = new();
    public ObservableCollection<ModRowViewModel> FilteredAvailableMods { get; } = new();
    public ObservableCollection<string> TagFilterOptions { get; } = new();
    public ObservableCollection<string> SortOptions { get; } = new();
    public ObservableCollection<ModPackViewModel> Packs { get; } = new();
    public ObservableCollection<ProfileRowViewModel> ProfileRows { get; } = new();
    public UploadViewModel Upload { get; }
    private static readonly HttpClient ApiHttp = new();
    private readonly IconCache _iconCache = new();

    public bool IsInstalledTab => SelectedTab == "Installed";
    public bool IsBrowseTab => SelectedTab == "Browse";
    public bool IsSettingsTab => SelectedTab == "Settings";
    public bool IsUploadTab => SelectedTab == "Upload";
    public bool IsProfilesTab => SelectedTab == "Profiles";
    public bool HasPacks => Packs.Count > 0;

    public string InstalledViewButtonText =>
        ShowAllInstalledMods ? "Show profile" : "Show all";

    public string BrowseViewButtonText =>
        ShowOnlyUninstalledBrowseMods ? "Show all" : "Show uninstalled";

    public bool IsBrowseGridVisible => IsBrowseTab && SelectedBrowseMod is null && SelectedPack is null;
    public bool IsBrowseDetailVisible => IsBrowseTab && SelectedBrowseMod is not null;
    public bool IsPackDetailVisible => IsBrowseTab && SelectedPack is not null;

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstalledTab));
        OnPropertyChanged(nameof(IsBrowseTab));
        OnPropertyChanged(nameof(IsSettingsTab));
        OnPropertyChanged(nameof(IsUploadTab));
        OnPropertyChanged(nameof(IsProfilesTab));
        OnPropertyChanged(nameof(IsBrowseGridVisible));
        OnPropertyChanged(nameof(IsBrowseDetailVisible));
        OnPropertyChanged(nameof(IsPackDetailVisible));
    }

    partial void OnSelectedBrowseModChanged(ModRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsBrowseGridVisible));
        OnPropertyChanged(nameof(IsBrowseDetailVisible));
    }

    partial void OnSelectedPackChanged(ModPackViewModel? value)
    {
        OnPropertyChanged(nameof(IsBrowseGridVisible));
        OnPropertyChanged(nameof(IsPackDetailVisible));
    }

    partial void OnShowAllInstalledModsChanged(bool value)
    {
        OnPropertyChanged(nameof(InstalledViewButtonText));
        ApplyInstalledFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyInstalledFilter();

    partial void OnShowBepInExConsoleChanged(bool value)
    {
        _settings.ShowBepInExConsole = value;
        _settings.Save();

        if (_game is null) return;
        try
        {
            _bepInEx.SetConsoleEnabled(_game, value);
            StatusMessage = value
                ? "BepInEx console will appear on launch"
                : "BepInEx console hidden on launch";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not update BepInEx config: {ex.Message}";
        }
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        _settings.DarkMode = value;
        _settings.Save();
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant =
                value ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    partial void OnAutoUpdateModsChanged(bool value)
    {
        _settings.AutoUpdateMods = value;
        _settings.Save();
    }

    partial void OnStatusMessageChanged(string value) => ShowToast(value);

    private void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (IsSetupVisible) return; // the setup screen shows its own status
        ToastMessage = message;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer.Stop();
        IsToastVisible = false;
    }

    [ObservableProperty] private bool _isLicensePopupVisible;

    [RelayCommand]
    private void ShowLicense() => IsLicensePopupVisible = true;

    [RelayCommand]
    private void DismissLicense() => IsLicensePopupVisible = false;

    partial void OnShowOnlyUninstalledBrowseModsChanged(bool value)
    {
        OnPropertyChanged(nameof(BrowseViewButtonText));
        ApplyBrowseFilter();
    }

    partial void OnBrowseSearchTextChanged(string value) => ApplyBrowseFilter();
    partial void OnSelectedTagFilterChanged(string value) => ApplyBrowseFilter();
    partial void OnSelectedSortOptionChanged(string value) => ApplyBrowseFilter();

    public MainWindowViewModel(IGameLocator gameLocator, IBepInExManager bepInEx,
        IModRepository repository, IInstallManager installManager, IGameLauncher launcher,
        UploadViewModel uploadViewModel, AppSettings settings)
    {
        _settings = settings;
        _showBepInExConsole = settings.ShowBepInExConsole;
        _isDarkMode = settings.DarkMode;
        _autoUpdateMods = settings.AutoUpdateMods;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); IsToastVisible = false; };
        _gameLocator = gameLocator;
        _bepInEx = bepInEx;
        _repository = repository;
        _installManager = installManager;
        _launcher = launcher;
        Upload = uploadViewModel;
        Upload.UploadSucceeded += async (_, _) => await RefreshAfterUploadAsync();
        // Upload status lines used to hide at the bottom of the page; surface
        // them in the same toast as everything else.
        Upload.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UploadViewModel.Status)) ShowToast(Upload.Status);
        };
        Upload.LocalInstallHandler = async (path, name, description) =>
        {
            if (_game is null) return "Game not found — set the game folder in Settings first.";
            try
            {
                var mod = await _installManager.InstallLocalAsync(path, name, description, _game);
                SyncActiveProfileFromInstalled();
                RefreshLists();
                StatusMessage = $"Installed local mod {mod.Name}";
                return null;
            }
            catch (Exception ex)
            {
                return $"Local install failed: {ex.Message}";
            }
        };

        TagFilterOptions.Add(AllTagsOption);
        foreach (var tag in ModTags.All) TagFilterOptions.Add(tag);
        SortOptions.Add(SortMostDownloaded);
        SortOptions.Add(SortMostUpvoted);
        SortOptions.Add(SortNewest);
        SortOptions.Add(SortName);
    }

    public async Task InitializeAsync()
    {
        // TESTING: always run setup, even if settings.json says it completed.
        // Restore the check to make it first-run only:
        // if (!_settings.SetupCompleted)
        {
            await RunFirstTimeSetupAsync();
            return;
        }

        LocateGame();

        if (_game is not null && !CheckBepInExInstalled())
            IsBepInExPromptVisible = true;

        VerifyInstallations(silent: true);

        await RefreshIndexAsync();
        await LoadMyVotesAsync();

        // The profile is the desired state: install anything it lists that
        // isn't actually present (e.g. after a game reinstall).
        ActiveProfileName = _profiles.ActiveProfileName;
        var restored = await EnsureProfileModsInstalledAsync(_profiles.Active);
        MergeProfileWithReality(_profiles.Active);
        _profiles.Save();

        RefreshLists();
        RefreshProfileRows();

        _ = CheckLoaderUpdateAsync();

        var updates = InstalledMods.Count(m => m.UpdateAvailable);
        if (updates > 0 && AutoUpdateMods)
        {
            StatusMessage = $"Auto-updating {updates} mod{(updates == 1 ? "" : "s")}…";
            await UpdateAllAsync();
        }
        else if (updates > 0)
        {
            StatusMessage = $"{updates} of your installed mods need an update";
        }
        if (restored > 0)
            StatusMessage = restored == 1
                ? $"Restored 1 missing mod from profile '{ActiveProfileName}'"
                : $"Restored {restored} missing mods from profile '{ActiveProfileName}'";
    }

    private void LocateGame()
    {
        _game = !string.IsNullOrWhiteSpace(_settings.GamePathOverride) &&
                Directory.Exists(_settings.GamePathOverride)
            ? SteamGameLocator.DescribeInstall(_settings.GamePathOverride)
            : _gameLocator.FindGame();
        GamePathInput = _settings.GamePathOverride;
        GameFound = _game is not null;
        GameStatus = GameFound ? "Game found" : "Game not found";
        GamePath = _game?.GameDirectory ?? "";

        if (_game is not null)
            LaunchOptionsText = _bepInEx.GetRequiredLaunchOptions(_game) ?? "";
    }

    /// <summary>
    /// First-run experience: locate the game, install BepInEx silently, adopt
    /// any mods already in the plugins folder, then ask what Barnyard is for.
    /// </summary>
    private async Task RunFirstTimeSetupAsync()
    {
        IsSetupVisible = true;
        SetupStage = "Loading";
        SetupStatus = "Locating game folder…";
        await Task.Delay(1200); // let each status line register

        LocateGame();

        var installedBepInEx = false;
        if (_game is not null && !CheckBepInExInstalled())
        {
            SetupStatus = "Installing BepInEx…";
            try
            {
                await _bepInEx.InstallAsync(_game, _bepInEx.GetDefaultDownloadUrl(_game));
                BepInExStatus = "BepInEx installed";
                installedBepInEx = true;
            }
            catch { /* the regular prompt can retry after setup */ }
        }
        _showLaunchOptionsAfterSetup = installedBepInEx && !string.IsNullOrEmpty(LaunchOptionsText);

        SetupStatus = "Syncing mods…";
        await Task.Delay(500);
        VerifyInstallations(silent: true);
        await RefreshIndexAsync();
        await LoadMyVotesAsync();

        // Adopt mods the player installed by hand before Barnyard existed.
        if (_game is not null && _index is not null)
        {
            try { _installManager.AdoptExistingMods(_index, _game); } catch { }
        }

        // First-run premise: everything currently installed is the player's
        // starting set. Enable it all so the sync lands in the default
        // profile (the merge below only picks up enabled mods).
        if (_game is not null)
        {
            foreach (var mod in _installManager.GetInstalled().Where(m => !m.Enabled).ToList())
            {
                try { _installManager.SetEnabled(mod.Id, true, _game); } catch { }
            }
        }

        ActiveProfileName = _profiles.ActiveProfileName;
        MergeProfileWithReality(_profiles.Active);

        // Synced mods always land in the default profile, even if another
        // profile happens to be active.
        var defaultProfile = _profiles.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, ProfileService.DefaultProfileName, StringComparison.OrdinalIgnoreCase));
        if (defaultProfile is not null && !ReferenceEquals(defaultProfile, _profiles.Active))
            MergeProfileWithReality(defaultProfile);

        _profiles.Save();
        RefreshLists();
        RefreshProfileRows();
        _ = CheckLoaderUpdateAsync();
        await Task.Delay(800);

        var synced = InstalledMods.Count;
        SetupSyncedText = synced == 1 ? "1 mod synced" : $"{synced} mods synced";
        SetupStage = "Synced";
    }

    [RelayCommand]
    private void SetupContinue() => SetupStage = "Choice";

    [RelayCommand]
    private Task SetupChooseOther() => FinishSetupAsync();

    [RelayCommand]
    private void SetupChooseCompetitive() => SetupStage = "Competitive";

    [RelayCommand]
    private async Task SetupInstallCompetitiveAsync()
    {
        var pack = Packs.FirstOrDefault(p =>
            p.Name.Contains("competitive", StringComparison.OrdinalIgnoreCase));
        if (pack is not null && _game is not null && _index is not null)
        {
            SetupStage = "Loading";
            SetupStatus = "Installing the competitive mod pack…";
            await InstallPackAsync(pack);
        }
        await FinishSetupAsync();
    }

    [RelayCommand]
    private Task SetupSkipCompetitive() => FinishSetupAsync();

    private async Task FinishSetupAsync()
    {
        // TESTING: setup runs on every launch. Restore these two lines to make
        // it first-run only.
        // _settings.SetupCompleted = true;
        // _settings.Save();
        SetupOpacity = 0;           // fades via the panel's opacity transition
        await Task.Delay(500);
        IsSetupVisible = false;
        if (_showLaunchOptionsAfterSetup)
            IsLaunchOptionsPromptVisible = true;
    }

    private async Task RefreshAfterUploadAsync()
    {
        await RefreshIndexAsync();
        RefreshLists();
        StatusMessage = "Mod list refreshed after upload";
    }

    private async Task RefreshIndexAsync()
    {
        var indexCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UCHModLoader", "index-cache.json");
        try
        {
            _index = await _repository.GetIndexAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(indexCachePath)!);
                await File.WriteAllTextAsync(indexCachePath, JsonSerializer.Serialize(_index));
            }
            catch { }
            Upload.SetIndex(_index);

            var openPackId = SelectedPack?.Id;
            Packs.Clear();
            foreach (var pack in await _repository.GetPacksAsync())
            {
                var packVm = new ModPackViewModel
                {
                    Id = pack.Id,
                    Name = pack.Name,
                    Description = pack.Description,
                    ModIds = pack.ModIds,
                    IconUrl = pack.IconUrl,
                    IconVersion = pack.IconVersion,
                    Pack = pack,
                };
                foreach (var modId in pack.ModIds)
                {
                    var packEntry = _index?.Find(modId);
                    packVm.Mods.Add(new ModRowViewModel
                    {
                        Id = modId,
                        Name = packEntry?.Name ?? modId,
                        Author = packEntry?.Author ?? "",
                        Description = packEntry?.Description ?? "",
                        LatestVersion = packEntry?.Latest()?.Version ?? "",
                        IconUrl = packEntry?.IconUrl,
                        IconVersion = packEntry?.IconVersion,
                        Entry = packEntry,
                    });
                }
                Packs.Add(packVm);
            }
            if (openPackId is not null)
                SelectedPack = Packs.FirstOrDefault(p =>
                    string.Equals(p.Id, openPackId, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(HasPacks));

            StatusMessage = $"Index loaded · {_index.Mods.Count} mods available";
        }
        catch (Exception ex)
        {
            // Offline fallback: last successful index from disk.
            try
            {
                _index = JsonSerializer.Deserialize<ModIndex>(
                    await File.ReadAllTextAsync(indexCachePath)) ?? new ModIndex();
                Upload.SetIndex(_index);
                StatusMessage = "Offline — showing the last known mod list";
            }
            catch
            {
                StatusMessage = $"Could not load mod index: {ex.Message}";
                _index = new ModIndex();
            }
        }
    }

    private async Task LoadMyVotesAsync()
    {
        _myVotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Upload.IsLoggedIn) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{Upload.BaseUrl}/api/votes/mine");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Upload.ApiToken);
            var response = await ApiHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            var ids = JsonSerializer.Deserialize<List<string>>(
                await response.Content.ReadAsStringAsync()) ?? new List<string>();
            _myVotes = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private void RefreshLists()
    {
        var openModId = SelectedBrowseMod?.Id;

        InstalledMods.Clear();
        AvailableMods.Clear();

        var installed = _installManager.GetInstalled();

        foreach (var mod in installed)
        {
            var entry = _index?.Find(mod.Id);
            InstalledMods.Add(new ModRowViewModel
            {
                Id = mod.Id,
                Name = mod.Name,
                Author = entry?.Author ?? (mod.IsLocal ? "local file" : ""),
                Description = entry?.Description ?? mod.Description,
                ConflictIds = entry?.Conflicts ?? mod.Conflicts,
                InstalledVersion = mod.Version,
                InstalledRevision = mod.Revision,
                LatestVersion = entry?.Latest()?.Version ?? mod.Version,
                LatestRevision = entry?.Latest()?.Revision ?? 0,
                IsEnabled = mod.Enabled,
                IsLockedDependency = _installManager.GetDependents(mod.Id).Count > 0,
                IconUrl = entry?.IconUrl,
                IconVersion = entry?.IconVersion,
                Entry = entry,
                Installed = mod,
            });
        }

        if (_index is not null)
        {
            foreach (var entry in _index.Mods)
            {
                var existing = installed.FirstOrDefault(m =>
                    string.Equals(m.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                var latest = entry.Latest();
                var depNames = latest is null
                    ? ""
                    : string.Join(", ", latest.Dependencies.Keys
                        .Select(id => _index.Find(id)?.Name ?? id));
                var history = entry.Versions
                    .OrderByDescending(v => v.Revision)
                    .Select(v => new VersionOptionViewModel
                    {
                        ModId = entry.Id,
                        Version = v.Version,
                        Revision = v.Revision,
                        Changelog = v.Changelog,
                        UploadedDisplay = v.UploadedUtc == default
                            ? ""
                            : v.UploadedUtc.ToLocalTime().ToString("yyyy-MM-dd"),
                        IsInstalledRevision = existing is not null && existing.Revision == v.Revision,
                    })
                    .ToList();

                AvailableMods.Add(new ModRowViewModel
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Author = entry.Author,
                    Description = entry.Description,
                    DependenciesDisplay = depNames,
                    LatestChangelog = latest?.Changelog ?? "",
                    VersionHistory = history,
                    InstalledVersion = existing?.Version ?? "",
                    InstalledRevision = existing?.Revision ?? 0,
                    LatestVersion = entry.Latest()?.Version ?? "",
                    LatestRevision = entry.Latest()?.Revision ?? 0,
                    Downloads = entry.Downloads,
                    IsPrivate = entry.IsPrivate,
                    AuthorVerified = entry.AuthorVerified,
                    Upvotes = entry.Upvotes,
                    HasVoted = _myVotes.Contains(entry.Id),
                    Tags = entry.Tags,
                    IconUrl = entry.IconUrl,
                    IconVersion = entry.IconVersion,
                    Entry = entry,
                    Installed = existing,
                });
            }
        }

        // Keep the open details page bound to the freshly built row for the same mod.
        if (openModId is not null)
            SelectedBrowseMod = AvailableMods.FirstOrDefault(m =>
                string.Equals(m.Id, openModId, StringComparison.OrdinalIgnoreCase));

        ApplyBrowseFilter();
        ApplyInstalledFilter();
        _ = LoadIconsAsync();
    }

    /// <summary>
    /// Flags enabled installed mods that conflict with another enabled mod.
    /// Conflict declarations are symmetric: either side declaring it counts.
    /// </summary>
    private void ComputeConflicts()
    {
        var enabled = InstalledMods.Where(m => m.IsEnabled).ToList();
        foreach (var row in InstalledMods)
        {
            var conflicting = enabled
                .Where(other =>
                    !string.Equals(other.Id, row.Id, StringComparison.OrdinalIgnoreCase) &&
                    (row.ConflictIds.Contains(other.Id, StringComparer.OrdinalIgnoreCase) ||
                     other.ConflictIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase)))
                .Select(other => other.Name)
                .ToList();

            row.HasConflict = row.IsEnabled && conflicting.Count > 0;
            row.ConflictTooltip = row.HasConflict
                ? $"This mod conflicts with {string.Join(", ", conflicting)}. " +
                  "You can launch the game anyway, but you may run into issues."
                : "";
        }
    }

    private void ApplyInstalledFilter()
    {
        ComputeConflicts();
        DisplayedInstalledMods.Clear();

        var profileIds = new HashSet<string>(
            _profiles.Active.EnabledModIds, StringComparer.OrdinalIgnoreCase);

        foreach (var row in InstalledMods)
        {
            if (!ShowAllInstalledMods && !profileIds.Contains(row.Id) &&
                !_retainedInstalledIds.Contains(row.Id)) continue;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var query = SearchText.Trim();
                var matches =
                    row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    row.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    row.Author.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
            }

            DisplayedInstalledMods.Add(row);
        }
    }

    private void ApplyBrowseFilter()
    {
        // Private mods never appear in Browse — access is via key redemption,
        // and management via Installed / Your mods.
        IEnumerable<ModRowViewModel> mods = AvailableMods.Where(m => !m.IsPrivate);

        if (ShowOnlyUninstalledBrowseMods)
            mods = mods.Where(m => !m.IsInstalled);

        if (!string.IsNullOrWhiteSpace(BrowseSearchText))
        {
            var query = BrowseSearchText.Trim();
            mods = mods.Where(m =>
                m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.Author.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedTagFilter != AllTagsOption)
            mods = mods.Where(m => m.Tags.Contains(SelectedTagFilter, StringComparer.OrdinalIgnoreCase));

        mods = SelectedSortOption switch
        {
            SortMostUpvoted => mods.OrderByDescending(m => m.Upvotes).ThenByDescending(m => m.Downloads),
            SortNewest => mods.OrderByDescending(m => m.Entry?.Latest()?.UploadedUtc ?? DateTime.MinValue),
            SortName => mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            _ => mods.OrderByDescending(m => m.Downloads).ThenByDescending(m => m.Upvotes),
        };

        FilteredAvailableMods.Clear();
        foreach (var mod in mods) FilteredAvailableMods.Add(mod);
    }

    private async Task LoadIconsAsync()
    {
        var rows = AvailableMods.Concat(InstalledMods)
            .Concat(Packs.SelectMany(p => p.Mods));
        foreach (var row in rows.Where(r => r.Icon is null && !string.IsNullOrEmpty(r.IconUrl)).ToList())
        {
            row.Icon = await _iconCache.GetAsync(row.Id, row.IconVersion, row.IconUrl);
        }

        foreach (var pack in Packs.Where(p => p.Icon is null && !string.IsNullOrEmpty(p.IconUrl)).ToList())
        {
            pack.Icon = await _iconCache.GetAsync($"pack-{pack.Id}", pack.IconVersion, pack.IconUrl);
        }
    }

    [RelayCommand]
    private void SelectTab(string tab)
    {
        // Navigating counts as a page reload: rows kept visible after being
        // disabled can drop out of the profile view now.
        if (_retainedInstalledIds.Count > 0)
        {
            _retainedInstalledIds.Clear();
            ApplyInstalledFilter();
        }
        SelectedTab = tab;
        if (tab != "Browse")
        {
            SelectedBrowseMod = null;
            SelectedPack = null;
        }
    }

    private async Task CheckLoaderUpdateAsync()
    {
        try
        {
            using var response = await ApiHttp.GetAsync($"{Upload.BaseUrl}/api/loaderversion");
            if (!response.IsSuccessStatusCode) return;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var remote = json.RootElement.GetProperty("version").GetString() ?? "";
            var url = json.RootElement.GetProperty("downloadUrl").GetString() ?? "";

            if (Version.TryParse(remote, out var remoteVersion) &&
                Version.TryParse(LoaderVersion, out var localVersion) &&
                remoteVersion > localVersion)
            {
                UpdatePromptText =
                    $"Barnyard {remote} is available (you have {LoaderVersion}).";
                LoaderUpdateUrl = url;
                IsUpdatePromptVisible = true;
            }
        }
        catch { /* update check is best-effort */ }
    }

    [RelayCommand]
    private void OpenLoaderUpdate()
    {
        if (!string.IsNullOrEmpty(LoaderUpdateUrl)) UrlOpener.Open(LoaderUpdateUrl);
        IsUpdatePromptVisible = false;
    }

    [RelayCommand]
    private void DismissUpdatePrompt() => IsUpdatePromptVisible = false;

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (_game is null || _index is null) return;

        var outdated = InstalledMods.Where(m => m.UpdateAvailable).ToList();
        if (outdated.Count == 0) { StatusMessage = "Everything is up to date"; return; }

        var updated = 0;
        foreach (var row in outdated)
        {
            try
            {
                var plan = _installManager.PlanInstall(row.Id, _index);
                await _installManager.ExecuteAsync(plan, _repository, _game);
                _installManager.SetEnabled(row.Id, true, _game);
                updated++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Update of {row.Name} failed: {ex.Message}";
            }
        }

        SyncActiveProfileFromInstalled();
        RefreshLists();
        if (updated == outdated.Count)
            StatusMessage = $"Updated {updated} mod{(updated == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private void SetGamePath()
    {
        var path = GamePathInput.Trim();

        if (path.Length == 0)
        {
            _settings.GamePathOverride = "";
            _settings.Save();
            StatusMessage = "Game path override cleared — restart the loader to auto-detect.";
            return;
        }

        if (!Directory.Exists(path))
        {
            StatusMessage = "That folder does not exist.";
            return;
        }

        _settings.GamePathOverride = path;
        _settings.Save();

        _game = SteamGameLocator.DescribeInstall(path);
        GameFound = true;
        GameStatus = "Game found";
        GamePath = path;
        LaunchOptionsText = _bepInEx.GetRequiredLaunchOptions(_game) ?? "";

        if (!CheckBepInExInstalled())
            IsBepInExPromptVisible = true;

        VerifyInstallations(silent: false);
        RefreshLists();
        StatusMessage = "Game path set.";
    }

    [RelayCommand]
    private async Task InstallVersionAsync(VersionOptionViewModel version)
    {
        if (_game is null || _index is null) { StatusMessage = "Game not found."; return; }
        try
        {
            var plan = _installManager.PlanInstall(version.ModId, _index, version.Revision);
            await _installManager.ExecuteAsync(plan, _repository, _game);
            _installManager.SetEnabled(version.ModId, true, _game);
            SyncActiveProfileFromInstalled();
            StatusMessage = $"Installed {version.Display}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Install failed: {ex.Message}";
        }
        RefreshLists();
    }

    [RelayCommand]
    private async Task ReportModAsync(ModRowViewModel row)
    {
        if (!Upload.IsLoggedIn)
        {
            StatusMessage = "Log in with Discord to report a mod.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ReportReason))
        {
            StatusMessage = "Describe the problem before submitting a report.";
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Upload.BaseUrl}/api/mods/{Uri.EscapeDataString(row.Id)}/report");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Upload.ApiToken);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["reason"] = ReportReason.Trim(),
            });

            var response = await ApiHttp.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                ReportReason = "";
                StatusMessage = $"Report submitted for {row.Name} — thank you.";
            }
            else
            {
                StatusMessage = $"Report failed: {await response.Content.ReadAsStringAsync()}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Report failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleInstalledView() => ShowAllInstalledMods = !ShowAllInstalledMods;

    [RelayCommand]
    private void ToggleBrowseView() => ShowOnlyUninstalledBrowseMods = !ShowOnlyUninstalledBrowseMods;

    [RelayCommand]
    private void OpenMod(ModRowViewModel row) => SelectedBrowseMod = row;

    [RelayCommand]
    private void OpenPack(ModPackViewModel pack) => SelectedPack = pack;

    [RelayCommand]
    private void ClosePackDetails() => SelectedPack = null;

    [RelayCommand]
    private void ToggleExpand(ModRowViewModel row) => row.IsExpanded = !row.IsExpanded;

    [RelayCommand]
    private void CloseModDetails() => SelectedBrowseMod = null;

    /// <summary>
    /// Installs any mods the profile lists that aren't currently installed
    /// (and exist in the index). Returns how many were installed.
    /// </summary>
    private async Task<int> EnsureProfileModsInstalledAsync(Profile profile)
    {
        if (_game is null || _index is null) return 0;

        var installedIds = new HashSet<string>(
            _installManager.GetInstalled().Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var toInstall = profile.EnabledModIds
            .Where(id => !installedIds.Contains(id) && _index.Find(id) is not null)
            .ToList();

        var restored = 0;
        foreach (var modId in toInstall)
        {
            try
            {
                var plan = _installManager.PlanInstall(modId, _index);
                await _installManager.ExecuteAsync(plan, _repository, _game);
                _installManager.SetEnabled(modId, true, _game);
                restored++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not restore '{modId}': {ex.Message}";
            }
        }
        return restored;
    }

    /// <summary>
    /// Updates the profile from installed reality without forgetting desired
    /// mods that aren't installed right now (e.g. offline after a reinstall):
    /// installed-and-enabled mods are added, installed-but-disabled removed,
    /// and not-installed entries are preserved as intent.
    /// </summary>
    private void MergeProfileWithReality(Profile profile)
    {
        var installed = _installManager.GetInstalled();
        var enabled = new HashSet<string>(
            installed.Where(m => m.Enabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(
            installed.Where(m => !m.Enabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        profile.EnabledModIds = profile.EnabledModIds
            .Where(id => !disabled.Contains(id))
            .Union(enabled, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RefreshProfileRows()
    {
        ProfileRows.Clear();
        foreach (var profile in _profiles.Profiles)
        {
            ProfileRows.Add(new ProfileRowViewModel
            {
                Name = profile.Name,
                ModCount = profile.EnabledModIds.Count,
                IsActive = string.Equals(profile.Name, _profiles.ActiveProfileName,
                    StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private void SyncActiveProfileFromInstalled()
    {
        MergeProfileWithReality(_profiles.Active);
        _profiles.Save();
        RefreshProfileRows();
    }

    [RelayCommand]
    private void CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) { StatusMessage = "Profile name is required."; return; }
        if (name.Length > 40) { StatusMessage = "Profile name must be 40 characters or fewer."; return; }
        if (_profiles.NameExists(name)) { StatusMessage = $"A profile named '{name}' already exists."; return; }

        _profiles.Profiles.Add(new Profile { Name = name });
        _profiles.Save();
        NewProfileName = "";
        RefreshProfileRows();
        StatusMessage = $"Created profile '{name}' — switch to it and enable the mods you want.";
    }

    [RelayCommand]
    private void RemoveProfile(ProfileRowViewModel row)
    {
        if (row.IsActive) { StatusMessage = "Switch to another profile before removing this one."; return; }
        if (_profiles.Profiles.Count <= 1) { StatusMessage = "You need at least one profile."; return; }

        _profiles.Profiles.RemoveAll(p =>
            string.Equals(p.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        _profiles.Save();
        RefreshProfileRows();
        StatusMessage = $"Removed profile '{row.Name}'.";
    }

    [RelayCommand]
    private async Task SwitchProfileAsync(ProfileRowViewModel row)
    {
        if (row.IsActive) return;
        var profile = _profiles.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;

        _profiles.ActiveProfileName = profile.Name;
        ActiveProfileName = profile.Name;
        _retainedInstalledIds.Clear();

        var restored = await EnsureProfileModsInstalledAsync(profile);
        ApplyProfile(profile);
        _profiles.Save();
        RefreshLists();
        RefreshProfileRows();
        StatusMessage = restored > 0
            ? $"Switched to profile '{profile.Name}' (installed {restored} missing mod{(restored == 1 ? "" : "s")})"
            : $"Switched to profile '{profile.Name}'.";
    }

    private void ApplyProfile(Profile profile)
    {
        if (_game is null) return;
        var target = new HashSet<string>(profile.EnabledModIds, StringComparer.OrdinalIgnoreCase);

        // Enable pass: enabling is never blocked.
        foreach (var mod in _installManager.GetInstalled().Where(m => !m.Enabled && target.Contains(m.Id)).ToList())
        {
            try { _installManager.SetEnabled(mod.Id, true, _game); } catch { }
        }

        // Disable pass: loop so dependents unwind before their dependencies.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mod in _installManager.GetInstalled().Where(m => m.Enabled && !target.Contains(m.Id)).ToList())
            {
                try
                {
                    _installManager.SetEnabled(mod.Id, false, _game);
                    changed = true;
                }
                catch { /* still required by an enabled mod — later iterations may free it */ }
            }
        }

        // Dependencies of enabled mods may have stayed on; fold reality back
        // in without dropping not-installed intent.
        MergeProfileWithReality(profile);
    }

    [RelayCommand]
    private async Task InstallPackAsync(ModPackViewModel pack)
    {
        if (_game is null || _index is null) { StatusMessage = "Game not found."; return; }
        try
        {
            pack.IsBusy = true;
            StatusMessage = $"Installing pack '{pack.Name}'…";

            var installedCount = 0;
            var skipped = 0;
            foreach (var modId in pack.ModIds)
            {
                if (_index.Find(modId) is null) { skipped++; continue; }
                var plan = _installManager.PlanInstall(modId, _index);
                await _installManager.ExecuteAsync(plan, _repository, _game);
                _installManager.SetEnabled(modId, true, _game);
                installedCount++;
            }

            // The pack's mods join whatever profile is active.
            SyncActiveProfileFromInstalled();

            RefreshLists();
            RefreshProfileRows();
            StatusMessage = skipped == 0
                ? $"Installed pack '{pack.Name}' ({installedCount} mods) to profile '{ActiveProfileName}'"
                : $"Installed pack '{pack.Name}' ({installedCount} mods, {skipped} no longer available) to profile '{ActiveProfileName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Pack install failed: {ex.Message}";
        }
        finally
        {
            pack.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RedeemKeyAsync()
    {
        if (!Upload.IsLoggedIn)
        {
            StatusMessage = "Log in with Discord to redeem a private mod key.";
            return;
        }
        if (string.IsNullOrWhiteSpace(PrivateKeyInput))
        {
            StatusMessage = "Enter a key first.";
            return;
        }

        try
        {
            IsRedeemingKey = true;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Upload.BaseUrl}/api/mods/redeemkey");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Upload.ApiToken);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["key"] = PrivateKeyInput.Trim(),
            });

            var response = await ApiHttp.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                StatusMessage = $"Key rejected: {body}";
                return;
            }

            using var json = JsonDocument.Parse(body);
            var name = json.RootElement.GetProperty("name").GetString() ?? "the mod";
            var modId = json.RootElement.GetProperty("modId").GetString() ?? "";
            PrivateKeyInput = "";

            await RefreshIndexAsync();
            RefreshLists();

            // Private mods are hidden from the grid, so take the user straight
            // to the mod's details page where the install button lives.
            SelectedBrowseMod = AvailableMods.FirstOrDefault(m =>
                string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"Access granted — '{name}' unlocked.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Key redemption failed: {ex.Message}";
        }
        finally
        {
            IsRedeemingKey = false;
        }
    }

    [RelayCommand]
    private async Task ToggleVoteAsync(ModRowViewModel row)
    {
        if (!Upload.IsLoggedIn)
        {
            StatusMessage = "Log in with Discord on the Upload tab to vote.";
            return;
        }

        // Optimistic: flip immediately for instant feedback, then reconcile
        // with (or roll back to) whatever the server says.
        var votedBefore = row.HasVoted;
        var upvotesBefore = row.Upvotes;
        row.HasVoted = !votedBefore;
        row.Upvotes = upvotesBefore + (row.HasVoted ? 1 : -1);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Upload.BaseUrl}/api/mods/{Uri.EscapeDataString(row.Id)}/vote");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Upload.ApiToken);
            var response = await ApiHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                row.HasVoted = votedBefore;
                row.Upvotes = upvotesBefore;
                StatusMessage = "Vote failed — try logging in again on the Upload tab.";
                return;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            row.Upvotes = json.RootElement.GetProperty("upvotes").GetInt32();
            row.HasVoted = json.RootElement.GetProperty("voted").GetBoolean();
            if (row.HasVoted) _myVotes.Add(row.Id); else _myVotes.Remove(row.Id);
        }
        catch (Exception ex)
        {
            row.HasVoted = votedBefore;
            row.Upvotes = upvotesBefore;
            StatusMessage = $"Vote failed: {ex.Message}";
        }
    }

    private bool CheckBepInExInstalled()
    {
        if (_game is null) { BepInExStatus = ""; return false; }
        var installed = _bepInEx.IsInstalled(_game);
        BepInExStatus = installed ? "BepInEx installed" : "BepInEx not installed";
        return installed;
    }

    [RelayCommand]
    private async Task InstallBepInExAsync()
    {
        if (_game is null) return;
        try
        {
            IsBepInExInstalling = true;
            await _bepInEx.InstallAsync(_game, _bepInEx.GetDefaultDownloadUrl(_game));
            BepInExStatus = "BepInEx installed";
            StatusMessage = "BepInEx installed — your mods are ready";
            IsBepInExPromptVisible = false;

            // Non-Windows setups need Steam launch options set by hand;
            // show the instruction with a copyable string.
            var launchOptions = _bepInEx.GetRequiredLaunchOptions(_game);
            if (launchOptions is not null)
            {
                LaunchOptionsText = launchOptions;
                IsLaunchOptionsPromptVisible = true;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not install BepInEx: {ex.Message}";
        }
        finally
        {
            IsBepInExInstalling = false;
        }
    }

    [RelayCommand]
    private void DismissBepInExPrompt() => IsBepInExPromptVisible = false;

    [RelayCommand]
    private void DismissLaunchOptionsPrompt() => IsLaunchOptionsPromptVisible = false;

    public bool HasLaunchOptions => !string.IsNullOrEmpty(LaunchOptionsText);

    partial void OnLaunchOptionsTextChanged(string value) =>
        OnPropertyChanged(nameof(HasLaunchOptions));

    /// <summary>
    /// Reconciles installed-mod state with the actual game folder. Broken
    /// entries are removed from state and from all profiles.
    /// </summary>
    private int VerifyInstallations(bool silent)
    {
        if (_game is null) return 0;

        var result = _installManager.VerifyInstalls(_game);
        if (result.BrokenCount == 0) return 0;

        // Profiles deliberately keep these ids: the profile is the desired
        // state, and EnsureProfileModsInstalledAsync reinstalls the missing.
        if (!silent)
        {
            RefreshLists();
            RefreshProfileRows();
        }

        StatusMessage = result.BrokenCount == 1
            ? "1 mod was missing from the game folder"
            : $"{result.BrokenCount} mods were missing from the game folder";
        return result.BrokenCount;
    }

    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        // The game folder may have been reinstalled while the loader was open.
        // Without BepInEx no mod runs, so prompt instead of launching.
        if (_game is not null && !CheckBepInExInstalled())
        {
            IsBepInExPromptVisible = true;
            return;
        }

        // The game can regenerate BepInEx.cfg; re-stamp the console preference
        // before every launch so it always holds.
        try { _bepInEx.SetConsoleEnabled(_game!, ShowBepInExConsole); } catch { }

        var broken = VerifyInstallations(silent: false);

        var restored = 0;
        if (broken > 0)
        {
            restored = await EnsureProfileModsInstalledAsync(_profiles.Active);
            if (restored > 0)
            {
                MergeProfileWithReality(_profiles.Active);
                _profiles.Save();
                RefreshLists();
                RefreshProfileRows();
            }
        }

        try
        {
            _launcher.Launch();
            StatusMessage = restored > 0
                ? $"Restored {restored} missing mod{(restored == 1 ? "" : "s")} — launching game…"
                : "Launching game…";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Launch failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallModAsync(ModRowViewModel row)
    {
        if (_game is null || _index is null) { StatusMessage = "Game not found."; return; }
        try
        {
            row.IsBusy = true;
            // Dependencies resolve and install silently.
            var plan = _installManager.PlanInstall(row.Id, _index);
            await _installManager.ExecuteAsync(plan, _repository, _game);
            // Already-installed-but-disabled dependencies aren't in the plan;
            // the enable cascade switches them on.
            _installManager.SetEnabled(row.Id, true, _game);
            var deps = plan.DependencyActions.Count();
            StatusMessage = deps > 0
                ? $"Installed {row.Name} and {deps} dependenc{(deps == 1 ? "y" : "ies")}"
                : $"Installed {row.Name}";
            SyncActiveProfileFromInstalled();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Install failed: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            RefreshLists();
        }
    }

    [RelayCommand]
    private async Task UninstallModAsync(ModRowViewModel row)
    {
        if (_game is null) return;
        try
        {
            await _installManager.UninstallAsync(row.Id, _game);
            foreach (var profile in _profiles.Profiles)
                profile.EnabledModIds.RemoveAll(id =>
                    string.Equals(id, row.Id, StringComparison.OrdinalIgnoreCase));
            _profiles.Save();
            RefreshProfileRows();
            StatusMessage = $"Uninstalled {row.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        RefreshLists();
    }

    [RelayCommand]
    private void ToggleMod(ModRowViewModel row)
    {
        if (_game is null) return;
        try
        {
            var turningOn = !row.Installed!.Enabled;
            if (!turningOn && !ShowAllInstalledMods)
                _retainedInstalledIds.Add(row.Id);
            var enabledBefore = _installManager.GetInstalled().Count(m => m.Enabled);
            _installManager.SetEnabled(row.Id, turningOn, _game);
            var enabledAfter = _installManager.GetInstalled().Count(m => m.Enabled);

            if (turningOn)
            {
                var depsEnabled = enabledAfter - enabledBefore - 1;
                StatusMessage = depsEnabled > 0
                    ? $"Enabled {row.Name} (+{depsEnabled} dependenc{(depsEnabled == 1 ? "y" : "ies")})"
                    : $"Enabled {row.Name}";
            }
            else
            {
                StatusMessage = $"Disabled {row.Name}";
            }
            SyncActiveProfileFromInstalled();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        RefreshLists();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        await RefreshIndexAsync();
        await LoadMyVotesAsync();
        RefreshLists();
        var updates = InstalledMods.Count(m => m.UpdateAvailable);
        StatusMessage = updates == 0 ? "Everything is up to date" : $"{updates} update(s) available";
    }
}
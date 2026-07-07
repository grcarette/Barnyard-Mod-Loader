using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UCHModLoader.Core.Models;

namespace UCHModLoader.App.ViewModels;

public partial class ModRowViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public int InstalledRevision { get; init; }
    public int LatestRevision { get; init; }
    public long Downloads { get; init; }
    public bool IsPrivate { get; init; }
    public bool AuthorVerified { get; init; } = true;
    public bool IsNewCreator => !AuthorVerified;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private int _upvotes;
    [ObservableProperty] private bool _hasVoted;
    [ObservableProperty] private bool _isExpanded;
    public string? IconUrl { get; init; }
    public string? IconVersion { get; init; }

    public string DependenciesDisplay { get; init; } = "";
    public bool HasDependencies => DependenciesDisplay.Length > 0;
    public string LatestChangelog { get; init; } = "";
    public bool HasChangelog => LatestChangelog.Length > 0;
    public IReadOnlyList<VersionOptionViewModel> VersionHistory { get; init; } =
        Array.Empty<VersionOptionViewModel>();
    public bool HasVersionHistory => VersionHistory.Count > 1;

    public bool IsInstalled => !string.IsNullOrEmpty(InstalledVersion);
    public bool HasTags => Tags.Count > 0;
    public string TagsDisplay => string.Join("  ·  ", Tags);
    public string DownloadsDisplay => Downloads == 1 ? "1 download" : $"{Downloads:N0} downloads";

    public bool UpdateAvailable =>
        IsInstalled &&
        (LatestRevision > 0
            ? LatestRevision > InstalledRevision
            : !string.IsNullOrEmpty(LatestVersion) &&
              Version.TryParse(LatestVersion, out var lv) &&
              Version.TryParse(InstalledVersion, out var iv) && lv > iv);

    public bool IsLockedDependency { get; init; }
    public string Subtitle => $"{(IsInstalled ? InstalledVersion : LatestVersion)} · by {Author}";

    public ModEntry? Entry { get; init; }
    public InstalledMod? Installed { get; init; }
}

public sealed class VersionOptionViewModel
{
    public string ModId { get; init; } = "";
    public string Version { get; init; } = "";
    public int Revision { get; init; }
    public string Changelog { get; init; } = "";
    public bool HasChangelog => Changelog.Length > 0;
    public string UploadedDisplay { get; init; } = "";
    public bool IsInstalledRevision { get; init; }
    public string Display => $"{Version} (r{Revision})";
}
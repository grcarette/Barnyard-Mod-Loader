using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UCHModLoader.Core.Models;

namespace UCHModLoader.App.ViewModels;

public partial class ModPackViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> ModIds { get; init; } = Array.Empty<string>();

    /// <summary>Rows for the pack detail page: icon, name, expandable description.</summary>
    public ObservableCollection<ModRowViewModel> Mods { get; } = new();
    public string? IconUrl { get; init; }
    public string? IconVersion { get; init; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Bitmap? _icon;

    public string CountDisplay => ModIds.Count == 1 ? "1 mod" : $"{ModIds.Count} mods";

    public double CardNameFontSize => UiText.FitFontSize(Name, 13, 18, 9.5);

    public ModPack? Pack { get; init; }
}
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
    public IReadOnlyList<string> ModNames { get; init; } = Array.Empty<string>();
    public string? IconUrl { get; init; }
    public string? IconVersion { get; init; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Bitmap? _icon;

    public string CountDisplay => ModIds.Count == 1 ? "1 mod" : $"{ModIds.Count} mods";

    public ModPack? Pack { get; init; }
}
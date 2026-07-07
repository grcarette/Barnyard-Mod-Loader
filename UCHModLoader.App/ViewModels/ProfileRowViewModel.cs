using CommunityToolkit.Mvvm.ComponentModel;

namespace UCHModLoader.App.ViewModels;

public partial class ProfileRowViewModel : ObservableObject
{
    public string Name { get; init; } = "";
    public int ModCount { get; init; }

    [ObservableProperty] private bool _isActive;

    public string CountDisplay => ModCount == 1 ? "1 mod enabled" : $"{ModCount} mods enabled";
}
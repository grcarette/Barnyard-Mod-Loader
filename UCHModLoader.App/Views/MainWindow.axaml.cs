using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UCHModLoader.App.ViewModels;

namespace UCHModLoader.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "barnyard.ico");
        if (File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);
    }

    private UploadViewModel? Upload => (DataContext as MainWindowViewModel)?.Upload;

    private async Task<string?> PickFileAsync(string title, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title) { Patterns = patterns },
            },
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async void OnChooseModFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Mod file", "*.dll", "*.zip");
        if (path is not null && Upload is not null) Upload.ModFilePath = path;
    }

    private async void OnChooseIconFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Icon", "*.png");
        if (path is not null && Upload is not null) Upload.IconFilePath = path;
    }

    private async void OnChooseUpdateModFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Mod file", "*.dll", "*.zip");
        if (path is not null && Upload is not null) Upload.UpdateModFilePath = path;
    }

    private async void OnChooseDetailsIconFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Icon", "*.png");
        if (path is not null && Upload is not null) Upload.DetailsIconPath = path;
    }

    private async void OnChoosePackIconFile(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Icon", "*.png");
        if (path is not null && Upload is not null) Upload.PackIconPath = path;
    }

    private async void OnBrowseGameFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Ultimate Chicken Horse folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
            vm.GamePathInput = folders[0].Path.LocalPath;
    }
}
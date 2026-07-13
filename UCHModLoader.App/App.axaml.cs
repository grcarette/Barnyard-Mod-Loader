using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Net.Http;
using UCHModLoader.App.Services;
using UCHModLoader.App.ViewModels;
using UCHModLoader.App.Views;
using UCHModLoader.Core;
using UCHModLoader.Core.Services;

namespace UCHModLoader.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Clean up leftovers from the over-packed v1.0.0 release zip.
            ReleaseJunkCleanup.Run();

            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UCHModLoader/0.1");

            var settings = AppSettings.Load();

            RequestedThemeVariant = settings.DarkMode
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;

            // Server URL set in settings → use the live server (index + packs).
            // No server configured yet → mock repository so the app stays usable.
            IModRepository repository;
            if (string.IsNullOrWhiteSpace(settings.ServerUrl))
            {
                repository = new MockModRepository();
            }
            else
            {
                var baseUrl = settings.ServerUrl.TrimEnd('/');
                repository = new GitHubModRepository(http,
                    $"{baseUrl}/api/index",
                    $"{baseUrl}/api/packs",
                    () => settings.ApiToken);
            }

            var viewModel = new MainWindowViewModel(
                new SteamGameLocator(),
                new BepInExManager(http),
                repository,
                new InstallManager(),
                new SteamGameLauncher(),
                new UploadViewModel(settings),
                settings);

            var window = new MainWindow { DataContext = viewModel };
            if (settings.IsMaximized)
                window.WindowState = WindowState.Maximized;
            window.Closing += (_, _) =>
            {
                // After a full uninstall, saving would recreate the data folder.
                if (MainWindowViewModel.UninstallCompleted) return;
                settings.IsMaximized = window.WindowState == WindowState.Maximized;
                settings.Save();
            };

            desktop.MainWindow = window;
            _ = viewModel.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
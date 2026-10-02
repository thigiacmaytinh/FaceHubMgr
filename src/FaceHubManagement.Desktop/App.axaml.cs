using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FaceHubManagement.Desktop.Diagnostics;
using FaceHubManagement.Desktop.Services;
using FaceHubManagement.Desktop.ViewModels;
using FaceHubManagement.Desktop.Views;

namespace FaceHubManagement.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DesktopDiagnostics.ApplyFontResources(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DesktopDiagnostics.InstallDispatcherHandlers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsStore = new SettingsStore();
            DesktopSettings settings = settingsStore.Load();
            // Theme selection is intentionally disabled; Avalonia follows the system/default theme.
            // ThemeService.Apply(settings.Theme);
            var viewModel = new MainWindowViewModel(settingsStore, settings);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

using Microsoft.Extensions.DependencyInjection;
using PhotoViewerV3.Folders;
using PhotoViewerV3.Libraries;
using PhotoViewerV3.Settings;
using Wpf.Ui.Abstractions;

namespace PhotoViewerV3.Shell;

/// <summary>
/// Wpf-seitige Registrierungen der Shell: Navigation, Dialoge, Statusleiste, Theme sowie die Seiten
/// Bibliothek / Ordner / Einstellungen mit ihren ViewModels. Wird in App.xaml.cs mit einer Zeile aufgerufen.
/// Seiten sind Singletons — der NavigationPageProvider cached sie zusätzlich je Typ.
/// </summary>
public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        // Shell-Infrastruktur
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<INavigationViewPageProvider, NavigationPageProvider>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppRestartService, AppRestartService>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<ShellViewModel>();

        // Seiten + ViewModels
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<LibraryPage>();
        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<FolderTreePage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();

        return services;
    }
}

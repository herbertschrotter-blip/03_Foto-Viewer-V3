using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Settings.Info;

namespace PhotoViewerV3.Settings; // wie die übrigen Einstellungs-Module ohne zusätzliches using in App.xaml.cs

/// <summary>DI-Registrierung von Einstellungen › Info (ADR-0042); eine Zeile in <c>App.ConfigureServices</c>: <c>services.AddSettingsInfo();</c>.</summary>
public static class SettingsInfoServiceCollectionExtensions
{
    public static IServiceCollection AddSettingsInfo(this IServiceCollection services)
    {
        services.TryAddSingleton<IInfoActions, InfoActions>();
        services.TryAddSingleton<InfoViewModel>();
        return services;
    }
}

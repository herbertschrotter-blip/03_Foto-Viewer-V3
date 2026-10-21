using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Settings.FaceData;

/// <summary>Registrierung (eine Zeile in <c>App.ConfigureServices</c>).</summary>
public static class FaceDataSettingsServiceCollectionExtensions
{
    public static IServiceCollection AddFaceDataSettings(this IServiceCollection services)
    {
        services.TryAddSingleton<FaceDataSettingsViewModel>();
        return services;
    }
}

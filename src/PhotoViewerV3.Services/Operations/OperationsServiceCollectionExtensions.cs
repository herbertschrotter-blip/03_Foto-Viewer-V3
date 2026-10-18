using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Operations;

/// <summary>
/// DI-Registrierung des Operations-Moduls (Datei-Operationen + Papierkorb). Wird in
/// <c>AddPhotoViewerServices</c> mit einer Zeile aufgerufen. TryAdd, damit Tests oder die App
/// eigene Implementierungen (z. B. Fake-Papierkorb) vorab registrieren können.
/// </summary>
public static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddOperations(this IServiceCollection services)
    {
        services.TryAddSingleton<IRecycleBin>(_ => OperatingSystem.IsWindows()
            ? new WindowsRecycleBin()
            : throw new PlatformNotSupportedException("Der Papierkorb wird nur unter Windows unterstützt."));
        services.TryAddSingleton<IFileOperationService, FileOperationService>();
        return services;
    }
}

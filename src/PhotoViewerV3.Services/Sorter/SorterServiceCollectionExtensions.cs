using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// DI-Registrierung des Sorter-Moduls (File Sorter + Flatten &amp; Move + Profil-Store). Wird in
/// <c>AddPhotoViewerServices</c> mit einer Zeile aufgerufen. TryAdd, damit Tests eigene
/// Implementierungen vorab registrieren können. Benötigt <c>AddOperations()</c> (IFileOperationService)
/// und das Medienformat-Register.
/// </summary>
public static class SorterServiceCollectionExtensions
{
    public static IServiceCollection AddSorter(this IServiceCollection services)
    {
        services.TryAddSingleton<SorterCatalog>();
        services.TryAddSingleton<SorterRunLog>();
        services.TryAddSingleton<SorterMoveExecutor>();
        services.TryAddSingleton<ISorterProfileStore, SorterProfileStore>();
        services.TryAddSingleton<IFileSorterService, FileSorterService>();
        services.TryAddSingleton<IFlattenService, FlattenService>();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Gallery.Services;
using Lumora.Sorter.Services;
using Lumora.Sorter.ViewModels;

namespace Lumora.Sorter;

/// <summary>
/// DI-Registrierung des Sorter-UI-Moduls (eine Zeile in <c>App.ConfigureServices</c>:
/// <c>services.AddSorterUi();</c>). Seite und ViewModels sind Singletons, damit Analyse-Ergebnis und
/// Eingaben beim Wechsel zwischen den Bereichen erhalten bleiben; MainWindow löst die Seite aus dem
/// Container auf (der Container ist der Cache). Dialog-/Ordnerauswahl-Dienste kommen aus dem
/// Galerie-Modul (TryAdd, falls <c>AddGallery()</c> nicht vorher läuft).
/// </summary>
public static class SorterUiServiceCollectionExtensions
{
    public static IServiceCollection AddSorterUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IGalleryDialogService, GalleryDialogService>();
        services.TryAddSingleton<IFolderPickerService, FolderPickerService>();
        services.TryAddSingleton<ISorterFileDialogService, SorterFileDialogService>();
        services.TryAddSingleton<SorterLibraryLocator>();
        services.TryAddSingleton<FileSorterViewModel>();
        services.TryAddSingleton<FlattenViewModel>();
        services.TryAddSingleton<SorterPageViewModel>();
        services.TryAddSingleton<SorterPage>();
        return services;
    }
}

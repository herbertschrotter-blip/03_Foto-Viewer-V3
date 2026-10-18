using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PhotoViewerV3.Libraries;

/// <summary>
/// DI-Registrierung des Bibliotheks-Moduls (Wpf/Library): der Drei-Knopf-Dialog für den Mass-Missing-Guard.
/// Wird mit einer Zeile aus <c>AddShell</c> bzw. <c>App.xaml.cs</c> aufgerufen; solange das nicht geschieht,
/// erzeugt <see cref="LibraryViewModel"/> den Dialogdienst selbst (optionaler Konstruktorparameter).
/// </summary>
public static class LibraryModuleServiceCollectionExtensions
{
    public static IServiceCollection AddLibraryModule(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibraryDialogService, LibraryDialogService>();
        return services;
    }
}

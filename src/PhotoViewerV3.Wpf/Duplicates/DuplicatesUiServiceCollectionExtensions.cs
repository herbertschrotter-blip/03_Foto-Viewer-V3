using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Duplicates;

/// <summary>Öffnet die Seite „Doppelte“ von außen (Statusleisten-Hinweis „N Dateien im Eingang … — anzeigen“).</summary>
public interface IDuplicatesPageNavigator
{
    /// <summary>Seite „Doppelte“, exakt gleich, nur Eingang ↔ Bibliothek.</summary>
    void ShowInboxDuplicates();
}

/// <inheritdoc cref="IDuplicatesPageNavigator"/>
public sealed class DuplicatesPageNavigator(IServiceProvider services, INavigationService navigation) : IDuplicatesPageNavigator
{
    public void ShowInboxDuplicates()
    {
        services.GetRequiredService<DuplicatesViewModel>().ShowInboxDuplicates();
        navigation.NavigateTo(NavigationTags.Duplicates);
    }
}

/// <summary>
/// Seite „Doppelte“ (ADR-0053): Vorschaubilder, ViewModel, Seite, Navigator für den Statusleisten-Hinweis. Eine Zeile in
/// <c>App.ConfigureServices</c>: <c>services.AddDuplicatesUi();</c> — nach <c>AddGallery()</c> (Dialog-Dienst) und
/// <c>AddFacesUi()</c> (WIC-Bild-Lader für die Bild-Fingerabdrücke).
/// </summary>
public static class DuplicatesUiServiceCollectionExtensions
{
    public static IServiceCollection AddDuplicatesUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IDuplicateImageSource, DuplicateImageProvider>();
        services.TryAddSingleton<DuplicatesViewModel>();
        services.TryAddSingleton<DuplicatesPage>();
        services.TryAddSingleton<IDuplicatesPageNavigator, DuplicatesPageNavigator>();
        return services;
    }
}

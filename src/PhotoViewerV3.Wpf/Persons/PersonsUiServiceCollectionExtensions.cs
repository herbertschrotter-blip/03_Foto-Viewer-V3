using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Persons.ViewModels;
using PhotoViewerV3.Services.Faces.Crops;

namespace PhotoViewerV3.Persons;

/// <summary>
/// Seite „Personen“ (ADR-0037): WIC-Renderer der Gesichts-Ausschnitte, Bild-Lieferung, Dialoge, ViewModel und Seite.
/// Den Zuordnungsdienst registriert <c>AddPhotoViewerServices</c> (<c>AddFaceRecognition</c>). Eine Zeile in
/// <c>App.ConfigureServices</c>: <c>services.AddPersonsUi();</c> — nach <c>SmokeTestRunner.ConfigureServices</c>, damit der
/// Rauchtest seine Dialoge vorher eintragen kann (TryAdd).
/// </summary>
public static class PersonsUiServiceCollectionExtensions
{
    public static IServiceCollection AddPersonsUi(this IServiceCollection services)
    {
        services.TryAddSingleton<IFaceCropRenderer, WicFaceCropRenderer>();
        services.TryAddSingleton<FaceImageProvider>();
        services.TryAddSingleton<IPersonsDialogService, PersonsDialogService>();
        services.TryAddSingleton<PersonsViewModel>();
        services.TryAddSingleton<PersonsPage>();
        return services;
    }
}

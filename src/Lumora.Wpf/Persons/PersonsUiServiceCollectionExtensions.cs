using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces.Crops;

namespace Lumora.Persons;

/// <summary>
/// Seite „Personen“ (ADR-0037): WIC-Renderer der Gesichts-Ausschnitte, Bild-Lieferung, Dialoge, ViewModel und Seite.
/// Den Zuordnungsdienst registriert <c>AddLumoraServices</c> (<c>AddFaceRecognition</c>). Eine Zeile in
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

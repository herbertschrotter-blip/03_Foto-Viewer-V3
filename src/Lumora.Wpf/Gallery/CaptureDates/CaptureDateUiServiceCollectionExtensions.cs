using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumora.Gallery.CaptureDates;

/// <summary>
/// DI der Oberfläche zum Aufnahmedatum (ADR-0046): Dialog „Datum setzen…“ (per TryAdd, damit der Rauchtest seinen Fake
/// vorher setzen kann) und <see cref="CaptureDateActions"/> für Galerie und Ordnerbaum. Aufruf in App.xaml.cs.
/// </summary>
public static class CaptureDateUiServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureDateUi(this IServiceCollection services)
    {
        services.TryAddSingleton<ICaptureDateDialogService, CaptureDateDialogService>();
        services.TryAddSingleton<CaptureDateActions>();
        return services;
    }
}

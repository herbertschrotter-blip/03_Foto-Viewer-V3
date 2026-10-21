using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Services.Library;

namespace Lumora.Places;

/// <summary>
/// Bibliotheks-Bereich der Karte (harte Regel „Bibliotheks-Trennung“, ADR-0048): die Bibliotheken, deren Fotos die Seite
/// „Orte“ zeigt, zählt und verortet — die aktive Bibliothek bzw. in der Ansicht „Alle verknüpften“ alle Bibliotheken ihrer
/// Gruppe. Implementierung seit dem Zusammenbau 0.2.56: <see cref="LibraryScopePlacesScope"/>.
/// </summary>
public interface IPlacesScope
{
    Task<IReadOnlyList<int>> GetLibraryIdsAsync(CancellationToken ct);
}

/// <summary>
/// Bereich aus <see cref="ILibraryScope"/> (frisch je Aufruf, leer ohne Bibliothek). Die Seite lädt bei
/// <c>ActiveLibraryChangedMessage</c> neu — die senden Bibliothekswechsel, „Alle verknüpften“, Verknüpfen und Lösen.
/// </summary>
public sealed class LibraryScopePlacesScope : IPlacesScope
{
    private readonly ILibraryScope _scope;

    public LibraryScopePlacesScope(ILibraryScope scope)
    {
        _scope = scope;
    }

    public async Task<IReadOnlyList<int>> GetLibraryIdsAsync(CancellationToken ct)
    {
        var scope = await _scope.GetCurrentAsync(ct).ConfigureAwait(false);
        return scope.HasLibrary ? scope.LibraryIds : [];
    }
}

/// <summary>
/// Datenschutz-Merkmal für die Karte: läuft gerade eine versteckte Bibliothek (eigener Katalog im verschlüsselten
/// Container)? Dann zeigt die Seite den Hinweis, dass Kartenkacheln aus dem Internet kommen.
/// Seit dem Zusammenbau mit 0.2.52: <see cref="CatalogMapPrivacyContext"/> liest
/// <c>ICatalogContext.IsHidden</c> der Sitzung.
/// </summary>
public interface IMapPrivacyContext
{
    bool IsHiddenCatalog { get; }
}

/// <summary>Merkmal aus dem Katalog der Sitzung (<see cref="Lumora.Services.Infrastructure.ICatalogContext.IsHidden"/>, ADR-0049).</summary>
public sealed class CatalogMapPrivacyContext : IMapPrivacyContext
{
    private readonly Lumora.Services.Infrastructure.ICatalogContext _catalog;

    public CatalogMapPrivacyContext(Lumora.Services.Infrastructure.ICatalogContext catalog) => _catalog = catalog;

    public bool IsHiddenCatalog => _catalog.IsHidden;
}

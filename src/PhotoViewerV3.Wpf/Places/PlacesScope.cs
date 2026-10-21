using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Places;

/// <summary>
/// Bibliotheks-Bereich der Karte (harte Regel „Bibliotheks-Trennung“): die Bibliotheken, deren Fotos die Seite „Orte“
/// zeigt, zählt und verortet. Heute = die aktive Bibliothek.
/// ZUSAMMENBAU mit Agent T: <see cref="ActiveLibraryPlacesScope"/> auf dessen Bereichs-Logik umstellen (aktive
/// Bibliothek bzw. Verknüpfungsgruppe in der Ansicht „Alle verknüpften“).
/// </summary>
public interface IPlacesScope
{
    Task<IReadOnlyList<int>> GetLibraryIdsAsync(CancellationToken ct);
}

/// <summary>Bereich = die aktive Bibliothek (leer ohne Bibliothek).</summary>
public sealed class ActiveLibraryPlacesScope : IPlacesScope
{
    private readonly ILibraryService _libraries;

    public ActiveLibraryPlacesScope(ILibraryService libraries)
    {
        _libraries = libraries;
    }

    public async Task<IReadOnlyList<int>> GetLibraryIdsAsync(CancellationToken ct)
    {
        var active = await _libraries.GetActiveAsync(ct).ConfigureAwait(false);
        return active is null ? [] : [active.Id];
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

/// <summary>Merkmal aus dem Katalog der Sitzung (<see cref="PhotoViewerV3.Services.Infrastructure.ICatalogContext.IsHidden"/>, ADR-0049).</summary>
public sealed class CatalogMapPrivacyContext : IMapPrivacyContext
{
    private readonly PhotoViewerV3.Services.Infrastructure.ICatalogContext _catalog;

    public CatalogMapPrivacyContext(PhotoViewerV3.Services.Infrastructure.ICatalogContext catalog) => _catalog = catalog;

    public bool IsHiddenCatalog => _catalog.IsHidden;
}

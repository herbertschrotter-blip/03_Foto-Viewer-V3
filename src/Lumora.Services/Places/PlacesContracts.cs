namespace Lumora.Services.Places;

// Gemeinsamer Vertrag „Orte“ zwischen Daten (Agent V, 0.2.53) und Karte (Agent W) — Namen und Signaturen exakt so
// lassen, die Karte baut dagegen (ADR-0050). Zusätzliches steht in eigenen Typen (PlacesModels.cs).

/// <summary>Eine verortete Datei (WGS84, Dezimalgrad).</summary>
public sealed record GeoPoint(int FileId, double Latitude, double Longitude);

/// <summary>
/// Kartenausschnitt in Dezimalgrad. <see cref="West"/> &gt; <see cref="East"/> bedeutet: der Ausschnitt überquert die
/// Datumsgrenze (180°).
/// </summary>
public sealed record GeoBounds(double South, double West, double North, double East);

/// <summary>Ort aus den GeoNames-Daten: Id (GeoNames-Id), Name (deutsch, sonst GeoNames), Region, Land.</summary>
public sealed record PlaceInfo(int PlaceId, string Name, string? Region, string CountryCode, string Country);

/// <summary>Lesen der Koordinaten für die Karte — immer nur innerhalb der übergebenen Bibliotheken.</summary>
public interface IGeoQueryService
{
    /// <summary>
    /// Alle verorteten Dateien der angegebenen Bibliotheken (Bereich = aktive Bibliothek bzw. Verknüpfungsgruppe),
    /// optional eingeschränkt auf einen Kartenausschnitt; zusätzliche Galerie-Filter folgen später.
    /// </summary>
    Task<IReadOnlyList<GeoPoint>> GetPointsAsync(IReadOnlyCollection<int> libraryIds, GeoBounds? bounds, CancellationToken ct);

    /// <summary>Begrenzungsrahmen aller verorteten Dateien der Bibliotheken (null, wenn keine).</summary>
    Task<GeoBounds?> GetExtentAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct);
}

/// <summary>Koordinaten setzen und entfernen — Katalog und Datei (EXIF bzw. XMP), protokolliert und rückgängig machbar.</summary>
public interface IGeoTagService
{
    /// <summary>Koordinaten für Dateien setzen: Katalog + EXIF/XMP schreiben (protokolliert, rückgängig machbar).</summary>
    Task<GeoTagResult> SetLocationAsync(IReadOnlyCollection<int> fileIds, double latitude, double longitude, bool keepModifiedTime, CancellationToken ct);

    /// <summary>Koordinaten entfernen (nur Katalog + geschriebene Daten der App; Original-EXIF bleibt über Rückgängig erhalten).</summary>
    Task<GeoTagResult> ClearLocationAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);
}

/// <summary>
/// Ergebnis von <see cref="IGeoTagService"/>: <see cref="Written"/> = Datei (EXIF bzw. XMP) und Katalog geschrieben,
/// <see cref="CatalogOnly"/> = nur der Katalog (Videos, Datei ohne Änderungsbedarf), <see cref="Failed"/> = nicht
/// geändert (Gründe in <see cref="Errors"/>, „Name: Grund“).
/// </summary>
public sealed record GeoTagResult(int Written, int CatalogOnly, int Failed, IReadOnlyList<string> Errors);

/// <summary>Ortsnamen offline (GeoNames, eingebettet).</summary>
public interface IReverseGeocoder
{
    /// <summary>Nächster Ort (offline, GeoNames) oder null.</summary>
    PlaceInfo? Nearest(double latitude, double longitude);

    /// <summary>Ortssuche nach Namen (offline), für „Ort suchen“ beim Verorten.</summary>
    IReadOnlyList<(PlaceInfo Place, double Latitude, double Longitude)> Search(string text, int max);
}

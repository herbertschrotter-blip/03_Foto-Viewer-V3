// =====================================================================================================================
// PLATZHALTER bis zum Zusammenbau mit Agent V (Orte: Daten). Diese Typen gehören laut gemeinsamem Vertrag
// („Orte: gemeinsamer Vertrag zwischen Agent V und Agent W“) zu Agent V und stehen hier nur, damit die Karte (Agent W)
// schon baut und testbar ist. Signaturen exakt wie im Vertrag.
// BEIM ZUSAMMENBAU: diese Datei löschen (V liefert dieselben Typen im selben Namespace). Ebenso den Ordner
// Services/Places/Placeholders und die Zeile `services.AddPlacesPlaceholders();` in PlacesUiServiceCollectionExtensions
// (V registriert die echten Dienste; die Platzhalter sind TryAdd und würden ohnehin nicht greifen).
// Ausnahme: IMapPicker — im Vertrag nur erwähnt („Agent V legt das Interface an“), Signatur hier von Agent W gewählt;
// falls V eine andere Signatur anlegt, MapPickerService (Wpf/Places/Picker) daran anpassen.
// =====================================================================================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoViewerV3.Services.Places;

public sealed record GeoPoint(int FileId, double Latitude, double Longitude);

public sealed record GeoBounds(double South, double West, double North, double East);

public sealed record PlaceInfo(int PlaceId, string Name, string? Region, string CountryCode, string Country);

public interface IGeoQueryService
{
    /// Alle verorteten Dateien der angegebenen Bibliotheken (Bereich = aktive Bibliothek bzw. Verknüpfungsgruppe),
    /// optional eingeschränkt auf einen Kartenausschnitt; zusätzliche Galerie-Filter folgen später.
    Task<IReadOnlyList<GeoPoint>> GetPointsAsync(IReadOnlyCollection<int> libraryIds, GeoBounds? bounds, CancellationToken ct);

    /// Begrenzungsrahmen aller verorteten Dateien der Bibliotheken (null, wenn keine).
    Task<GeoBounds?> GetExtentAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct);
}

public interface IGeoTagService
{
    /// Koordinaten für Dateien setzen: Katalog + EXIF/XMP schreiben (protokolliert, rückgängig machbar).
    Task<GeoTagResult> SetLocationAsync(IReadOnlyCollection<int> fileIds, double latitude, double longitude, bool keepModifiedTime, CancellationToken ct);

    /// Koordinaten entfernen (nur Katalog + geschriebene Daten der App; Original-EXIF bleibt über Rückgängig erhalten).
    Task<GeoTagResult> ClearLocationAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);
}

public sealed record GeoTagResult(int Written, int CatalogOnly, int Failed, IReadOnlyList<string> Errors);

public interface IReverseGeocoder
{
    /// Nächster Ort (offline, GeoNames) oder null.
    PlaceInfo? Nearest(double latitude, double longitude);

    /// Ortssuche nach Namen (offline), für „Ort suchen“ beim Verorten.
    IReadOnlyList<(PlaceInfo Place, double Latitude, double Longitude)> Search(string text, int max);
}

/// <summary>
/// Kleines Kartenfenster zum Wählen eines Punkts (Dialog „Ort setzen…“ von Agent V). Implementierung: Agent W
/// (<c>PhotoViewerV3.Places.Picker.MapPickerService</c>). Signatur von Agent W gewählt (im Vertrag nicht festgelegt).
/// </summary>
public interface IMapPicker
{
    /// <summary>
    /// Zeigt die Karte (Start: übergebener Punkt bzw. Ausdehnung der verorteten Fotos) und liefert den gewählten Punkt,
    /// null bei Abbruch.
    /// </summary>
    Task<(double Latitude, double Longitude)?> PickAsync(double? initialLatitude, double? initialLongitude, string? title, CancellationToken ct);
}

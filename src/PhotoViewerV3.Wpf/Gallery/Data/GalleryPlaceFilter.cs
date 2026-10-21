using System.Collections.Generic;
using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Gallery.Data;

/// <summary>
/// Galerie-Filter „Ort“ (ADR-0050): Dateien an einem der gewählten Orte (Länder und Regionen sind beim Wählen in die
/// Orte des Bibliotheksbereichs aufgelöst) ODER ohne Koordinaten („ohne Ort“) ODER mit Koordinaten, aber ohne Ort in der
/// Nähe („ohne Ortsnamen“). UND-verknüpft mit allen anderen Filtern.
/// </summary>
/// <param name="PlaceIds">GeoNames-Ids (Files.PlaceId).</param>
/// <param name="IncludeWithoutLocation">Dateien ohne Koordinaten.</param>
/// <param name="IncludeWithoutPlaceName">Dateien mit Koordinaten ohne Ortsnamen (Meer, Wildnis).</param>
/// <param name="Label">Text für Chip und „Es gilt“-Zeile („Österreich oder Rom“).</param>
public sealed record GalleryPlaceFilter(IReadOnlyList<int> PlaceIds, bool IncludeWithoutLocation, bool IncludeWithoutPlaceName = false, string? Label = null)
{
    public bool IsActive => PlaceIds.Count > 0 || IncludeWithoutLocation || IncludeWithoutPlaceName;
}

/// <summary>
/// Filter „Kartenausschnitt“ (Vertrag mit der Karte, ADR-0050): die Dateien einer Kartengruppe (<paramref name="FileIds"/>)
/// bzw. alle verorteten Dateien im Ausschnitt (<paramref name="Bounds"/>, wenn keine Ids). Gesetzt über
/// <c>ShowFilesOnMapSelectionMessage</c>.
/// </summary>
public sealed record GalleryGeoFilter(GeoBounds? Bounds, IReadOnlyCollection<int>? FileIds)
{
    public bool IsActive => Bounds is not null || FileIds is { Count: > 0 };
}

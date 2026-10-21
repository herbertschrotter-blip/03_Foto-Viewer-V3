using PhotoViewerV3.Services.Places;

namespace PhotoViewerV3.Services.Messaging;

/// <summary>
/// Orte-Karte (ADR-0051) → Galerie: zeige genau diese Dateien einer Kartengruppe (bzw. eines Kartenausschnitts).
/// Absender: Agent W (Seite „Orte“, „In der Galerie zeigen“ bzw. Doppelklick auf eine Gruppe); Empfänger: die Galerie
/// (Agent V, Filterbaustein <c>GalleryGeoFilter(Bounds, FileIds)</c>). Die Seite „Orte“ navigiert danach selbst zur Galerie.
/// <paramref name="Bounds"/> = Begrenzungsrahmen der Dateien, <paramref name="FileIds"/> = die Dateien selbst (nur aus
/// dem aktuellen Bibliotheks-Bereich).
/// </summary>
public sealed record ShowFilesOnMapSelectionMessage(GeoBounds Bounds, IReadOnlyCollection<int> FileIds);

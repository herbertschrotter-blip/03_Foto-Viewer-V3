using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Tracks;

namespace Lumora.Services.Messaging;

// Ungenaue Orte und GPS-Spuren (ADR-0062).

/// <summary>Fortschritt der Genauigkeits-Prüfung (gedrosselt, Statusleiste).</summary>
public sealed record GeoAccuracyStatusMessage(GeoAccuracyStatus Status);

/// <summary>Genauigkeit von Dateien hat sich geändert (Prüfung, Sprung-Regel) — Zähler und Liste „Ungenau“ neu lesen.</summary>
public sealed record GeoAccuracyChangedMessage(IReadOnlyList<int> LibraryIds);

/// <summary>Fortschritt des Einlesens von GPS-Spuren (Statusleiste, Einstellungen › Orte).</summary>
public sealed record GpsTrackImportStatusMessage(GpsTrackImportStatus Status);

/// <summary>GPS-Spuren einer Gruppe eingelesen bzw. entfernt — Vorschläge, Zähler und Einstellungen neu lesen.</summary>
public sealed record GpsTracksChangedMessage(int GroupId);

/// <summary>
/// Eine Garmin-Uhr ist als Laufwerk (USB-Massenspeicher mit <c>GARMIN\Activity</c>) erschienen und hat Aktivitäten, die
/// noch nicht eingelesen sind — die Oberfläche fragt, ob sie eingelesen werden sollen.
/// </summary>
public sealed record GarminDeviceDetectedMessage(string ActivityFolder, int NewFileCount);

/// <summary>Zusammenfassung „Orte“ für die Statusleiste: geschätzte Positionen, Vorschläge (null = unbekannt), Aktivitäten.</summary>
public sealed record GeoPlacesSummaryMessage(int Estimated, int? Suggestions, int Activities);

/// <summary>Seite „Orte“ soll ein Foto im Bereich „Ungenau“ bzw. „Ohne Ort“ zeigen (Lightbox › „Auf der Karte zeigen“).</summary>
public sealed record ShowFileSuggestionOnMapMessage(int FileId);

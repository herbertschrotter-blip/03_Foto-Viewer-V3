using Lumora.Domain.Enums;
using Lumora.Services.Metadata;

namespace Lumora.Services.Places;

/// <summary>Koordinate und Ort einer Datei, wie der Katalog sie kennt (Lightbox, Dialog „Ort setzen“).</summary>
/// <param name="Source">null = noch nicht gelesen.</param>
public sealed record FileGeoInfo(int FileId, double? Latitude, double? Longitude, GeoSource? Source, PlaceInfo? Place)
{
    public bool HasCoordinates => Latitude is not null && Longitude is not null;

    /// <summary>„Wien, Österreich (48,2082° N, 16,3738° O)“, „48,2082° N, 16,3738° O“ ohne Ortsnamen, sonst null.</summary>
    public string? Text => Latitude is { } lat && Longitude is { } lon ? PlaceText.Describe(Place, lat, lon) : null;

    /// <summary>Herkunft in Worten („EXIF“, „Video“, „manuell“).</summary>
    public string? SourceText => Source switch
    {
        GeoSource.Exif => "EXIF",
        GeoSource.Video => "Video",
        GeoSource.Manual => "manuell",
        GeoSource.Track => "aus GPS-Spur", // ADR-0062
        GeoSource.Neighbors => "aus Nachbarfotos", // ADR-0062
        _ => null,
    };
}

/// <summary>Anzeigetexte für Orte.</summary>
public static class PlaceText
{
    /// <summary>„Wien, Österreich“ bzw. nur das Land, wenn der Ortsname fehlt; null ohne Ort.</summary>
    public static string? Short(PlaceInfo? place)
        => place is null ? null : $"{place.Name}, {place.Country}";

    /// <summary>„Wien, Österreich (48,2082° N, 16,3738° O)“.</summary>
    public static string Describe(PlaceInfo? place, double latitude, double longitude)
        => place is null ? GeoCoordinate.Format(latitude, longitude) : $"{Short(place)} ({GeoCoordinate.Format(latitude, longitude)})";
}

/// <summary>Anzahl Dateien je Ort in einem Bibliotheksbereich (Galerie-Filter „Ort“).</summary>
/// <param name="Places">PlaceId → Anzahl.</param>
/// <param name="WithoutLocation">Dateien ohne Koordinaten (Filter „ohne Ort“).</param>
/// <param name="WithoutPlaceName">Dateien mit Koordinaten, aber ohne Ort in der Nähe (Meer, Wildnis).</param>
public sealed record PlaceCountSummary(IReadOnlyList<(int PlaceId, int Count)> Places, int WithoutLocation, int WithoutPlaceName);

/// <summary>Zuletzt verwendeter Ort („Ort von anderem Foto übernehmen“).</summary>
public sealed record RecentLocation(double Latitude, double Longitude, PlaceInfo? Place, string Label);

/// <summary>Koordinate und Ort je Datei (Galerie-Seite: Listenspalte, Kachel-Tooltip).</summary>
public sealed record FilePlaceRow(int FileId, double Latitude, double Longitude, int? PlaceId);

/// <summary>Zusätzliche Lese-Abfragen zu Orten — immer nur innerhalb der übergebenen Bibliotheken.</summary>
public interface IGeoCatalogReader
{
    Task<FileGeoInfo?> GetAsync(int fileId, CancellationToken ct = default);

    Task<PlaceCountSummary> GetPlaceCountsAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct = default);

    /// <summary>Koordinaten der Dateien (nur verortete), für die Galerie-Seite.</summary>
    Task<IReadOnlyList<FilePlaceRow>> GetForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default);

    /// <summary>Zuletzt in Lumora gesetzte Orte, aufgefüllt mit den häufigsten Orten des Bereichs.</summary>
    Task<IReadOnlyList<RecentLocation>> GetRecentLocationsAsync(IReadOnlyCollection<int> libraryIds, int max, CancellationToken ct = default);
}

/// <summary>Rückgängig für „Ort setzen/entfernen“ (Galerie-„Rückgängig“, gemeinsame Reihenfolge über die Batch-Id).</summary>
public interface IGeoTagUndoService
{
    /// <summary>Jüngster noch nicht zurückgenommener Batch „Ort setzen“ oder null.</summary>
    Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default);

    /// <summary>Nimmt den Batch zurück (Katalog + Datei-Bytes); null, wenn er nicht (mehr) rückgängig zu machen ist.</summary>
    Task<GeoTagUndoResult?> UndoAsync(int batchId, CancellationToken ct = default);
}

/// <summary>Ergebnis von Rückgängig: zurückgesetzt, übersprungen (Datei inzwischen verändert/fehlt), Fehler.</summary>
public sealed record GeoTagUndoResult(int BatchId, int Restored, int Failed, IReadOnlyList<string> Errors, string Description);

/// <summary>Zustand des Nachtragens der Koordinaten (Statusleiste).</summary>
public sealed record GeoBackfillStatus(bool IsRunning, int Done, int Total, int Skipped = 0, string? Message = null)
{
    public static GeoBackfillStatus Idle { get; } = new(false, 0, 0);
}

/// <summary>Ergebnis eines Durchgangs des Nachtragens.</summary>
public sealed record GeoBackfillReport(int Read, int WithLocation, int Skipped);

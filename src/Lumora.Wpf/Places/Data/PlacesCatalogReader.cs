using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Services.Database;

namespace Lumora.Places.Data;

/// <summary>Bild oder Video der Bibliothek mit Rangfolge für das „beste“ Foto einer Kartengruppe.</summary>
/// <param name="SortKey">Aufnahmedatum, sonst Änderungsdatum (Unix-Sekunden) — größer = neuer.</param>
public readonly record struct PlacesMediaRow(int FileId, byte Rating, long SortKey);

/// <summary>Angaben zu einer Datei für Vorschaubild, Lightbox und Seitenleiste.</summary>
public sealed record PlacesFileInfo(
    int FileId,
    int LibraryId,
    string FolderPath,
    string Name,
    MediaType MediaType,
    bool IsFavorite,
    byte? Rating,
    long Size,
    DateTime ModifiedAtUtc)
{
    public string Path => System.IO.Path.Combine(FolderPath, Name);
}

/// <summary>
/// Katalog-Lesepfad der Seite „Orte“ (ADR-0051) — nur, was der Vertrag mit Agent V (<c>IGeoQueryService</c>) nicht
/// liefert: Bewertung/Datum aller Medien (bestes Foto einer Gruppe, Reihenfolge „Ohne Ort“) und Dateiangaben für
/// Vorschaubilder und Lightbox. Immer auf die übergebenen Bibliotheken beschränkt (Bibliotheks-Trennung).
/// </summary>
public interface IPlacesCatalogReader
{
    /// <summary>Alle vorhandenen Bilder und Videos der Bibliotheken, neueste zuerst (Aufnahme-, sonst Änderungsdatum).</summary>
    Task<IReadOnlyList<PlacesMediaRow>> GetMediaAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct);

    /// <summary>
    /// Wie oben, aber nur die Treffer des gemeinsamen Galerie-Filters (ADR-0055; null = ohne Filter). Dieselbe
    /// Klausel-Erzeugung wie die Galerie (<see cref="GalleryPageSource.CreateFilteredCommand"/>); der Bereich kommt immer
    /// aus <paramref name="libraryIds"/>.
    /// </summary>
    Task<IReadOnlyList<PlacesMediaRow>> GetMediaAsync(IReadOnlyCollection<int> libraryIds, GalleryQuery? filter, CancellationToken ct);

    /// <summary>Angaben zu den Dateien (nur vorhandene, nur aus den Bibliotheken); Reihenfolge beliebig.</summary>
    Task<IReadOnlyList<PlacesFileInfo>> GetFilesAsync(IReadOnlyCollection<int> libraryIds, IReadOnlyCollection<int> fileIds, CancellationToken ct);
}

/// <summary>Raw-SQLite-Implementierung (eigene kurzlebige Verbindung je Abfrage, auf dem Thread-Pool).</summary>
public sealed class PlacesCatalogReader : IPlacesCatalogReader
{
    private const int ChunkSize = 500;
    private readonly ILibraryContextFactory _db;

    public PlacesCatalogReader(ILibraryContextFactory db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<PlacesMediaRow>> GetMediaAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct)
        => Task.Run<IReadOnlyList<PlacesMediaRow>>(() =>
        {
            if (libraryIds.Count == 0)
            {
                return [];
            }

            using var conn = _db.OpenSqliteConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT f.Id, f.Rating, COALESCE(f.CapturedAt, f.ModifiedAtUtc) FROM Files f " +
                $"WHERE f.LibraryId IN ({IdList(libraryIds)}) AND f.Missing = 0 AND f.MediaType IN ({(int)MediaType.Image}, {(int)MediaType.Video}) " +
                "ORDER BY COALESCE(f.CapturedAt, f.ModifiedAtUtc) DESC, f.Id DESC";
            var result = new List<PlacesMediaRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var rating = reader.IsDBNull(1) ? (byte)0 : (byte)reader.GetInt32(1);
                result.Add(new PlacesMediaRow(reader.GetInt32(0), rating, reader.GetInt64(2)));
            }

            return result;
        }, ct);

    public Task<IReadOnlyList<PlacesMediaRow>> GetMediaAsync(IReadOnlyCollection<int> libraryIds, GalleryQuery? filter, CancellationToken ct)
    {
        if (filter is null)
        {
            return GetMediaAsync(libraryIds, ct);
        }

        return Task.Run<IReadOnlyList<PlacesMediaRow>>(() =>
        {
            var ids = libraryIds.Distinct().ToList();
            if (ids.Count == 0)
            {
                return [];
            }

            // Bereich der Karte (ILibraryScope) statt dem der Galerie-Abfrage — beide sind gleich, außer kurz beim Wechsel
            var scoped = filter with { LibraryId = ids[0], LibraryIds = ids.Count > 1 ? ids : null };
            using var conn = _db.OpenSqliteConnection();
            using var cmd = GalleryPageSource.CreateFilteredCommand(
                conn,
                scoped,
                "f.Id, f.Rating, COALESCE(f.CapturedAt, f.ModifiedAtUtc)",
                $"AND f.Missing = 0 AND f.MediaType IN ({(int)MediaType.Image}, {(int)MediaType.Video})\n" +
                "ORDER BY COALESCE(f.CapturedAt, f.ModifiedAtUtc) DESC, f.Id DESC");
            var result = new List<PlacesMediaRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var rating = reader.IsDBNull(1) ? (byte)0 : (byte)reader.GetInt32(1);
                result.Add(new PlacesMediaRow(reader.GetInt32(0), rating, reader.GetInt64(2)));
            }

            return result;
        }, ct);
    }

    public Task<IReadOnlyList<PlacesFileInfo>> GetFilesAsync(IReadOnlyCollection<int> libraryIds, IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => Task.Run<IReadOnlyList<PlacesFileInfo>>(() =>
        {
            if (libraryIds.Count == 0 || fileIds.Count == 0)
            {
                return [];
            }

            using var conn = _db.OpenSqliteConnection();
            var result = new List<PlacesFileInfo>(fileIds.Count);
            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT f.Id, f.LibraryId, fo.Path, f.Name, f.MediaType, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc " +
                    "FROM Files f JOIN Folders fo ON fo.Id = f.FolderId " +
                    $"WHERE f.Id IN ({string.Join(",", chunk)}) AND f.LibraryId IN ({IdList(libraryIds)}) AND f.Missing = 0";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new PlacesFileInfo(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        (MediaType)reader.GetInt32(4),
                        reader.GetInt32(5) == 1,
                        reader.IsDBNull(6) ? null : (byte)reader.GetInt32(6),
                        reader.GetInt64(7),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).UtcDateTime));
                }
            }

            return result;
        }, ct);

    private static string IdList(IEnumerable<int> ids) => string.Join(",", ids.Distinct());
}

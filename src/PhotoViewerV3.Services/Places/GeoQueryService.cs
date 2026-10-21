using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Places;

/// <summary>
/// Lese-Abfragen zu Orten (ADR-0050) über Raw-SQLite — jede Abfrage nur innerhalb der übergebenen Bibliotheken
/// (Bibliotheks-Trennung). Punkte und Ausdehnung laufen über <c>IX_Files_Library_Geo</c>, Ortszähler über
/// <c>IX_Files_Library_Place</c>.
/// </summary>
public sealed class GeoQueryService : IGeoQueryService, IGeoCatalogReader
{
    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly IPlaceLookup _places;
    private readonly IReverseGeocoder _geocoder;

    public GeoQueryService(ILibraryContextFactory factory, IPlaceLookup places, IReverseGeocoder geocoder)
    {
        _factory = factory;
        _places = places;
        _geocoder = geocoder;
    }

    public Task<IReadOnlyList<GeoPoint>> GetPointsAsync(IReadOnlyCollection<int> libraryIds, GeoBounds? bounds, CancellationToken ct)
        => RunAsync<IReadOnlyList<GeoPoint>>(connection =>
        {
            if (libraryIds.Count == 0)
            {
                return [];
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Id, Latitude, Longitude FROM Files WHERE LibraryId IN ({BindIds(cmd, libraryIds.Distinct().ToList(), "$lib")}) AND Latitude IS NOT NULL"
                              + BoundsClause(cmd, bounds, "Latitude", "Longitude") + " ORDER BY Id;";
            var result = new List<GeoPoint>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new GeoPoint(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2)));
            }

            return result;
        }, ct);

    public Task<GeoBounds?> GetExtentAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct)
        => RunAsync(connection =>
        {
            if (libraryIds.Count == 0)
            {
                return null;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MIN(Latitude), MIN(Longitude), MAX(Latitude), MAX(Longitude) FROM Files "
                              + $"WHERE LibraryId IN ({BindIds(cmd, libraryIds.Distinct().ToList(), "$lib")}) AND Latitude IS NOT NULL;";
            using var reader = cmd.ExecuteReader();
            return reader.Read() && !reader.IsDBNull(0)
                ? new GeoBounds(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3))
                : (GeoBounds?)null;
        }, ct);

    public Task<FileGeoInfo?> GetAsync(int fileId, CancellationToken ct = default)
        => RunAsync(connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Latitude, Longitude, GeoSource, PlaceId FROM Files WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            double? lat = reader.IsDBNull(0) ? null : reader.GetDouble(0);
            double? lon = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            GeoSource? source = reader.IsDBNull(2) ? null : (GeoSource)reader.GetInt32(2);
            var place = reader.IsDBNull(3) ? null : _places.GetPlace(reader.GetInt32(3));
            return new FileGeoInfo(fileId, lat, lon, source, place);
        }, ct);

    public Task<PlaceCountSummary> GetPlaceCountsAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct = default)
        => RunAsync(connection =>
        {
            if (libraryIds.Count == 0)
            {
                return new PlaceCountSummary([], 0, 0);
            }

            var ids = libraryIds.Distinct().ToList();
            var places = new List<(int, int)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT PlaceId, COUNT(*) FROM Files WHERE LibraryId IN ({BindIds(cmd, ids, "$lib")}) AND PlaceId IS NOT NULL GROUP BY PlaceId;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    places.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            int withoutLocation;
            int withoutName;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(CASE WHEN Latitude IS NULL THEN 1 ELSE 0 END), 0), "
                                  + "COALESCE(SUM(CASE WHEN Latitude IS NOT NULL AND PlaceId IS NULL THEN 1 ELSE 0 END), 0) "
                                  + $"FROM Files WHERE LibraryId IN ({BindIds(cmd, ids, "$lib")});";
                using var reader = cmd.ExecuteReader();
                reader.Read();
                withoutLocation = reader.GetInt32(0);
                withoutName = reader.GetInt32(1);
            }

            return new PlaceCountSummary(places, withoutLocation, withoutName);
        }, ct);

    public Task<IReadOnlyList<FilePlaceRow>> GetForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
        => RunAsync<IReadOnlyList<FilePlaceRow>>(connection =>
        {
            var result = new List<FilePlaceRow>();
            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT Id, Latitude, Longitude, PlaceId FROM Files WHERE Id IN ({BindIds(cmd, chunk, "$f")}) AND Latitude IS NOT NULL;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new FilePlaceRow(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
                }
            }

            return result;
        }, ct);

    public Task<IReadOnlyList<RecentLocation>> GetRecentLocationsAsync(IReadOnlyCollection<int> libraryIds, int max, CancellationToken ct = default)
        => RunAsync<IReadOnlyList<RecentLocation>>(connection =>
        {
            if (libraryIds.Count == 0 || max <= 0)
            {
                return [];
            }

            var ids = libraryIds.Distinct().ToList();
            var result = new List<RecentLocation>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // 1) Zuletzt in Lumora gesetzt (neueste Operation zuerst) — nur Dateien des Bereichs.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT u.NewLatitude, u.NewLongitude
                    FROM GeoTagUndo u
                    JOIN FileOperations o ON o.Id = u.FileOperationId
                    JOIN Files f ON f.Id = o.FileId
                    WHERE u.NewLatitude IS NOT NULL AND f.LibraryId IN ({BindIds(cmd, ids, "$lib")})
                    ORDER BY o.Id DESC
                    LIMIT 500;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && result.Count < max)
                {
                    Add(reader.GetDouble(0), reader.GetDouble(1), null);
                }
            }

            // 2) Aufgefüllt mit den häufigsten Orten des Bereichs (Koordinate des Ortes).
            if (result.Count < max)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    SELECT PlaceId, AVG(Latitude), AVG(Longitude), COUNT(*) AS N
                    FROM Files WHERE LibraryId IN ({BindIds(cmd, ids, "$lib")}) AND PlaceId IS NOT NULL
                    GROUP BY PlaceId ORDER BY N DESC LIMIT $max;
                    """;
                cmd.Parameters.AddWithValue("$max", max * 2);
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && result.Count < max)
                {
                    Add(reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(0));
                }
            }

            return result;

            void Add(double lat, double lon, int? placeId)
            {
                var place = placeId is { } p ? _places.GetPlace(p) : null;
                place ??= _geocoder.Nearest(lat, lon);
                var key = place is not null ? "p" + place.PlaceId.ToString(CultureInfo.InvariantCulture) : $"{lat:F4};{lon:F4}";
                if (!seen.Add(key))
                {
                    return;
                }

                var label = PlaceText.Short(place) ?? Metadata.GeoCoordinate.Format(lat, lon);
                result.Add(new RecentLocation(lat, lon, place, label));
            }
        }, ct);

    /// <summary>Bedingung für einen Kartenausschnitt (über die Datumsgrenze: West &gt; Ost) samt Parametern.</summary>
    internal static string BoundsClause(SqliteCommand cmd, GeoBounds? bounds, string latColumn, string lonColumn, string prefix = "$b")
    {
        if (bounds is null)
        {
            return string.Empty;
        }

        cmd.Parameters.AddWithValue(prefix + "s", bounds.South);
        cmd.Parameters.AddWithValue(prefix + "n", bounds.North);
        cmd.Parameters.AddWithValue(prefix + "w", bounds.West);
        cmd.Parameters.AddWithValue(prefix + "e", bounds.East);
        var lon = bounds.West <= bounds.East
            ? $"{lonColumn} BETWEEN {prefix}w AND {prefix}e"
            : $"({lonColumn} >= {prefix}w OR {lonColumn} <= {prefix}e)";
        return $" AND {latColumn} BETWEEN {prefix}s AND {prefix}n AND {lon}";
    }

    internal static string BindIds(SqliteCommand cmd, IReadOnlyList<int> ids, string prefix)
    {
        var sb = new StringBuilder(ids.Count * 6);
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var p = prefix + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(p);
            cmd.Parameters.AddWithValue(p, ids[i]);
        }

        return sb.ToString();
    }

    private Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
        => Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                return work(connection);
            },
            ct);
}

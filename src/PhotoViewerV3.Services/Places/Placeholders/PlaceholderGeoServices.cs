// =====================================================================================================================
// PLATZHALTER bis zum Zusammenbau mit Agent V (siehe PlacesContract.Placeholder.cs). Beim Zusammenbau den ganzen Ordner
// Services/Places/Placeholders löschen und in PlacesUiServiceCollectionExtensions die Zeile
// `services.AddPlacesPlaceholders();` entfernen — V registriert die echten Dienste (Katalog + EXIF/XMP, GeoNames).
// =====================================================================================================================

using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Services.Places.Placeholders;

/// <summary>
/// Platzhalter für <see cref="IGeoQueryService"/> und <see cref="IGeoTagService"/> bis zum Zusammenbau: Koordinaten nur im
/// Speicher dieser Sitzung (kein Katalog, keine Datei wird geschrieben). Die Bibliothek einer Datei kommt aus dem Katalog
/// (<c>Files.LibraryId</c>), damit die Bibliotheks-Trennung auch mit dem Platzhalter gilt.
/// </summary>
public sealed class InMemoryGeoCatalog : IGeoQueryService, IGeoTagService
{
    private readonly ILibraryContextFactory _db;
    private readonly IMessenger _messenger;
    private readonly ConcurrentDictionary<int, (double Lat, double Lon)> _points = new();

    public InMemoryGeoCatalog(ILibraryContextFactory db, IMessenger? messenger = null)
    {
        _db = db;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public async Task<IReadOnlyList<GeoPoint>> GetPointsAsync(IReadOnlyCollection<int> libraryIds, GeoBounds? bounds, CancellationToken ct)
    {
        var libraries = await LibraryOfAsync(_points.Keys.ToList(), ct).ConfigureAwait(false);
        return _points
            .Where(p => libraries.TryGetValue(p.Key, out var lib) && libraryIds.Contains(lib))
            .Where(p => bounds is null || (p.Value.Lat >= bounds.South && p.Value.Lat <= bounds.North && p.Value.Lon >= bounds.West && p.Value.Lon <= bounds.East))
            .Select(p => new GeoPoint(p.Key, p.Value.Lat, p.Value.Lon))
            .OrderBy(p => p.FileId)
            .ToList();
    }

    public async Task<GeoBounds?> GetExtentAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct)
    {
        var points = await GetPointsAsync(libraryIds, null, ct).ConfigureAwait(false);
        return points.Count == 0
            ? null
            : new GeoBounds(points.Min(p => p.Latitude), points.Min(p => p.Longitude), points.Max(p => p.Latitude), points.Max(p => p.Longitude));
    }

    public async Task<GeoTagResult> SetLocationAsync(IReadOnlyCollection<int> fileIds, double latitude, double longitude, bool keepModifiedTime, CancellationToken ct)
    {
        var libraries = await LibraryOfAsync(fileIds.ToList(), ct).ConfigureAwait(false);
        foreach (var id in fileIds.Where(libraries.ContainsKey))
        {
            _points[id] = (latitude, longitude);
        }

        Notify(libraries);
        var missing = fileIds.Count(id => !libraries.ContainsKey(id));
        return new GeoTagResult(0, fileIds.Count - missing, missing, missing == 0 ? [] : [$"{missing} Datei(en) nicht im Katalog"]);
    }

    public async Task<GeoTagResult> ClearLocationAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var libraries = await LibraryOfAsync(fileIds.ToList(), ct).ConfigureAwait(false);
        var removed = fileIds.Count(id => _points.TryRemove(id, out _));
        Notify(libraries);
        return new GeoTagResult(0, removed, 0, []);
    }

    private void Notify(IReadOnlyDictionary<int, int> libraries)
    {
        foreach (var group in libraries.GroupBy(p => p.Value))
        {
            _messenger.Send(new FilesChangedMessage(group.Key, FilesChangeKind.Metadata, group.Select(p => p.Key).ToList()));
        }
    }

    private async Task<IReadOnlyDictionary<int, int>> LibraryOfAsync(IReadOnlyList<int> fileIds, CancellationToken ct)
    {
        var result = new Dictionary<int, int>();
        if (fileIds.Count == 0)
        {
            return result;
        }

        await Task.Run(() =>
        {
            using var conn = _db.OpenSqliteConnection();
            foreach (var chunk in fileIds.Chunk(500))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT Id, LibraryId FROM Files WHERE Missing = 0 AND Id IN ({string.Join(",", chunk)})";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }
        }, ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>Platzhalter für <see cref="IReverseGeocoder"/> bis zum Zusammenbau: kennt keine Orte.</summary>
public sealed class NoReverseGeocoder : IReverseGeocoder
{
    public PlaceInfo? Nearest(double latitude, double longitude) => null;

    public IReadOnlyList<(PlaceInfo Place, double Latitude, double Longitude)> Search(string text, int max) => [];
}

public static class PlacesPlaceholderServiceCollectionExtensions
{
    /// <summary>Platzhalter-Registrierung (TryAdd: echte Dienste von Agent V haben Vorrang). Beim Zusammenbau entfernen.</summary>
    public static IServiceCollection AddPlacesPlaceholders(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryGeoCatalog>();
        services.TryAddSingleton<IGeoQueryService>(sp => sp.GetRequiredService<InMemoryGeoCatalog>());
        services.TryAddSingleton<IGeoTagService>(sp => sp.GetRequiredService<InMemoryGeoCatalog>());
        services.TryAddSingleton<IReverseGeocoder, NoReverseGeocoder>();
        return services;
    }
}

using Lumora.Domain.Enums;
using Lumora.Places.Suggestions;
using Lumora.Services.Library;
using Lumora.Services.Places;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;

namespace Lumora.Wpf.Tests.Places;

/// <summary>Bereich aus festen Bibliotheken und Gruppe 1.</summary>
internal sealed class FixedScope(params int[] libraryIds) : ILibraryScope
{
    public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default)
        => Task.FromResult(new LibraryScopeInfo(libraryIds[0], 1, "Test", libraryIds, libraryIds, false));

    public Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default) => GetCurrentAsync(ct);

    public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryGroupInfo>>([new LibraryGroupInfo(1, "Test", libraryIds)]);

    public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Vorschläge im Speicher; zeichnet Übernehmen/Ablehnen auf.</summary>
internal sealed class FakeSuggestionService : IGeoSuggestionService
{
    public List<GeoSuggestionItem> Inaccurate { get; } = [];

    public List<GeoSuggestionItem> Unlocated { get; } = [];

    public HashSet<int> Rejected { get; } = [];

    public List<(IReadOnlyList<GeoSuggestionApply> Items, bool WriteToFile)> Applied { get; } = [];

    public List<CameraTimeInfo> Cameras { get; } = [];

    public int NextBatchId { get; set; } = 41;

    public int Activities { get; set; } = 1;

    public Task<GeoSuggestionSet> LoadAsync(LibraryScopeInfo scope, GeoSuggestionListKind kind, CancellationToken ct = default)
        => Task.FromResult(new GeoSuggestionSet(kind, (kind == GeoSuggestionListKind.Inaccurate ? Inaccurate : Unlocated).Where(i => !Rejected.Contains(i.FileId) && scope.Contains(i.LibraryId)).ToList(), Activities));

    public Task<GeoAccuracyCounts> CountAsync(LibraryScopeInfo scope, CancellationToken ct = default)
        => Task.FromResult(new GeoAccuracyCounts(Inaccurate.Count(i => scope.Contains(i.LibraryId)), Unlocated.Count, Activities));

    public Task<GeoSuggestionItem?> GetForFileAsync(int fileId, CancellationToken ct = default)
        => Task.FromResult(Inaccurate.Concat(Unlocated).FirstOrDefault(i => i.FileId == fileId && !Rejected.Contains(fileId)));

    public Task SetRejectedAsync(IReadOnlyCollection<int> fileIds, bool rejected, CancellationToken ct = default)
    {
        foreach (var id in fileIds)
        {
            if (rejected)
            {
                Rejected.Add(id);
            }
            else
            {
                Rejected.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<GeoAssignResult> ApplyAsync(IReadOnlyList<GeoSuggestionApply> items, bool writeToFile, CancellationToken ct = default)
    {
        Applied.Add((items, writeToFile));
        Inaccurate.RemoveAll(i => items.Any(a => a.FileId == i.FileId));
        return Task.FromResult(new GeoAssignResult(NextBatchId++, writeToFile ? items.Count : 0, writeToFile ? 0 : items.Count, 0, []));
    }

    public Task<IReadOnlyList<CameraTimeInfo>> GetCamerasAsync(LibraryScopeInfo scope, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CameraTimeInfo>>(Cameras);
}

internal sealed class FakeGeoUndo : IGeoTagUndoService
{
    public List<int> Undone { get; } = [];

    public Task<int?> GetUndoableBatchIdAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);

    public Task<GeoTagUndoResult?> UndoAsync(int batchId, CancellationToken ct = default)
    {
        Undone.Add(batchId);
        return Task.FromResult<GeoTagUndoResult?>(new GeoTagUndoResult(batchId, 1, 0, [], "Test"));
    }
}

internal sealed class FakeTrackSettings : IGeoTrackSettingsStore
{
    public GeoTrackSettings Value { get; set; } = GeoTrackSettings.Default;

    public Dictionary<string, long> Markers { get; } = new();

    public int Saves { get; private set; }

    public Task<GeoTrackSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);

    public Task SaveAsync(GeoTrackSettings settings, CancellationToken ct = default)
    {
        Value = settings;
        Saves++;
        return Task.CompletedTask;
    }

    public Task<long?> GetMarkerAsync(string key, CancellationToken ct = default) => Task.FromResult(Markers.TryGetValue(key, out var v) ? (long?)v : null);

    public Task SetMarkerAsync(string key, long value, CancellationToken ct = default)
    {
        Markers[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class FakeTrackStore : IGpsTrackStore
{
    public List<GpsTrackInfo> Tracks { get; } = [];

    public Dictionary<int, TrackPoint[]> Points { get; } = new();

    public int Deleted { get; private set; }

    public Task<GpsTrackSummary> GetSummaryAsync(int groupId, CancellationToken ct = default)
        => Task.FromResult(Tracks.Count == 0
            ? GpsTrackSummary.Empty
            : new GpsTrackSummary(Tracks.Count, Tracks.Min(t => t.StartUtc), Tracks.Max(t => t.EndUtc), Tracks.Select(t => t.Device!).Distinct().ToList(), new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc)));

    public Task<IReadOnlyList<GpsTrackInfo>> ListAsync(int groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GpsTrackInfo>>(Tracks.ToList());

    public Task<IReadOnlyList<GpsTrackInfo>> FindOverlappingAsync(int groupId, long fromUnix, long toUnix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GpsTrackInfo>>(Tracks.Where(t => t.StartUnix <= toUnix && t.EndUnix >= fromUnix).ToList());

    public Task<TrackPoint[]> LoadPointsAsync(int trackId, CancellationToken ct = default) => Task.FromResult(Points.GetValueOrDefault(trackId, []));

    public Task<HashSet<string>> LoadChecksumsAsync(int groupId, CancellationToken ct = default) => Task.FromResult(new HashSet<string>());

    public Task<int> InsertAsync(int groupId, IReadOnlyList<NewGpsTrack> tracks, CancellationToken ct = default) => Task.FromResult(tracks.Count);

    public Task<int> DeleteAllAsync(int groupId, CancellationToken ct = default)
    {
        var n = Tracks.Count;
        Tracks.Clear();
        Deleted += n;
        return Task.FromResult(n);
    }
}

/// <summary>Bausteine für Vorschläge der Wpf-Tests.</summary>
internal static class SuggestionSamples
{
    public const long T0 = 1_584_280_000; // 2020-03-15 13:46:40 UTC

    public static readonly GpsTrackInfo Hike = new(7, DateTimeOffset.FromUnixTimeSeconds(T0 - 600).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(T0 + 3000).UtcDateTime,
        100, 4800, "Wandern", "Fenix 7", "Wanderung Weinebene", GpsTrackSource.Fit, new GeoBounds(46.9, 14.9, 47.0, 15.0));

    public static GeoSuggestionItem Item(int id, GeoSuggestionLevel level, int minutes = 0, int library = 1, bool stored = true, int dayOffset = 0)
    {
        var utc = T0 + (minutes * 60L) + (dayOffset * 86_400L);
        GeoSuggestion? suggestion = level switch
        {
            GeoSuggestionLevel.Track => new GeoSuggestion(id, level, GeoSuggestionKind.Track, 46.95, 14.95 + (id * 0.001), 7, 5, Altitude: 1853),
            GeoSuggestionLevel.Sure => new GeoSuggestion(id, level, GeoSuggestionKind.Interpolated, 47.0, 15.0,
                AnchorBefore: new SuggestionAnchor(90, utc - 300, 47.0, 14.99), AnchorAfter: new SuggestionAnchor(91, utc + 300, 47.0, 15.01)),
            GeoSuggestionLevel.Likely => new GeoSuggestion(id, level, GeoSuggestionKind.Neighbor, 47.05, 15.05, AnchorBefore: new SuggestionAnchor(92, utc - 600, 47.05, 15.05)),
            _ => null,
        };
        return new GeoSuggestionItem(id, library, $"foto{id}.jpg", 0, DateTimeOffset.FromUnixTimeSeconds(utc).UtcDateTime.AddHours(1), utc, PhotoTimeBasis.TimeZone,
            stored ? 46.85 : null, stored ? 15.12 : null, stored ? GeoAccuracyReason.NoAltitudeNoTime : GeoAccuracyReason.None, suggestion,
            level == GeoSuggestionLevel.Track ? Hike : null);
    }

    public static (PlacesSuggestionContext Context, FakeSuggestionService Service, FakeGeoUndo Undo, FakeTrackSettings Settings, FakeTrackStore Tracks) Context(params int[] libraries)
    {
        var service = new FakeSuggestionService();
        var undo = new FakeGeoUndo();
        var settings = new FakeTrackSettings();
        var tracks = new FakeTrackStore();
        tracks.Tracks.Add(Hike);
        tracks.Points[7] = Enumerable.Range(0, 100).Select(i => new TrackPoint(T0 - 600 + (i * 36), 46.95, 14.9 + (i * 0.001), 1800 + i)).ToArray();
        var world = new FakePlacesWorld();
        return (new PlacesSuggestionContext(service, undo, settings, tracks, new FixedScope(libraries.Length == 0 ? [1] : libraries), world), service, undo, settings, tracks);
    }
}

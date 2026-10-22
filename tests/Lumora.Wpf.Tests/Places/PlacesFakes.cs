using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Places;
using Lumora.Places.Data;
using Lumora.Places.Dialogs;
using Lumora.Places.Map.Tiles;
using Lumora.Places.ViewModels;
using Lumora.Services.Places;
using Lumora.Shell;

namespace Lumora.Wpf.Tests.Places;

/// <summary>
/// Fakes für die Seite „Orte“: Geodaten und Medien je Bibliothek im Speicher (Fake von <see cref="IGeoQueryService"/>,
/// <see cref="IGeoTagService"/> und <see cref="IReverseGeocoder"/> nach dem Vertrag mit Agent V), Dialog mit
/// vorgegebener Antwort, aufzeichnende Lightbox und Navigation. Kein Netz, keine Datenbank.
/// </summary>
internal sealed class FakePlacesWorld : IGeoQueryService, IGeoTagService, IReverseGeocoder, IPlacesCatalogReader, IPlacesScope
{
    /// <summary>FileId → (Bibliothek, Koordinaten oder null, Bewertung, Datum).</summary>
    public Dictionary<int, (int Library, (double Lat, double Lon)? Location, byte Rating, long SortKey)> Files { get; } = new();

    public List<int> ScopeLibraries { get; } = [1];

    public List<(IReadOnlyCollection<int> Ids, double Lat, double Lon, bool Keep)> SetCalls { get; } = [];

    public List<IReadOnlyCollection<int>> PointQueries { get; } = [];

    public void Add(int id, int library, double? lat = null, double? lon = null, byte rating = 0, long sortKey = 0)
        => Files[id] = (library, lat is { } a && lon is { } b ? (a, b) : null, rating, sortKey == 0 ? id : sortKey);

    // IPlacesScope
    public Task<IReadOnlyList<int>> GetLibraryIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(ScopeLibraries.ToList());

    // IGeoQueryService
    public Task<IReadOnlyList<GeoPoint>> GetPointsAsync(IReadOnlyCollection<int> libraryIds, GeoBounds? bounds, CancellationToken ct)
    {
        PointQueries.Add(libraryIds.ToList());
        return Task.FromResult<IReadOnlyList<GeoPoint>>(Files
            .Where(f => libraryIds.Contains(f.Value.Library) && f.Value.Location is not null)
            .Select(f => new GeoPoint(f.Key, f.Value.Location!.Value.Lat, f.Value.Location!.Value.Lon))
            .ToList());
    }

    public async Task<GeoBounds?> GetExtentAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct)
    {
        var points = await GetPointsAsync(libraryIds, null, ct);
        return points.Count == 0
            ? null
            : new GeoBounds(points.Min(p => p.Latitude), points.Min(p => p.Longitude), points.Max(p => p.Latitude), points.Max(p => p.Longitude));
    }

    // IGeoTagService
    public Task<GeoTagResult> SetLocationAsync(IReadOnlyCollection<int> fileIds, double latitude, double longitude, bool keepModifiedTime, CancellationToken ct)
    {
        SetCalls.Add((fileIds.ToList(), latitude, longitude, keepModifiedTime));
        foreach (var id in fileIds)
        {
            var f = Files[id];
            Files[id] = (f.Library, (latitude, longitude), f.Rating, f.SortKey);
        }

        return Task.FromResult(new GeoTagResult(fileIds.Count, 0, 0, []));
    }

    public Task<GeoTagResult> ClearLocationAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct) => Task.FromResult(new GeoTagResult(0, 0, 0, []));

    // IReverseGeocoder
    public static readonly PlaceInfo Vienna = new(1, "Wien", "Wien", "AT", "Österreich");
    public static readonly PlaceInfo Graz = new(2, "Graz", "Steiermark", "AT", "Österreich");

    public PlaceInfo? Nearest(double latitude, double longitude) => latitude > 47.6 ? Vienna : Graz;

    public IReadOnlyList<(PlaceInfo Place, double Latitude, double Longitude)> Search(string text, int max)
        => new[] { (Vienna, 48.2082, 16.3738), (Graz, 47.0707, 15.4395) }
            .Where(p => p.Item1.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Take(max).ToList();

    // IPlacesCatalogReader
    public Task<IReadOnlyList<PlacesMediaRow>> GetMediaAsync(IReadOnlyCollection<int> libraryIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PlacesMediaRow>>(Files
            .Where(f => libraryIds.Contains(f.Value.Library))
            .OrderByDescending(f => f.Value.SortKey).ThenByDescending(f => f.Key)
            .Select(f => new PlacesMediaRow(f.Key, f.Value.Rating, f.Value.SortKey))
            .ToList());

    /// <summary>Gefilterte Abfragen (ADR-0055): Filter und Treffer-Prädikat des Tests.</summary>
    public List<GalleryQuery?> MediaFilters { get; } = [];

    /// <summary>Welche Dateien der (Fake-)Galerie-Filter trifft; null = alle.</summary>
    public Func<int, bool>? FilterMatches { get; set; }

    public async Task<IReadOnlyList<PlacesMediaRow>> GetMediaAsync(IReadOnlyCollection<int> libraryIds, GalleryQuery? filter, CancellationToken ct)
    {
        MediaFilters.Add(filter);
        var all = await GetMediaAsync(libraryIds, ct);
        return filter is null || FilterMatches is null ? all : all.Where(r => FilterMatches(r.FileId)).ToList();
    }

    public Task<IReadOnlyList<PlacesFileInfo>> GetFilesAsync(IReadOnlyCollection<int> libraryIds, IReadOnlyCollection<int> fileIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PlacesFileInfo>>(fileIds.Distinct()
            .Where(id => Files.TryGetValue(id, out var f) && libraryIds.Contains(f.Library))
            .Select(id => new PlacesFileInfo(id, Files[id].Library, @"C:\Fotos", $"foto{id}.jpg", MediaType.Image, false, Files[id].Rating, 1000, DateTime.UnixEpoch))
            .ToList());
}

/// <summary>Vorschaubilder sofort als 1×1-Bitmap (ohne Thumbnail-Pipeline).</summary>
internal sealed class FakePlacesImages : IPlacesImageSource
{
    private readonly Dictionary<int, ImageSource> _images = new();

    public List<int> Requested { get; } = [];

    public event Action<IReadOnlyList<int>>? ImagesReady;

    public ImageSource? TryGet(int fileId) => _images.GetValueOrDefault(fileId);

    public void Request(IReadOnlyList<PlacesFileInfo> files)
    {
        Requested.AddRange(files.Select(f => f.FileId));
        foreach (var f in files)
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 90, 120, 200, 255 }, 4);
            bmp.Freeze();
            _images[f.FileId] = bmp;
        }

        ImagesReady?.Invoke(files.Select(f => f.FileId).ToList());
    }
}

internal sealed class FakePlacesDialogs : IPlacesDialogs
{
    public AssignLocationAnswer? Next { get; set; } = new(KeepModifiedTime: true);

    public List<AssignLocationRequest> Requests { get; } = [];

    public Task<AssignLocationAnswer?> ConfirmAssignAsync(AssignLocationRequest request, Window? owner)
    {
        Requests.Add(request);
        return Task.FromResult(Next);
    }
}

internal sealed class RecordingLightbox : ILightboxService
{
    public List<(IReadOnlyList<LightboxEntry> Entries, int Start)> Opened { get; } = [];

    public bool IsOpen => false;

    public void Open(IReadOnlyList<LightboxEntry> entries, int startIndex, Window? owner = null) => Opened.Add((entries, startIndex));
}

internal sealed class RecordingNavigation : INavigationService
{
    public List<string> Tags { get; } = [];

    public string? CurrentTag => Tags.LastOrDefault();

    public event EventHandler<string>? Navigated;

    public void NavigateTo(string tag)
    {
        Tags.Add(tag);
        Navigated?.Invoke(this, tag);
    }
}

internal sealed class FakePrivacy : IMapPrivacyContext
{
    public bool IsHiddenCatalog { get; set; }
}

/// <summary>Bausteine eines ViewModels mit Fakes.</summary>
internal sealed class PlacesHarness
{
    public FakePlacesWorld World { get; } = new();
    public FakePlacesImages Images { get; } = new();
    public FakePlacesDialogs Dialogs { get; } = new();
    public RecordingLightbox Lightbox { get; } = new();
    public RecordingNavigation Navigation { get; } = new();
    public FakePrivacy Privacy { get; } = new();
    public WeakReferenceMessenger Messenger { get; } = new();

    /// <summary>Gemeinsamer Galerie-Filter (ADR-0055) — echter Speicher, die Galerie veröffentlicht per <c>Publish</c>.</summary>
    public SharedGalleryFilter Filter { get; } = new();

    public PlacesViewModel Create(IMapTileSource? tiles = null, Lumora.Places.Styles.IMapStyleService? styles = null, Lumora.Places.Suggestions.PlacesSuggestionContext? suggestions = null)
    {
        var source = tiles ?? new DisabledMapTileSource();
        var vm = new PlacesViewModel(World, World, World, World, World, Images, Dialogs, Lightbox, Navigation, Privacy,
            NullLogger<PlacesViewModel>.Instance, new MapTileProvider(source, new MapTileOptions { Enabled = false }), Messenger, Filter, styles, suggestions);
        vm.Viewport.SetSize(1000, 700);
        return vm;
    }

    /// <summary>Wien (3 Fotos dicht beieinander), Graz (2), Bibliothek 2 in Paris (2, darf nie auftauchen), 3 ohne Ort.</summary>
    public void SeedAustria()
    {
        World.Add(1, 1, 48.2082, 16.3738, rating: 3);
        World.Add(2, 1, 48.2090, 16.3750, rating: 5);
        World.Add(3, 1, 48.2075, 16.3725);
        World.Add(4, 1, 47.0707, 15.4395);
        World.Add(5, 1, 47.0712, 15.4401);
        World.Add(6, 1);
        World.Add(7, 1);
        World.Add(8, 1);
        World.Add(101, 2, 48.8566, 2.3522);
        World.Add(102, 2, 48.8570, 2.3530);
        World.Add(103, 2);
    }
}

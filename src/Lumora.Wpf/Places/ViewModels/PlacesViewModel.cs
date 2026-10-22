using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Places.Styles;
using Lumora.Places.Data;
using Lumora.Places.Dialogs;
using Lumora.Places.Map;
using Lumora.Places.Map.Tiles;
using Lumora.Services.Messaging;
using Lumora.Services.Places;
using Lumora.Services.Places.Clustering;
using Lumora.Shell;

namespace Lumora.Places.ViewModels;

/// <summary>
/// Seite „Orte“ (ADR-0051): Weltkarte mit den verorteten Fotos des Bibliotheks-Bereichs als Gruppen, die beim Zoomen
/// feiner werden (<see cref="GeoClusterIndex"/>), Start-Ausschnitt = Ausdehnung der Fotos plus Rand (ohne verortete
/// Fotos die Welt), Ortssuche, „In der Galerie zeigen“ (<see cref="ShowFilesOnMapSelectionMessage"/>), Lightbox für
/// Einzelfotos und Verorten aus der Seitenleiste „Ohne Ort“ (Ziehen auf die Karte bzw. Rechtsklick „Hier verorten“ →
/// <see cref="IGeoTagService.SetLocationAsync"/>). Daten nur aus den Bibliotheken von <see cref="IPlacesScope"/>.
/// </summary>
public sealed partial class PlacesViewModel : ObservableObject
{
    /// <summary>Höchstens so viele Fotos öffnet die Lightbox aus einer Gruppe bzw. dem Ausschnitt.</summary>
    public const int LightboxLimit = 2000;

    private readonly IPlacesScope _scope;
    private readonly IGeoQueryService _geo;
    private readonly IGeoTagService _tagger;
    private readonly IReverseGeocoder _geocoder;
    private readonly IPlacesCatalogReader _reader;
    private readonly IPlacesImageSource _images;
    private readonly IPlacesDialogs _dialogs;
    private readonly ILightboxService _lightbox;
    private readonly INavigationService _navigation;
    private readonly IMapPrivacyContext _privacy;
    private readonly ILogger<PlacesViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly Dictionary<int, PlacesFileInfo> _infos = new();
    private readonly Dictionary<int, UnlocatedTile> _unlocatedById = new();
    private readonly HashSet<int> _imagesRequested = new(); // Marker-Bilder schon angefordert (nicht bei jeder Mausbewegung erneut)
    private Dictionary<int, PlacesMediaRow> _media = new();
    private IReadOnlyList<int> _libraryIds = [];
    private bool _dirty = true;
    private bool _fitPending = true;
    private bool _isVisible;
    private int _loadVersion;
    private Task _lastLoad = Task.CompletedTask;
    private CancellationTokenSource? _reloadDelay;
    private DateTime _ownChangeUntil = DateTime.MinValue; // Meldungen der eigenen Verortung bis dahin überhören

    public PlacesViewModel(
        IPlacesScope scope,
        IGeoQueryService geo,
        IGeoTagService tagger,
        IReverseGeocoder geocoder,
        IPlacesCatalogReader reader,
        IPlacesImageSource images,
        IPlacesDialogs dialogs,
        ILightboxService lightbox,
        INavigationService navigation,
        IMapPrivacyContext privacy,
        ILogger<PlacesViewModel> logger,
        MapTileProvider? tiles = null,
        IMessenger? messenger = null,
        ISharedGalleryFilter? filter = null,
        IMapStyleService? styles = null,
        Lumora.Places.Suggestions.PlacesSuggestionContext? suggestions = null)
    {
        _scope = scope;
        _geo = geo;
        _tagger = tagger;
        _geocoder = geocoder;
        _reader = reader;
        _images = images;
        _dialogs = dialogs;
        _lightbox = lightbox;
        _navigation = navigation;
        _privacy = privacy;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        Tiles = tiles;

        Viewport.ShowWorld();
        Viewport.Changed += (_, _) => UpdateMarkers();
        _images.ImagesReady += ApplyImages;
        if (tiles is not null)
        {
            tiles.StateChanged += (_, _) => UpdateTileStatus();
            tiles.SourceChanged += (_, _) => OnMapSourceChanged(); // Kartenstil (ADR-0055, PlacesViewModel.Styles.cs)
        }

        InitFilterAndStyles(filter, styles); // gemeinsamer Filter und Kartenstil (ADR-0055)
        InitSuggestions(suggestions); // ungenaue Orte und GPS-Spuren (ADR-0062, PlacesViewModel.Suggestions.cs)

        _messenger.Register<PlacesViewModel, ActiveLibraryChangedMessage>(this, static (r, _) => UiThread.Post(() => r.OnLibraryChanged()));
        _messenger.Register<PlacesViewModel, LibraryRootsChangedMessage>(this, static (r, m) => UiThread.Post(() => r.OnCatalogChanged(m.LibraryId, ownChange: false)));
        _messenger.Register<PlacesViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => r.OnCatalogChanged(m.LibraryId, ownChange: false)));
        _messenger.Register<PlacesViewModel, FilesChangedMessage>(this, static (r, m) => UiThread.Post(
            () => r.OnCatalogChanged(m.LibraryId, ownChange: m.Kind == FilesChangeKind.Metadata && DateTime.UtcNow < r._ownChangeUntil)));
        UpdateTileStatus();
    }

    /// <summary>Ausschnitt der Karte (die Karte verschiebt/zoomt ihn, Änderungen berechnen die Marker neu).</summary>
    public MapViewport Viewport { get; } = new();

    public MapTileProvider? Tiles { get; }

    /// <summary>Gruppen-Index des aktuellen Bereichs (Tests, Rauchtest).</summary>
    public GeoClusterIndex Index { get; private set; } = GeoClusterIndex.Empty;

    /// <summary>Zuletzt gestartetes Laden (Tests, Rauchtest).</summary>
    public Task LastLoad => _lastLoad;

    [ObservableProperty] private IReadOnlyList<MapMarker> _markers = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(HasNoLocatedPhotos))]
    private int _locatedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(UnlocatedHeader), nameof(HasUnlocated))]
    private int _unlocatedCount;

    public string SummaryText => HasLibrary
        ? PlacesText.Summary(LocatedCount, UnlocatedCount) + (IsFiltered ? " · gefiltert" : string.Empty)
        : "Keine Bibliothek";

    public string UnlocatedHeader => $"Ohne Ort ({PlacesText.N(UnlocatedCount)})";

    public bool HasUnlocated => UnlocatedCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLocatedPhotos))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(HasNoLocatedPhotos))]
    private bool _hasLibrary;

    /// <summary>Leerzustand über der Karte: Bibliothek ohne verortete Fotos.</summary>
    public bool HasNoLocatedPhotos => HasLibrary && !IsLoading && LocatedCount == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowVisibleInGalleryCommand))]
    private int _visiblePhotoCount;

    public string VisibleText => VisiblePhotoCount == 0 ? "Keine Fotos im Ausschnitt" : $"Im Ausschnitt: {PlacesText.N(VisiblePhotoCount)} Foto{(VisiblePhotoCount == 1 ? string.Empty : "s")}";

    partial void OnVisiblePhotoCountChanged(int value) => OnPropertyChanged(nameof(VisibleText));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty] private bool _messageIsError;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Hinweis im versteckten Modus: Kacheln kommen aus dem Internet.</summary>
    [ObservableProperty] private bool _isHiddenNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTileStatus))]
    private string _tileStatusText = string.Empty;

    public bool HasTileStatus => !string.IsNullOrEmpty(TileStatusText);

    public string Attribution => Tiles?.Attribution ?? OsmTileSource.OsmAttribution;

    /// <summary>Lizenzseite der Namensnennung (OSM, CARTO bzw. MapTiler).</summary>
    public string AttributionLink => CurrentDefinition?.AttributionLink ?? MapStyleCatalog.OpenStreetMap.AttributionLink;

    [ObservableProperty] private bool _isBusy;

    // ---- Lebenszyklus --------------------------------------------------------------------------------------------------

    /// <summary>Seite wird angezeigt: beim ersten Mal bzw. nach Änderungen neu laden.</summary>
    public Task ActivateAsync()
    {
        _isVisible = true;
        IsHiddenNotice = _privacy.IsHiddenCatalog && Tiles is { State: not MapTileSourceState.Disabled };
        UpdateTileStatus();
        return _dirty ? ReloadAsync() : Task.CompletedTask;
    }

    public void Deactivate() => _isVisible = false;

    /// <summary>Neu laden; der Ausschnitt bleibt, außer nach einem Bibliothekswechsel bzw. beim ersten Laden.</summary>
    public Task ReloadAsync()
    {
        _lastLoad = LoadAsync();
        return _lastLoad;
    }

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    private void OnLibraryChanged()
    {
        _dirty = true;
        _fitPending = true;
        if (_isVisible)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>
    /// Scan, Datei-Änderungen, Verorten anderswo: entprellt neu laden, solange die Seite sichtbar ist — nur für
    /// Bibliotheken des Bereichs. Die eigene Verortung lädt selbst neu; ihre Metadaten-Meldung kurz danach zählt nicht.
    /// </summary>
    private void OnCatalogChanged(int libraryId, bool ownChange)
    {
        if (ownChange || (_libraryIds.Count > 0 && !_libraryIds.Contains(libraryId)))
        {
            return;
        }

        _dirty = true;
        if (!_isVisible)
        {
            return;
        }

        _reloadDelay?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDelay = cts;
        _ = ReloadAfterDelayAsync(cts.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(600, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var libraries = await _scope.GetLibraryIdsAsync(CancellationToken.None).ConfigureAwait(true);
            if (!libraries.SequenceEqual(_libraryIds))
            {
                _fitPending = true;
                _infos.Clear(); // nichts aus einer anderen Bibliothek weiterverwenden
                _imagesRequested.Clear();
            }

            _libraryIds = libraries;
            HasLibrary = libraries.Count > 0;
            _dirty = false;
            if (libraries.Count == 0)
            {
                Apply(GeoClusterIndex.Empty, new Dictionary<int, PlacesMediaRow>(), [], null, version);
                return;
            }

            // Gemeinsamer Galerie-Filter (ADR-0055): nur die Medien der Treffer — Punkte ohne Medienzeile zählen in Build()
            // nicht, so filtern Gruppen, Zähler, „Im Ausschnitt“, „Ohne Ort“ und der Start-Ausschnitt mit
            var filter = CurrentFilter;
            IsFiltered = filter is not null;
            var pointsTask = _geo.GetPointsAsync(libraries, null, CancellationToken.None);
            var extentTask = filter is null ? _geo.GetExtentAsync(libraries, CancellationToken.None) : Task.FromResult<GeoBounds?>(null);
            var mediaTask = _reader.GetMediaAsync(libraries, filter, CancellationToken.None);
            await Task.WhenAll(pointsTask, extentTask, mediaTask).ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            var points = pointsTask.Result;
            var media = mediaTask.Result;
            var (index, mediaById, unlocated, millis) = await Task.Run(() => Build(points, media)).ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            _logger.LogInformation(
                "Orte: {Located} verortete Fotos in {Millis} ms gruppiert ({Unlocated} ohne Ort, {Libraries} Bibliothek(en))",
                index.PhotoCount, millis, unlocated.Count, libraries.Count);
            Apply(index, mediaById, unlocated, extentTask.Result, version);
            _ = RefreshSuggestions(); // Zähler „geschätzt“ und Vorschläge (ADR-0062)
        }
        catch (Exception ex)
        {
            _dirty = true; // beim nächsten Anzeigen erneut versuchen
            _logger.LogWarning(ex, "Orte: Karte nicht geladen");
            ShowMessage("Die Orte konnten nicht geladen werden: " + ex.Message, isError: true);
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Punkte ∩ vorhandene Medien des Bereichs → Index; übrige Medien (neueste zuerst) → „Ohne Ort“.</summary>
    internal static (GeoClusterIndex Index, Dictionary<int, PlacesMediaRow> Media, List<int> Unlocated, long Millis) Build(
        IReadOnlyList<GeoPoint> points, IReadOnlyList<PlacesMediaRow> media)
    {
        var watch = Stopwatch.StartNew();
        var byId = new Dictionary<int, PlacesMediaRow>(media.Count);
        foreach (var row in media)
        {
            byId[row.FileId] = row;
        }

        var located = new HashSet<int>();
        var inputs = new List<ClusterInput>(points.Count);
        foreach (var p in points)
        {
            if (double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude) && Math.Abs(p.Latitude) <= 90 && Math.Abs(p.Longitude) <= 180
                && byId.TryGetValue(p.FileId, out var row) && located.Add(p.FileId))
            {
                inputs.Add(new ClusterInput(p.FileId, p.Latitude, p.Longitude, row.Rating, row.SortKey));
            }
        }

        var index = GeoClusterIndex.Build(inputs);
        var unlocated = new List<int>(Math.Max(0, media.Count - inputs.Count));
        foreach (var row in media)
        {
            if (!located.Contains(row.FileId))
            {
                unlocated.Add(row.FileId);
            }
        }

        return (index, byId, unlocated, watch.ElapsedMilliseconds);
    }

    private void Apply(GeoClusterIndex index, Dictionary<int, PlacesMediaRow> media, List<int> unlocated, GeoBounds? extent, int version)
    {
        if (version != _loadVersion)
        {
            return;
        }

        Index = index;
        _media = media;
        _infos.Clear(); // Pfade/Namen können sich geändert haben (Verschieben, Umbenennen) — sichtbare werden neu gelesen
        _imagesRequested.Clear();
        LocatedCount = index.PhotoCount;
        ApplyUnlocated(unlocated);
        if (_fitPending)
        {
            _fitPending = false;
            _fitIfNothingVisible = false;
            Viewport.FitBounds(index.PhotoCount == 0 ? null : extent ?? index.GetExtent());
        }

        UpdateMarkers();
        if (_fitIfNothingVisible)
        {
            // Filter geändert: der Ausschnitt bleibt, solange er Treffer zeigt — sonst auf die Treffer (ADR-0055)
            _fitIfNothingVisible = false;
            if (VisiblePhotoCount == 0 && index.PhotoCount > 0)
            {
                Viewport.FitBounds(index.GetExtent());
            }
        }
    }

    // ---- Marker --------------------------------------------------------------------------------------------------------

    /// <summary>Marker des Ausschnitts neu bestimmen (nach jeder Bewegung, synchron — Abfrage &lt; 10 ms).</summary>
    public void UpdateMarkers()
    {
        var (minX, minY, maxX, maxY) = Viewport.VisibleUnitRect(MapControl.GroupRadius + 12);
        var clusters = Index.Query(minX, minY, maxX, maxY, Viewport.ClusterZoom);
        var markers = new List<MapMarker>(clusters.Count);
        var visible = 0;
        var (vx0, vy0, vx1, vy1) = Viewport.VisibleUnitRect();
        var missing = new List<int>();
        var index = Index;
        foreach (var cluster in clusters)
        {
            var image = _images.TryGet(cluster.BestFileId);
            var inView = cluster.X >= vx0 && cluster.X <= vx1 && cluster.Y >= vy0 && cluster.Y <= vy1;
            markers.Add(new MapMarker(cluster, index, image) { IsInView = inView });
            if (image is null && _imagesRequested.Add(cluster.BestFileId))
            {
                missing.Add(cluster.BestFileId);
            }

            if (inView)
            {
                visible += cluster.Count;
            }
        }

        Markers = markers;
        VisiblePhotoCount = visible;
        if (missing.Count > 0)
        {
            _ = RequestImagesAsync(missing);
        }
    }

    private async Task RequestImagesAsync(IReadOnlyList<int> fileIds)
    {
        try
        {
            var infos = await EnsureInfosAsync(fileIds).ConfigureAwait(true);
            var found = infos.Select(i => i.FileId).ToHashSet();
            foreach (var id in fileIds.Where(id => !found.Contains(id)))
            {
                _imagesRequested.Remove(id); // keine Angaben (Neuladen dazwischen) → beim nächsten Mal erneut
            }

            _images.Request(infos);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Orte: Vorschaubilder nicht angefordert");
        }
    }

    private async Task<IReadOnlyList<PlacesFileInfo>> EnsureInfosAsync(IReadOnlyCollection<int> fileIds)
    {
        var missing = fileIds.Where(id => !_infos.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var loaded = await _reader.GetFilesAsync(_libraryIds, missing, CancellationToken.None).ConfigureAwait(true);
            foreach (var info in loaded.Where(i => _libraryIds.Contains(i.LibraryId))) // Bereich inzwischen gewechselt?
            {
                _infos[info.FileId] = info;
            }
        }

        return fileIds.Where(_infos.ContainsKey).Select(id => _infos[id]).ToList();
    }

    private void ApplyImages(IReadOnlyList<int> fileIds)
    {
        var ids = fileIds.ToHashSet();
        _imagesRequested.ExceptWith(ids); // angekommen; fällt es später aus dem Speicher, wird es neu angefordert
        var changed = false;
        foreach (var marker in Markers)
        {
            if (marker.Image is null && ids.Contains(marker.BestFileId) && _images.TryGet(marker.BestFileId) is { } image)
            {
                marker.Image = image;
                changed = true;
            }
        }

        if (changed)
        {
            Markers = Markers.ToList(); // neue Liste → Karte zeichnet neu
        }

        foreach (var id in fileIds)
        {
            if (_unlocatedById.TryGetValue(id, out var tile) && tile.Image is null)
            {
                tile.Image = _images.TryGet(id);
            }
        }

        ApplyRowImages(fileIds); // Vorschlagsliste (ADR-0062)
    }

    /// <summary>
    /// Klick auf einen Marker: Gruppe → hineinzoomen, bis sie zerfällt (zerfällt sie nie — alle am selben Ort —, öffnet
    /// die Lightbox mit ihren Fotos); Einzelfoto → Lightbox.
    /// </summary>
    public async Task ActivateMarkerAsync(MapMarker marker)
    {
        if (!marker.IsGroup)
        {
            await OpenLightboxAsync(VisibleFileIds(), marker.BestFileId).ConfigureAwait(true);
            return;
        }

        var zoom = marker.Index.GetExpansionZoom(marker.Cluster);
        if (zoom > MapViewport.MaxZoomLevel)
        {
            await OpenLightboxAsync(marker.FileIds(), marker.BestFileId).ConfigureAwait(true);
            return;
        }

        Viewport.SetView(marker.X, marker.Y, Math.Max(zoom, Viewport.Zoom));
    }

    /// <summary>„In der Galerie zeigen“ bzw. Doppelklick: Galerie mit genau den Dateien der Gruppe.</summary>
    public void ShowInGallery(MapMarker marker)
        => SendToGallery(marker.Index.GetBounds(marker.Cluster), marker.FileIds());

    [RelayCommand(CanExecute = nameof(CanShowVisibleInGallery))]
    private void ShowVisibleInGallery() => SendToGallery(Viewport.VisibleBounds, VisibleFileIds());

    private bool CanShowVisibleInGallery() => VisiblePhotoCount > 0;

    private void SendToGallery(GeoBounds bounds, IReadOnlyList<int> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Orte: {Count} Fotos in der Galerie zeigen", fileIds.Count);
        _messenger.Send(new ShowFilesOnMapSelectionMessage(bounds, fileIds));
        _navigation.NavigateTo(NavigationTags.Gallery);
    }

    /// <summary>Alle Fotos der Marker, deren Mittelpunkt im Ausschnitt liegt (wie „Im Ausschnitt: n Fotos“).</summary>
    public IReadOnlyList<int> VisibleFileIds() => Markers.Where(m => m.IsInView).SelectMany(m => m.FileIds()).ToList();

    private async Task OpenLightboxAsync(IReadOnlyList<int> fileIds, int startFileId)
    {
        var ordered = fileIds
            .OrderByDescending(id => _media.TryGetValue(id, out var row) ? row.SortKey : 0)
            .ThenByDescending(id => id)
            .Take(LightboxLimit)
            .ToList();
        if (!ordered.Contains(startFileId))
        {
            ordered.Insert(0, startFileId);
        }

        try
        {
            var infos = (await EnsureInfosAsync(ordered).ConfigureAwait(true)).ToDictionary(i => i.FileId);
            var entries = ordered.Where(infos.ContainsKey).Select(id => infos[id])
                .Select(f => new LightboxEntry(f.FileId, f.Path, f.Name, f.MediaType, f.IsFavorite, f.Rating, f.Size, f.ModifiedAtUtc))
                .ToList();
            var start = entries.FindIndex(e => e.FileId == startFileId);
            if (entries.Count > 0)
            {
                _lightbox.Open(entries, Math.Max(0, start), System.Windows.Application.Current?.MainWindow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Lightbox nicht geöffnet");
            ShowMessage("Das Foto konnte nicht geöffnet werden: " + ex.Message, isError: true);
        }
    }

    // ---- Zoom-Knöpfe ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ZoomIn() => Viewport.ZoomAt(Math.Floor(Viewport.Zoom + 1e-9) + 1, Viewport.Width / 2, Viewport.Height / 2);

    [RelayCommand]
    private void ZoomOut() => Viewport.ZoomAt(Math.Ceiling(Viewport.Zoom - 1e-9) - 1, Viewport.Width / 2, Viewport.Height / 2);

    /// <summary>Bereich aller verorteten Fotos plus Rand (wie beim Öffnen).</summary>
    [RelayCommand]
    private void ShowAllPhotos() => Viewport.FitBounds(Index.GetExtent());

    [RelayCommand]
    private void ShowWorld() => Viewport.ShowWorld();

    // ---- Kachelquelle --------------------------------------------------------------------------------------------------

    private void UpdateTileStatus()
    {
        TileStatusText = Tiles?.State switch
        {
            MapTileSourceState.Offline => "Offline — Karte aus dem Cache, Gruppen funktionieren weiter",
            MapTileSourceState.KeyRejected => "API-Schlüssel abgelehnt — in den Einstellungen prüfen (Karte aus dem Cache)",
            MapTileSourceState.Disabled or null => "Kartenkacheln abgeschaltet — Karte ohne Hintergrund",
            _ => string.Empty,
        };
        IsHiddenNotice = _privacy.IsHiddenCatalog && Tiles is { State: not MapTileSourceState.Disabled };
    }

    private void ShowMessage(string message, bool isError)
    {
        Message = message;
        MessageIsError = isError;
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;
}

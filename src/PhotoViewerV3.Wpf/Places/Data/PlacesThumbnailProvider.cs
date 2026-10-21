using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Places.Data;

/// <summary>Vorschaubilder für Karten-Marker und die Seitenleiste „Ohne Ort“.</summary>
public interface IPlacesImageSource
{
    /// <summary>Fertige Vorschaubilder (UI-Thread, gebündelt): FileIds, deren Bild jetzt über <see cref="TryGet"/> kommt.</summary>
    event Action<IReadOnlyList<int>>? ImagesReady;

    ImageSource? TryGet(int fileId);

    /// <summary>Vorschaubilder anfordern (nur fehlende werden erzeugt bzw. geladen).</summary>
    void Request(IReadOnlyList<PlacesFileInfo> files);
}

/// <summary>
/// Vorschaubilder über die Thumbnail-Pipeline (gleiche Cache-Dateien wie die Galerie), klein dekodiert (Marker 52 px,
/// Seitenleiste 96 px, × Bildschirm-Skalierung bis 2), eingefroren und GEBÜNDELT mit Background-Priorität an den
/// UI-Thread (CLAUDE.md). Speicher-Cache bis 3 000 Bilder (älteste fallen heraus).
/// </summary>
public sealed class PlacesThumbnailProvider : IPlacesImageSource, IDisposable
{
    private const int Capacity = 3000;
    private static readonly ThumbnailDecodeBox Box = new(192, 192);

    private readonly IThumbnailService _thumbnails;
    private readonly ConcurrentDictionary<int, byte> _wanted = new();
    private readonly ConcurrentQueue<(int FileId, ImageSource Image)> _ready = new();
    private readonly Dictionary<int, ImageSource> _cache = new();
    private readonly Queue<int> _order = new();
    private int _flushScheduled;

    public PlacesThumbnailProvider(IThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
        _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    public event Action<IReadOnlyList<int>>? ImagesReady;

    public ImageSource? TryGet(int fileId)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(fileId, out var image) ? image : null;
        }
    }

    public void Request(IReadOnlyList<PlacesFileInfo> files)
    {
        var requests = new List<ThumbnailRequest>();
        foreach (var file in files)
        {
            if (TryGet(file.FileId) is not null || !_wanted.TryAdd(file.FileId, 0))
            {
                continue;
            }

            requests.Add(new ThumbnailRequest(file.LibraryId, file.FileId, file.Path, file.MediaType, file.ModifiedAtUtc));
        }

        if (requests.Count > 0)
        {
            _ = _thumbnails.RequestThumbnailsAsync(requests);
        }
    }

    private void OnThumbnailReady(ThumbnailReadyEventArgs args)
    {
        if (!_wanted.TryRemove(args.FileId, out _))
        {
            return; // Galerie-Vorschaubild, nicht für die Karte angefordert
        }

        if (GalleryViewModel.TryLoadThumbnail(args.ThumbnailPath, Box) is not { } image)
        {
            return;
        }

        _ready.Enqueue((args.FileId, image));
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Flush();
            return;
        }

        _ = dispatcher.InvokeAsync(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        var ids = new List<int>();
        lock (_cache)
        {
            while (_ready.TryDequeue(out var entry))
            {
                if (_cache.TryAdd(entry.FileId, entry.Image))
                {
                    _order.Enqueue(entry.FileId);
                }

                ids.Add(entry.FileId);
            }

            while (_cache.Count > Capacity && _order.TryDequeue(out var old))
            {
                _cache.Remove(old);
            }
        }

        if (ids.Count > 0)
        {
            ImagesReady?.Invoke(ids);
        }
    }

    public void Dispose() => _thumbnails.ThumbnailReady -= OnThumbnailReady;
}

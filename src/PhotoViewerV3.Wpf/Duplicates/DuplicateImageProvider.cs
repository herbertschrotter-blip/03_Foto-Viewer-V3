using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Thumbnails;

namespace PhotoViewerV3.Duplicates;

/// <summary>Vorschaubilder der Seite „Doppelte“ (nur für sichtbare Gruppen angefordert).</summary>
public interface IDuplicateImageSource
{
    /// <summary>Fertige Bilder (UI-Thread, gebündelt): FileIds, deren Bild jetzt über <see cref="TryGet"/> kommt.</summary>
    event Action<IReadOnlyList<int>>? ImagesReady;

    ImageSource? TryGet(int fileId);

    void Request(IEnumerable<DuplicateMember> members);
}

/// <summary>
/// Über die Thumbnail-Pipeline (gleiche Cache-Dateien wie die Galerie), klein dekodiert (Karte 220 × 165 × Skalierung),
/// eingefroren und gebündelt mit Background-Priorität an den UI-Thread (CLAUDE.md). Speicher-Cache bis 1 500 Bilder.
/// </summary>
public sealed class DuplicateImageProvider : IDuplicateImageSource, IDisposable
{
    private const int Capacity = 1500;
    private static readonly ThumbnailDecodeBox Box = new(330, 248);

    private readonly IThumbnailService _thumbnails;
    private readonly ConcurrentDictionary<int, byte> _wanted = new();
    private readonly ConcurrentQueue<(int FileId, ImageSource Image)> _ready = new();
    private readonly Dictionary<int, ImageSource> _cache = new();
    private readonly Queue<int> _order = new();
    private int _flushScheduled;

    public DuplicateImageProvider(IThumbnailService thumbnails)
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

    public void Request(IEnumerable<DuplicateMember> members)
    {
        var requests = new List<ThumbnailRequest>();
        foreach (var m in members)
        {
            if (TryGet(m.FileId) is not null || !_wanted.TryAdd(m.FileId, 0))
            {
                continue;
            }

            requests.Add(new ThumbnailRequest(m.LibraryId, m.FileId, m.FullPath, m.MediaType, m.ModifiedAtUtc));
        }

        if (requests.Count > 0)
        {
            _ = _thumbnails.RequestThumbnailsAsync(requests);
        }
    }

    private void OnThumbnailReady(ThumbnailReadyEventArgs args)
    {
        if (!_wanted.TryRemove(args.FileId, out _) || GalleryViewModel.TryLoadThumbnail(args.ThumbnailPath, Box) is not { } image)
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

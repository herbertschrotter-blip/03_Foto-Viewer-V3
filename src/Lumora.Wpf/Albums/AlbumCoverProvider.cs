using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Albums;
using Lumora.Services.Thumbnails;

namespace Lumora.Albums;

/// <summary>
/// Titelbilder der Seite „Alben“ (ADR-0054) über die Vorschaubild-Pipeline (gleiche Cache-Dateien wie die Galerie),
/// klein dekodiert, eingefroren und GEBÜNDELT mit Background-Priorität an den UI-Thread (CLAUDE.md).
/// </summary>
public sealed class AlbumCoverProvider : IDisposable
{
    private static readonly ThumbnailDecodeBox Box = new(440, 330);

    private readonly IThumbnailService _thumbnails;
    private readonly ConcurrentDictionary<int, byte> _wanted = new();
    private readonly ConcurrentQueue<(int FileId, ImageSource Image)> _ready = new();
    private readonly Dictionary<int, ImageSource> _cache = new();
    private int _flushScheduled;

    public AlbumCoverProvider(IThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
        _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    /// <summary>Fertige Titelbilder (UI-Thread): FileIds, deren Bild jetzt über <see cref="TryGet"/> kommt.</summary>
    public event Action<IReadOnlyList<int>>? ImagesReady;

    public ImageSource? TryGet(int fileId)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(fileId, out var image) ? image : null;
        }
    }

    public void Request(IEnumerable<AlbumCover> covers)
    {
        var requests = new List<ThumbnailRequest>();
        foreach (var cover in covers)
        {
            if (TryGet(cover.FileId) is null && _wanted.TryAdd(cover.FileId, 0))
            {
                requests.Add(new ThumbnailRequest(cover.LibraryId, cover.FileId, cover.Path, cover.MediaType, cover.ModifiedAtUtc));
            }
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
                _cache[entry.FileId] = entry.Image;
                ids.Add(entry.FileId);
            }
        }

        if (ids.Count > 0)
        {
            ImagesReady?.Invoke(ids);
        }
    }

    public void Dispose() => _thumbnails.ThumbnailReady -= OnThumbnailReady;
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Thumbnails;

namespace Lumora.Stories;

/// <summary>Vorschaubilder der Story-Seite (Karten, Collage-Kacheln); Fakes in Tests.</summary>
public interface IStoryImageSource
{
    /// <summary>Fertige Bilder (UI-Thread, gebündelt): FileIds, deren Bild jetzt über <see cref="TryGet"/> kommt.</summary>
    event Action<IReadOnlyList<int>>? ImagesReady;

    /// <summary>Bild in der größten schon geladenen Größe (mindestens <paramref name="large"/>, falls vorhanden).</summary>
    ImageSource? TryGet(int fileId, bool large = false);

    /// <summary>Vorschaubilder anfordern; schon geladene werden nicht erneut angefordert.</summary>
    void Request(IEnumerable<StoryMediaInfo> files, bool large = false);
}

/// <summary>
/// Über die Vorschaubild-Pipeline (gleiche Cache-Dateien wie die Galerie, ADR-0043), dekodiert auf zwei Zielgrößen —
/// klein 330 × 248 (Karten, dichte Collage) und groß 660 × 440 (große Plätze der Collage-Blöcke, Rückblick-Banner; das
/// Vorschaubild hat ohnehin höchstens 400 px kurze Kante) —, eingefroren und GEBÜNDELT mit Background-Priorität an den
/// UI-Thread (CLAUDE.md). Speicher-Cache bis 900 Bilder (älteste fallen heraus).
/// </summary>
public sealed class StoryImageProvider : IStoryImageSource, IDisposable
{
    private const int Capacity = 900;
    private static readonly ThumbnailDecodeBox SmallBox = new(330, 248);
    private static readonly ThumbnailDecodeBox LargeBox = new(660, 440);

    private readonly IThumbnailService _thumbnails;
    /// <summary>Unbeantwortete Anforderung gilt nach dieser Zeit als fehlgeschlagen (die Pipeline meldet Fehler nicht) und darf erneut.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<int, (bool Large, DateTime At)> _wanted = new(); // FileId → groß gewünscht, angefragt um
    private readonly ConcurrentQueue<(int FileId, bool Large, ImageSource Image)> _ready = new();
    private readonly Dictionary<int, (ImageSource Image, bool Large)> _cache = new();
    private readonly Queue<int> _order = new();
    private int _flushScheduled;

    public StoryImageProvider(IThumbnailService thumbnails)
    {
        _thumbnails = thumbnails;
        _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    public event Action<IReadOnlyList<int>>? ImagesReady;

    public ImageSource? TryGet(int fileId, bool large = false)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(fileId, out var entry) ? entry.Image : null;
        }
    }

    private bool HasAtLeast(int fileId, bool large)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(fileId, out var entry) && (entry.Large || !large);
        }
    }

    public void Request(IEnumerable<StoryMediaInfo> files, bool large = false)
    {
        var requests = new List<ThumbnailRequest>();
        foreach (var file in files)
        {
            if (HasAtLeast(file.FileId, large))
            {
                continue;
            }

            var send = false;
            var now = DateTime.UtcNow;
            _wanted.AddOrUpdate(
                file.FileId,
                _ => { send = true; return (large, now); },
                (_, old) =>
                {
                    if (now - old.At < RetryAfter)
                    {
                        return (old.Large || large, old.At);
                    }

                    send = true; // alte Anforderung ohne Antwort (z. B. Laufwerk war weg): erneut
                    return (old.Large || large, now);
                });
            if (send)
            {
                requests.Add(new ThumbnailRequest(file.LibraryId, file.FileId, file.Path, file.MediaType, file.ModifiedAtUtc));
            }
        }

        if (requests.Count > 0)
        {
            _ = _thumbnails.RequestThumbnailsAsync(requests);
        }
    }

    private void OnThumbnailReady(ThumbnailReadyEventArgs args)
    {
        if (!_wanted.TryRemove(args.FileId, out var wanted))
        {
            return; // Galerie-Vorschaubild, nicht für die Storys angefordert
        }

        var large = wanted.Large;
        if (GalleryViewModel.TryLoadThumbnail(args.ThumbnailPath, large ? LargeBox : SmallBox) is not { } image)
        {
            return;
        }

        _ready.Enqueue((args.FileId, large, image));
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
                if (!_cache.ContainsKey(entry.FileId))
                {
                    _order.Enqueue(entry.FileId);
                }

                if (!_cache.TryGetValue(entry.FileId, out var existing) || entry.Large || !existing.Large)
                {
                    _cache[entry.FileId] = (entry.Image, entry.Large);
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

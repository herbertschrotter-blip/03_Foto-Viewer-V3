using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.ListView;
using Lumora.Slideshow;

namespace Lumora.Wpf.Tests.Slideshow;

/// <summary>Steuerbare Zeit mit Zeitgebern: <see cref="Advance"/> löst fällige Zeitgeber der Reihe nach aus.</summary>
internal sealed class FakeTime : TimeProvider
{
    private readonly List<FakeTimer> _timers = new();
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).AddTicks(_ticks);

    public int ActiveTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count(t => t.DueTicks is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _ticks + by.Ticks;
        while (true)
        {
            FakeTimer? next;
            lock (_timers)
            {
                next = _timers.Where(t => t.DueTicks is { } due && due <= target).OrderBy(t => t.DueTicks).FirstOrDefault();
            }

            if (next is null)
            {
                break;
            }

            _ticks = Math.Max(_ticks, next.DueTicks!.Value);
            next.Fire();
        }

        _ticks = target;
    }

    private void Remove(FakeTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public long? DueTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
            return true;
        }

        public void Fire()
        {
            DueTicks = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : DueTicks + _period.Ticks;
            callback(state);
        }

        public void Dispose()
        {
            DueTicks = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Datei-Infos aus einer Tabelle; zählt die Stapel-Aufrufe.</summary>
internal sealed class FakeItemLoader : ISlideshowItemLoader
{
    public Dictionary<int, SlideshowItemInfo> Items { get; } = new();

    public int Calls { get; private set; }

    public int MaxBatch { get; private set; }

    public Task<IReadOnlyDictionary<int, SlideshowItemInfo>> LoadAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        Calls++;
        MaxBatch = Math.Max(MaxBatch, fileIds.Count);
        var result = fileIds.Where(Items.ContainsKey).ToDictionary(id => id, id => Items[id]);
        return Task.FromResult<IReadOnlyDictionary<int, SlideshowItemInfo>>(result);
    }

    public SlideshowItemInfo AddImage(int id, bool missing = false, IReadOnlyList<SlideshowFaceBox>? faces = null)
    {
        var info = new SlideshowItemInfo(id, $@"C:\fake\{id}.jpg", MediaType.Image, missing, null, null, faces ?? []);
        Items[id] = info;
        return info;
    }

    public SlideshowItemInfo AddVideo(int id, int? durationMs = null)
    {
        var info = new SlideshowItemInfo(id, $@"C:\fake\{id}.mp4", MediaType.Video, false, null, null, [], durationMs);
        Items[id] = info;
        return info;
    }
}

/// <summary>Liefert kleine eingefrorene Bitmaps; Pfade in <see cref="Broken"/> werfen wie eine fehlende Datei.</summary>
internal sealed class FakeImageDecoder : ISlideshowImageDecoder
{
    public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Decoded { get; } = new();

    public Task<SlideshowImage> DecodeAsync(string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        lock (Decoded)
        {
            Decoded.Add(path);
        }

        if (Broken.Contains(path))
        {
            return Task.FromException<SlideshowImage>(new FileNotFoundException("fehlt", path));
        }

        return Task.FromResult(new SlideshowImage(CreateBitmap(4, 3)));
    }

    public static BitmapSource CreateBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>Video-Player ohne LibVLC: startet, wenn der Pfad nicht in <see cref="Broken"/> steht.</summary>
internal sealed class FakeVideoPlayer : ISlideshowVideoPlayer
{
    public event EventHandler? Ended;

    public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Playing { get; private set; }

    public bool IsPaused { get; private set; }

    public int Stops { get; private set; }

    public Task<bool> PlayAsync(string path, CancellationToken ct)
    {
        if (Broken.Contains(path))
        {
            return Task.FromResult(false);
        }

        Playing = path;
        IsPaused = false;
        return Task.FromResult(true);
    }

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    public void Stop()
    {
        Stops++;
        Playing = null;
    }

    public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }
}

/// <summary>Key-Value-Speicher im Speicher (Settings-Tabelle).</summary>
internal sealed class MemoryViewSettingsStore : IGalleryViewSettingsStore
{
    public Dictionary<string, string?> Values { get; } = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>Reihenfolge, die mitzählt, welche Bereiche abgefragt wurden.</summary>
internal sealed class CountingSequence(IReadOnlyList<int> ids) : ISlideshowSequence
{
    private readonly ListSlideshowSequence _inner = new(ids);

    public List<(int Start, int Count)> Requests { get; } = new();

    public int Count => _inner.Count;

    public Task<IReadOnlyList<int>> GetRangeAsync(int start, int count, CancellationToken ct)
    {
        Requests.Add((start, count));
        return _inner.GetRangeAsync(start, count, ct);
    }
}

/// <summary>Baut ein ViewModel mit Fakes (Rückrufe synchron, keine Fenster).</summary>
internal sealed class SlideshowHarness : IDisposable
{
    public SlideshowHarness(IReadOnlyList<int> ids, int startIndex = 0, SlideshowSettings? settings = null, bool withVideo = true, ISlideshowSettingsStore? store = null)
    {
        Sequence = new CountingSequence(ids);
        Video = withVideo ? new FakeVideoPlayer() : null;
        ViewModel = new SlideshowViewModel(
            Sequence,
            startIndex,
            "Test",
            settings ?? SlideshowSettings.Default,
            Loader,
            Decoder,
            Video,
            Time,
            action => action(),
            NullLogger.Instance,
            store);
        ViewModel.SlideChanged += (_, slide) => Slides.Add(slide);
    }

    public FakeTime Time { get; } = new();

    public FakeItemLoader Loader { get; } = new();

    public FakeImageDecoder Decoder { get; } = new();

    public FakeVideoPlayer? Video { get; }

    public CountingSequence Sequence { get; }

    public SlideshowViewModel ViewModel { get; }

    public List<SlideshowSlide> Slides { get; } = new();

    public int? CurrentFileId => ViewModel.CurrentSlide?.Info.FileId;

    public void Dispose() => ViewModel.Dispose();
}

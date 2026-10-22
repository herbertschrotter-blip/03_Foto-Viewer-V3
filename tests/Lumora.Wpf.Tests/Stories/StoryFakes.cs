using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Services.Albums;
using Lumora.Services.Quality;
using Lumora.Services.Stories;
using Lumora.Shell;
using Lumora.Slideshow;
using Lumora.Stories;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.Stories;

/// <summary>Story-Dienst im Speicher: Karten, Details, Highlights, Rückblicke; merkt Aufrufe (ADR-0060-Tests).</summary>
internal sealed class FakeStoryService : IStoryService
{
    private int _nextId = 1000;

    public List<StoryCard> Cards { get; } = [];

    public Dictionary<int, StoryDetails> Details { get; } = [];

    public Dictionary<int, List<HighlightPick>> Highlights { get; } = [];

    public List<StoryMemory> Memories { get; } = [];

    public List<string> Calls { get; } = [];

    public List<int> Seen { get; } = [];

    public (int StoryId, IReadOnlyList<int> FileIds)? Created { get; private set; }

    public static StoryCard Card(int id, StoryKind kind, string title, int year = 2025, bool pinned = false, bool hidden = false, bool seen = true,
        int files = 20, int videos = 1, string? subtitle = "Grado · Triest — Italien", int? personId = null, string? customTitle = null, int days = 1)
    {
        var first = new DateOnly(year, 7, 12);
        var last = first.AddDays(days - 1);
        var label = StoryTitleBuilder.KindLabel(kind, false);
        return new StoryCard(id, kind, label, customTitle ?? title, title, subtitle, StoryTitleBuilder.Counts(files, videos), files, videos,
            first.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc), last.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc), first, last, year, days,
            id * 100, false, pinned, hidden, seen, false, customTitle is not null, personId, null, DateTime.UtcNow);
    }

    /// <summary>Story mit Tagen zu je <paramref name="perDay"/> Fotos (Ids storyId*100 + n), jedes dritte Highlight.</summary>
    public StoryCard Add(StoryCard card, int dayCount = 1, int perDay = 6)
    {
        Cards.Add(card);
        var days = new List<StoryDay>();
        var all = new List<int>();
        for (var d = 0; d < dayCount; d++)
        {
            var ids = Enumerable.Range(0, perDay).Select(n => card.Id * 100 + d * perDay + n).ToList();
            all.AddRange(ids);
            var date = card.FirstDay.AddDays(d);
            days.Add(new StoryDay(d + 1, date, StoryTitleBuilder.Day(date, withYear: false), StoryTitleBuilder.ShortDay(date), "Grado", ids, perDay, 0));
        }

        Details[card.Id] = new StoryDetails(card, card.KindLabel + (dayCount > 1 ? $" · {dayCount} Tage" : string.Empty), [new StoryPlace(1, "Grado", "Italien")],
            [new StoryPerson(1, "Anna", 5), new StoryPerson(2, "Max", 3)], days, all);
        Highlights[card.Id] = all.Where((_, i) => i % 3 == 0).Select(id => new HighlightPick(id, 1.0, "scharf · Favorit", id % 2 == 0 ? 2 : 0)).ToList();
        return card;
    }

    private StoryCard? Find(int id) => Cards.FirstOrDefault(c => c.Id == id);

    private void Replace(int id, Func<StoryCard, StoryCard> change)
    {
        var index = Cards.FindIndex(c => c.Id == id);
        if (index >= 0)
        {
            Cards[index] = change(Cards[index]);
            if (Details.TryGetValue(id, out var details))
            {
                Details[id] = details with { Card = Cards[index] };
            }
        }
    }

    public Task<StoryOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        Calls.Add("overview");
        var visible = Cards.Where(c => !c.IsHidden).ToList();
        var events = visible.Where(c => c.Kind is StoryKind.Event or StoryKind.Trip).OrderByDescending(c => c.StartAt).ToList();
        var unseen = visible.Where(c => !c.IsSeen && c.Kind != StoryKind.Memory).ToList();
        return Task.FromResult(new StoryOverview(
            Memories,
            visible.Where(c => c.IsPinned).ToList(),
            unseen.Take(12).ToList(),
            unseen.Count,
            events,
            events.GroupBy(c => c.Year).OrderByDescending(g => g.Key).Select(g => new StoryYearCount(g.Key, g.Count())).ToList(),
            visible.Where(c => c.Kind == StoryKind.Person).ToList(),
            visible.Where(c => c.Kind == StoryKind.Place).ToList(),
            visible.Where(c => c.Kind == StoryKind.Custom).ToList(),
            Cards.Count(c => c.IsHidden),
            new StoryKindCounts(visible.Count(c => c.Kind != StoryKind.Memory) + Memories.Count, events.Count, Memories.Count,
                visible.Count(c => c.Kind == StoryKind.Person), visible.Count(c => c.Kind == StoryKind.Place), visible.Count(c => c.Kind == StoryKind.Custom)),
            visible.Sum(c => c.FileCount)));
    }

    public Task<IReadOnlyList<StoryCard>> GetHiddenAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoryCard>>(Cards.Where(c => c.IsHidden).ToList());

    public Task<StoryCard?> GetAsync(int storyId, CancellationToken ct = default) => Task.FromResult(Find(storyId));

    public Task<StoryDetails?> GetDetailsAsync(int storyId, CancellationToken ct = default)
        => Task.FromResult(Details.TryGetValue(storyId, out var d) ? d : null);

    public Task<IReadOnlyList<HighlightPick>> GetHighlightsAsync(int storyId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HighlightPick>>(Highlights.TryGetValue(storyId, out var h) ? h : []);

    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetPreviewFileIdsAsync(IReadOnlyCollection<int> storyIds, int perStory = 3, CancellationToken ct = default)
    {
        Calls.Add("previews:" + string.Join(",", storyIds));
        return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(storyIds.ToDictionary(
            id => id, id => (IReadOnlyList<int>)(Details.TryGetValue(id, out var d) ? d.FileIds.Take(perStory).ToList() : [])));
    }

    public Task<IReadOnlyList<StoryCard>> GetStoriesForFileAsync(int fileId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoryCard>>(Cards.Where(c => Details.TryGetValue(c.Id, out var d) && d.FileIds.Contains(fileId)).ToList());

    public Task<IReadOnlyList<StoryMemory>> GetMemoriesAsync(DateOnly? day = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoryMemory>>(Memories);

    public Task<int> OpenMemoryAsync(string key, CancellationToken ct = default)
    {
        Calls.Add("openMemory:" + key);
        var memory = Memories.First(m => m.Key == key);
        if (memory.StoryId is { } existing)
        {
            return Task.FromResult(existing);
        }

        var card = Add(Card(_nextId++, StoryKind.Memory, memory.Title, memory.Date.Year), dayCount: 1, perDay: 8);
        return Task.FromResult(card.Id);
    }

    public Task SetPinnedAsync(int storyId, bool pinned, CancellationToken ct = default)
    {
        Calls.Add($"pin:{storyId}:{pinned}");
        Replace(storyId, c => c with { IsPinned = pinned });
        return Task.CompletedTask;
    }

    public Task SetHiddenAsync(int storyId, bool hidden, CancellationToken ct = default)
    {
        Calls.Add($"hide:{storyId}:{hidden}");
        Replace(storyId, c => c with { IsHidden = hidden });
        return Task.CompletedTask;
    }

    public Task MarkSeenAsync(IReadOnlyCollection<int> storyIds, CancellationToken ct = default)
    {
        Seen.AddRange(storyIds);
        foreach (var id in storyIds)
        {
            Replace(id, c => c with { IsSeen = true });
        }

        return Task.CompletedTask;
    }

    public Task MarkAllSeenAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SetCustomTitleAsync(int storyId, string? title, CancellationToken ct = default)
    {
        Calls.Add($"title:{storyId}:{title ?? "<null>"}");
        Replace(storyId, c => c with { Title = title ?? c.AutoTitle, HasCustomTitle = title is not null });
        return Task.CompletedTask;
    }

    public Task SetCoverAsync(int storyId, int? fileId, CancellationToken ct = default)
    {
        Calls.Add($"cover:{storyId}:{fileId?.ToString() ?? "auto"}");
        Replace(storyId, c => c with { CoverFileId = fileId ?? c.Id * 100, CoverIsManual = fileId is not null });
        return Task.CompletedTask;
    }

    public Task<int> AddFilesAsync(int storyId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        Calls.Add($"add:{storyId}:{fileIds.Count}");
        return Task.FromResult(fileIds.Count);
    }

    public Task<int> RemoveFilesAsync(int storyId, IReadOnlyList<int> fileIds, CancellationToken ct = default)
    {
        Calls.Add($"remove:{storyId}:{string.Join(",", fileIds)}");
        return Task.FromResult(fileIds.Count);
    }

    public Task<int> CreateFromSelectionAsync(IReadOnlyList<int> fileIds, string? title = null, CancellationToken ct = default)
    {
        var card = Add(Card(_nextId++, StoryKind.Custom, title ?? "12. Juli 2025", customTitle: title));
        Created = (card.Id, fileIds);
        return Task.FromResult(card.Id);
    }

    public Task DeleteAsync(int storyId, CancellationToken ct = default)
    {
        Calls.Add($"delete:{storyId}");
        Cards.RemoveAll(c => c.Id == storyId);
        return Task.CompletedTask;
    }

    public Task<AlbumInfo> SaveAsAlbumAsync(int storyId, string? albumName = null, bool highlightsOnly = false, CancellationToken ct = default)
    {
        Calls.Add($"album:{storyId}:{highlightsOnly}");
        return Task.FromResult(new AlbumInfo(1, albumName ?? Find(storyId)!.Title, AlbumKind.Manual, 5, null, null, DateTime.UtcNow, DateTime.UtcNow));
    }

    public Task RecomputeAsync(int? storyId = null, CancellationToken ct = default)
    {
        Calls.Add($"recompute:{storyId}");
        return Task.CompletedTask;
    }
}

/// <summary>Datei-Infos ohne Datenbank: Querformat 3:2, jede Id mit Endung 5 ein Video, jede mit 9 ein Panorama.</summary>
internal sealed class FakeStoryMedia : IStoryMediaLoader
{
    public List<IReadOnlyCollection<int>> Requests { get; } = [];

    public Func<int, (int W, int H)> Size { get; set; } = id => id % 10 == 9 ? (6000, 1500) : (3000, 2000);

    public Task<IReadOnlyDictionary<int, StoryMediaInfo>> LoadAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        Requests.Add(fileIds);
        IReadOnlyDictionary<int, StoryMediaInfo> map = fileIds.Distinct().ToDictionary(id => id, id =>
        {
            var (w, h) = Size(id);
            var video = id % 10 == 5;
            return new StoryMediaInfo(id, 1, $@"C:\fake\{id}.{(video ? "mp4" : "jpg")}", $"{id}.{(video ? "mp4" : "jpg")}", video ? MediaType.Video : MediaType.Image,
                1000, DateTime.UtcNow, w, h, id % 7 == 0, null, video ? 42_000 : null, new DateTime(2025, 7, 12, 8 + (id % 100 % 14), 0, 0), null);
        });
        return Task.FromResult(map);
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyList<StoryAvatarInfo>>> LoadPersonsAsync(IReadOnlyCollection<int> storyIds, int perStory = 3, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<StoryAvatarInfo>>>(storyIds.ToDictionary(
            id => id, id => (IReadOnlyList<StoryAvatarInfo>)[new StoryAvatarInfo(1, "Anna", 5, 11, 1), new StoryAvatarInfo(2, "Max", 3, null, 1)]));
}

/// <summary>Bilder sofort als 1×1-Bitmap (eingefroren); merkt Anforderungen.</summary>
internal sealed class FakeStoryImages : IStoryImageSource
{
    private readonly Dictionary<int, ImageSource> _images = [];

    public List<int> Requested { get; } = [];

    public List<int> RequestedLarge { get; } = [];

    public bool Deliver { get; set; } = true;

    public event Action<IReadOnlyList<int>>? ImagesReady;

    public ImageSource? TryGet(int fileId, bool large = false) => _images.GetValueOrDefault(fileId);

    public void Request(IEnumerable<StoryMediaInfo> files, bool large = false)
    {
        var ids = files.Select(f => f.FileId).ToList();
        (large ? RequestedLarge : Requested).AddRange(ids);
        if (!Deliver)
        {
            return;
        }

        foreach (var id in ids)
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            _images[id] = bitmap;
        }

        ImagesReady?.Invoke(ids);
    }
}

internal sealed class RecordingSlideshow : ISlideshowService
{
    public List<(IReadOnlyList<int> Ids, int Start, string? Title)> Starts { get; } = [];

    public bool IsOpen => false;

    public void Start(IReadOnlyList<int> fileIds, int startIndex = 0, string? title = null) => Starts.Add((fileIds, startIndex, title));

    public void Start(ISlideshowSequence sequence, int startIndex = 0, string? title = null) => throw new NotSupportedException();
}

internal sealed class RecordingStoryLightbox : ILightboxService
{
    public List<(IReadOnlyList<LightboxEntry> Entries, int Start)> Opens { get; } = [];

    public bool IsOpen => false;

    public void Open(IReadOnlyList<LightboxEntry> entries, int startIndex, Window? owner = null) => Opens.Add((entries, startIndex));
}

/// <summary>Baut die Story-Seite mit Fakes.</summary>
internal sealed class StoryTestKit
{
    public FakeStoryService Stories { get; } = new();

    public FakeStoryMedia Media { get; } = new();

    public FakeStoryImages Images { get; } = new();

    public ScriptedDialogs Dialogs { get; } = new();

    public RecordingNavigationService Navigation { get; } = new();

    public RecordingSlideshow Slideshow { get; } = new();

    public RecordingStoryLightbox Lightbox { get; } = new();

    public CommunityToolkit.Mvvm.Messaging.IMessenger Messenger { get; } = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();

    public StoriesNavigationBadge Badge { get; } = new();

    public StoryGalleryBridge Bridge => _bridge ??= new StoryGalleryBridge(Stories, Dialogs, Navigation, NullLogger<StoryGalleryBridge>.Instance, Messenger);

    private StoryGalleryBridge? _bridge;

    public StoryPageServices Services => new(Stories, Media, Images, Dialogs, Navigation, faces: null, Slideshow, Lightbox, places: null, Bridge);

    public StoriesViewModel CreateOverview() => new(Services, NullLogger<StoriesViewModel>.Instance, Badge, Messenger);

    public StoryViewModel CreateStory(int id, Action? close = null)
        => new(Services, id, close ?? (() => { }), NullLogger<StoryViewModel>.Instance, Messenger);
}

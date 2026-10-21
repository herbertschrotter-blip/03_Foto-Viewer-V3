using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Slideshow;

/// <summary>Feste Liste von Datei-Ids (Auswahl, Album, Lightbox-Sequenz).</summary>
public sealed class ListSlideshowSequence : ISlideshowSequence
{
    private readonly IReadOnlyList<int> _ids;

    public ListSlideshowSequence(IReadOnlyList<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _ids = ids;
    }

    public int Count => _ids.Count;

    public Task<IReadOnlyList<int>> GetRangeAsync(int start, int count, CancellationToken ct)
    {
        if (start < 0 || start >= _ids.Count || count <= 0)
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        var end = Math.Min(_ids.Count, start + count);
        var result = new int[end - start];
        for (var i = start; i < end; i++)
        {
            result[i - start] = _ids[i];
        }

        return Task.FromResult<IReadOnlyList<int>>(result);
    }
}

/// <summary>
/// Seitenweise geladene Reihenfolge (ganze Galerie-Ansicht in Galerie-Sortierung): lädt nur die Seiten, die die Diashow
/// gerade braucht, und hält wenige davon vor. Die Seiten kommen vom Aufrufer (Galerie-SQL samt Filter und Sortierung).
/// </summary>
public sealed class PagedSlideshowSequence : ISlideshowSequence
{
    private const int CachedPages = 4;

    private readonly Func<int, int, CancellationToken, Task<IReadOnlyList<int>>> _loadPage;
    private readonly int _pageSize;
    private readonly Dictionary<int, IReadOnlyList<int>> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="count">Trefferzahl beim Start.</param>
    /// <param name="pageSize">Seitengröße der Abfrage.</param>
    /// <param name="loadPage">
    /// (Seitenindex, Seitengröße, Abbruch) → Ids der Seite in Anzeige-Reihenfolge; Abstand vom Anfang = Index × Größe. Für
    /// Einzelabrufe ruft die Reihenfolge sie mit (Position, 1) auf.
    /// </param>
    public PagedSlideshowSequence(int count, int pageSize, Func<int, int, CancellationToken, Task<IReadOnlyList<int>>> loadPage)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
        Count = Math.Max(0, count);
        _pageSize = Math.Max(1, pageSize);
        _loadPage = loadPage;
    }

    public int Count { get; }

    /// <summary>Zahl der bisher geladenen Seiten (Tests: nicht alles auf einmal).</summary>
    public int PagesLoaded { get; private set; }

    public async Task<IReadOnlyList<int>> GetRangeAsync(int start, int count, CancellationToken ct)
    {
        if (start < 0 || start >= Count || count <= 0)
        {
            return [];
        }

        if (count == 1 && !await IsPageCachedAsync(start / _pageSize, ct).ConfigureAwait(false))
        {
            // Einzelabruf (zufällige Reihenfolge): eine Zeile an genau dieser Stelle statt einer ganzen Seite
            var one = await _loadPage(start, 1, ct).ConfigureAwait(false);
            SingleLoads++;
            return one.Count > 0 ? [one[0]] : [];
        }

        var end = Math.Min(Count, start + count);
        var result = new List<int>(end - start);
        var index = start;
        while (index < end)
        {
            var pageIndex = index / _pageSize;
            var page = await GetPageAsync(pageIndex, ct).ConfigureAwait(false);
            var offset = index - (pageIndex * _pageSize);
            if (offset >= page.Count)
            {
                break; // Seite kürzer als erwartet (Katalog geändert) — Rest fehlt
            }

            var take = Math.Min(page.Count - offset, end - index);
            for (var i = 0; i < take; i++)
            {
                result.Add(page[offset + i]);
            }

            index += take;
            if (offset + take < _pageSize && index < end)
            {
                break; // Seite endet vor ihrer Größe — dahinter gibt es nichts mehr
            }
        }

        return result;
    }

    /// <summary>Zahl der Einzelabrufe (Tests).</summary>
    public int SingleLoads { get; private set; }

    private async Task<bool> IsPageCachedAsync(int pageIndex, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _pages.ContainsKey(pageIndex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<int>> GetPageAsync(int pageIndex, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pages.TryGetValue(pageIndex, out var cached))
            {
                _lru.Remove(pageIndex);
                _lru.AddFirst(pageIndex);
                return cached;
            }

            var page = await _loadPage(pageIndex, _pageSize, ct).ConfigureAwait(false);
            PagesLoaded++;
            _pages[pageIndex] = page;
            _lru.AddFirst(pageIndex);
            while (_pages.Count > CachedPages && _lru.Last is { } last)
            {
                _pages.Remove(last.Value);
                _lru.RemoveLast();
            }

            return page;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Zufällige Reihenfolge über einer anderen Reihenfolge (Einstellung „zufällig“): eine Permutation der Indizes, der
/// Start-Eintrag kommt zuerst. Die Ids holt sie einzeln aus der inneren Reihenfolge.
/// </summary>
public sealed class ShuffledSlideshowSequence : ISlideshowSequence
{
    private readonly ISlideshowSequence _inner;
    private readonly int[] _map;

    /// <param name="firstIndex">Index in <paramref name="inner"/>, der an Position 0 kommt (-1 = keiner).</param>
    public ShuffledSlideshowSequence(ISlideshowSequence inner, int firstIndex, Random random)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(random);
        _inner = inner;
        _map = Enumerable.Range(0, inner.Count).ToArray();
        random.Shuffle(_map);
        if (firstIndex >= 0 && firstIndex < _map.Length)
        {
            var at = Array.IndexOf(_map, firstIndex);
            (_map[0], _map[at]) = (_map[at], _map[0]);
        }
    }

    public int Count => _map.Length;

    /// <summary>Index in der inneren Reihenfolge für Position <paramref name="position"/>.</summary>
    public int InnerIndexAt(int position) => _map[position];

    public async Task<IReadOnlyList<int>> GetRangeAsync(int start, int count, CancellationToken ct)
    {
        if (start < 0 || start >= _map.Length || count <= 0)
        {
            return [];
        }

        var end = Math.Min(_map.Length, start + count);
        var result = new List<int>(end - start);
        for (var i = start; i < end; i++)
        {
            var one = await _inner.GetRangeAsync(_map[i], 1, ct).ConfigureAwait(false);
            result.Add(one.Count == 0 ? 0 : one[0]); // 0 = fehlt (innere Reihenfolge kürzer geworden), Position bleibt
        }

        return result;
    }
}

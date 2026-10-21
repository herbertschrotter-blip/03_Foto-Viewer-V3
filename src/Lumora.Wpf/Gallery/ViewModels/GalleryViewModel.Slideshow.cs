using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Slideshow;

namespace Lumora.Gallery.ViewModels;

/// <summary>
/// „Diashow“ in der Galerie (ADR-0059): mit Auswahl die ausgewählten Dateien in Seitenreihenfolge, sonst die ganze aktuelle
/// Ansicht (Filter, Suche, Ordner, Sortierung) — seitenweise nachgeladen, nie alle Ids auf einmal.
/// </summary>
public sealed partial class GalleryViewModel
{
    /// <summary>Seitengröße, mit der die Diashow die ganze Ansicht nachlädt.</summary>
    internal const int SlideshowPageSize = 200;

    private readonly ISlideshowService? _slideshow;

    /// <summary>True, wenn die Diashow verfügbar ist (Knopf und Kontextmenü).</summary>
    public bool HasSlideshow => _slideshow is not null;

    /// <summary>
    /// Knopf „Diashow“ und Kontextmenü: ab zwei ausgewählten Dateien nur die Auswahl (ab <paramref name="item"/>, falls
    /// darin); sonst die ganze Ansicht — ab der angeklickten bzw. einzigen ausgewählten Datei, ohne beides ab dem Anfang.
    /// </summary>
    [RelayCommand]
    private void StartSlideshow(GalleryItem? item)
    {
        if (_slideshow is null || !HasLibrary || TotalCount == 0)
        {
            return;
        }

        try
        {
            if (_selectedIds.Count > 1)
            {
                var ids = SelectedIdsInPageOrder();
                var start = item is null ? 0 : Math.Max(0, ids.IndexOf(item.Id));
                _logger.LogInformation("Diashow aus der Galerie: {Count} ausgewählte Dateien", ids.Count);
                _slideshow.Start(ids, start);
                return;
            }

            var anchor = item ?? (_selectedIds.Count == 1 ? CurrentPage.FirstOrDefault(i => _selectedIds.Contains(i.Id)) : null);
            var pageOffset = anchor is null ? -1 : CurrentPage.IndexOf(anchor);
            var startIndex = pageOffset < 0 ? 0 : Math.Min(TotalCount - 1, (PageIndex * PageSize) + pageOffset);
            var sequence = BuildViewSlideshowSequence();
            _logger.LogInformation("Diashow aus der Galerie: ganze Ansicht ({Count} Treffer, Start {Start})", sequence.Count, startIndex);
            _slideshow.Start(sequence, startIndex);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Diashow konnte nicht gestartet werden: {ex.Message}";
            _logger.LogError(ex, "Diashow aus der Galerie fehlgeschlagen");
        }
    }

    /// <summary>Ausgewählte Ids in der Reihenfolge der Seite (die Auswahl gilt je Seite).</summary>
    internal List<int> SelectedIdsInPageOrder()
        => CurrentPage.Where(i => _selectedIds.Contains(i.Id)).Select(i => i.Id).ToList();

    /// <summary>
    /// Die aktuelle Ansicht als seitenweise Reihenfolge: Abfrage und Trefferzahl beim Start eingefroren, jede Seite über
    /// dieselbe Galerie-SQL (<see cref="Data.GalleryPageSource.LoadPageAsync"/>).
    /// </summary>
    internal PagedSlideshowSequence BuildViewSlideshowSequence()
    {
        var query = BuildQuery();
        var total = TotalCount;
        return new PagedSlideshowSequence(
            total,
            SlideshowPageSize,
            async (pageIndex, pageSize, ct) =>
            {
                var rows = await _source.LoadPageAsync(query, pageIndex, pageSize, total, ct).ConfigureAwait(false);
                return rows.Select(r => r.Id).ToList();
            });
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Places.Map.Tiles;
using Lumora.Places.Styles;
using Lumora.Shell;

namespace Lumora.Places.ViewModels;

/// <summary>Eintrag des Stil-Menüs auf der Karte.</summary>
/// <param name="NeedsKey">Der Stil braucht einen Schlüssel, der fehlt (Hinweis im Menü, Karte fällt auf OSM zurück).</param>
public sealed record MapStyleMenuEntry(MapStyleOption Option, bool IsCurrent, bool NeedsKey)
{
    public string Header => Option.Label + (NeedsKey ? " — Schlüssel nötig" : string.Empty);
}

/// <summary>
/// Orte-Seite (ADR-0055): gemeinsamer Galerie-Filter (Karte, Gruppen, „Im Ausschnitt“, „Ohne Ort“, Zähler und
/// Start-Ausschnitt zeigen nur Treffer) und umschaltbarer Kartenstil (Stil-Menü auf der Karte, Namensnennung je Stil,
/// Hinweis beim Rückfall auf OpenStreetMap).
/// </summary>
public sealed partial class PlacesViewModel
{
    private ISharedGalleryFilter? _filter;
    private IMapStyleService? _styles;
    private bool _fitIfNothingVisible;

    /// <summary>Wirkt der gemeinsame Galerie-Filter auf die Karte?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private bool _isFiltered;

    /// <summary>
    /// Filter-Teil der Galerie-Abfrage oder null ohne wirkenden Filter. Der „Kartenausschnitt“ (aus „In der Galerie
    /// zeigen“) stammt von der Karte selbst und gilt nur in Galerie und Ordner — sonst schrumpfte die Karte auf die eben
    /// gezeigte Gruppe.
    /// </summary>
    private GalleryQuery? CurrentFilter
    {
        get
        {
            if (_filter is not { IsActive: true, Current: { } current })
            {
                return null;
            }

            var withoutMapArea = current with { Filter = current.Filter with { Geo = null } };
            return SharedGalleryFilter.HasAnyFilter(withoutMapArea) ? withoutMapArea : null;
        }
    }

    private void InitFilterAndStyles(ISharedGalleryFilter? filter, IMapStyleService? styles)
    {
        _filter = filter;
        _styles = styles;
        if (filter is not null)
        {
            filter.Changed += (_, _) => UiThread.Post(OnSharedFilterChanged);
        }

        if (styles is not null)
        {
            styles.Changed += (_, _) => UiThread.Post(NotifyMapStyleChanged);
        }
    }

    /// <summary>Filter in Galerie, Ordner oder auf dieser Seite geändert: entprellt neu laden (unsichtbar: beim Anzeigen).</summary>
    private void OnSharedFilterChanged()
    {
        _dirty = true;
        _fitIfNothingVisible = true;
        if (!_isVisible)
        {
            return;
        }

        _reloadDelay?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDelay = cts;
        _ = ReloadAfterDelayAsync(cts.Token);
    }

    // ---- Kartenstil ------------------------------------------------------------------------------------------------

    /// <summary>Gewählter Stil (Einstellungen bzw. Stil-Menü).</summary>
    public MapStyle CurrentMapStyle => _styles?.Style ?? MapStyle.Automatic;

    /// <summary>Beschriftung des Stil-Knopfs auf der Karte.</summary>
    public string MapStyleLabel => MapStyleCatalog.Options.First(o => o.Style == CurrentMapStyle).Label;

    /// <summary>Hinweis beim Rückfall auf OSM („API-Schlüssel nötig – in den Einstellungen eintragen“).</summary>
    public string? StyleNotice => (Tiles?.Source as StyledMapTileSource)?.Resolution.Notice;

    public bool HasStyleNotice => StyleNotice is not null;

    /// <summary>Stil-Menü: alle Stile, der gewählte markiert, fehlende Schlüssel genannt.</summary>
    public IReadOnlyList<MapStyleMenuEntry> MapStyleEntries()
    {
        var keys = _styles?.Keys ?? MapApiKeys.None;
        return MapStyleCatalog.Options
            .Select(o => new MapStyleMenuEntry(o, o.Style == CurrentMapStyle, o.Style != MapStyle.Automatic && o.Vendor is { } vendor && keys.For(vendor) is null))
            .ToList();
    }

    /// <summary>Stil wählen (gespeichert wie in den Einstellungen; die Kachelquelle wechselt sofort).</summary>
    public async Task SetMapStyleAsync(MapStyle style)
    {
        if (_styles is null)
        {
            return;
        }

        try
        {
            await _styles.SetStyleAsync(style).ConfigureAwait(true);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Orte: Kartenstil nicht gespeichert");
            ShowMessage("Der Kartenstil konnte nicht gespeichert werden: " + ex.Message, isError: true);
        }

        NotifyMapStyleChanged();
    }

    /// <summary>Quelle des aktuellen Stils (für Namensnennung und Lizenzlink).</summary>
    private MapTileStyleDefinition? CurrentDefinition => Tiles?.Source switch
    {
        StyledMapTileSource styled => styled.Resolution.Definition,
        HttpMapTileSource http => http.Style,
        DisabledMapTileSource disabled => disabled.Style,
        _ => null,
    };

    private void OnMapSourceChanged()
    {
        NotifyMapStyleChanged();
        UpdateTileStatus();
    }

    private void NotifyMapStyleChanged()
    {
        OnPropertyChanged(nameof(Attribution));
        OnPropertyChanged(nameof(AttributionLink));
        OnPropertyChanged(nameof(StyleNotice));
        OnPropertyChanged(nameof(HasStyleNotice));
        OnPropertyChanged(nameof(CurrentMapStyle));
        OnPropertyChanged(nameof(MapStyleLabel));
    }
}

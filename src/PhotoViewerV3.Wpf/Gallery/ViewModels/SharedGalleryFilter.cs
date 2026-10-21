using System;
using PhotoViewerV3.Gallery.Data;

namespace PhotoViewerV3.Gallery.ViewModels;

/// <summary>Seite, deren Galerie-Ansicht seitengebundene Teile hat (ADR-0055): Ordnerauswahl, Sortierung, Ansicht.</summary>
public enum GalleryPageKind
{
    Gallery = 0,
    Folders = 1,
}

/// <summary>
/// EIN Filterzustand für Galerie, Ordner und Orte (ADR-0055, Herbert 2026-10-21): Der Filter (Suche, Datei- und
/// Inhalt-Chips) gehört allen drei Seiten und bleibt beim Seitenwechsel erhalten; „Filter zurücksetzen“ wirkt überall.
/// Das <see cref="GalleryViewModel"/> (Singleton je Katalog-Sitzung, hält die Steuerelemente der Filterkarte) veröffentlicht
/// hier bei jedem Neuladen den Filter-Teil seiner Abfrage — ohne Ordner und Sortierung, die seitengebunden sind. Andere
/// Seiten (Orte) lesen <see cref="Current"/> und bauen ihre SQL damit über
/// <see cref="GalleryPageSource.CreateFilteredCommand"/>.
/// </summary>
public interface ISharedGalleryFilter
{
    /// <summary>Filter-Teil der Galerie-Abfrage (ohne Ordner/Sortierung) oder null, solange die Galerie nichts geladen hat.</summary>
    GalleryQuery? Current { get; }

    /// <summary>Wirkt mindestens eine Bedingung (Suche, Datei-, Inhalt-, Album-Filter)?</summary>
    bool IsActive { get; }

    /// <summary>Filter geändert (UI-Thread). Gleiche Treffermenge löst nichts aus.</summary>
    event EventHandler? Changed;

    /// <summary>Vom Galerie-ViewModel bei jedem Neuladen: Abfrage veröffentlichen (Ordner und Sortierung werden entfernt).</summary>
    void Publish(GalleryQuery query);
}

/// <summary>Speicher des gemeinsamen Filters (Singleton je Katalog-Sitzung).</summary>
public sealed class SharedGalleryFilter : ISharedGalleryFilter
{
    private string? _key;

    public GalleryQuery? Current { get; private set; }

    public bool IsActive { get; private set; }

    public event EventHandler? Changed;

    public void Publish(GalleryQuery query)
    {
        var filter = ToFilterQuery(query);
        var key = GalleryPageSource.FilterKey(filter);
        if (string.Equals(key, _key, StringComparison.Ordinal))
        {
            return;
        }

        _key = key;
        Current = filter;
        IsActive = HasAnyFilter(filter);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Abfrage ohne die seitengebundenen Teile (Ordner, Sortierung).</summary>
    public static GalleryQuery ToFilterQuery(GalleryQuery query) => GalleryPageSource.FilterOnly(query);

    /// <summary>Wirkt ein Filter? (wie die Trefferzeile der Galerie: Suche, Datei-Teil, Tags, Personen, Kategorien, Motive, Album)</summary>
    public static bool HasAnyFilter(GalleryQuery query)
        => query.HasSearch || query.Filter.IsActive || query.HasTagFilter || query.HasPersonFilter
           || query.ActiveCategoryFilters.Count > 0 || query.HasMotifFilter || query.AlbumId is not null;
}

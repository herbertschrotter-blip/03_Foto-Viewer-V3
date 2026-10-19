using System.Collections.Generic;
using System.Linq;
using PhotoViewerV3.Gallery.Data;

namespace PhotoViewerV3.Gallery.ListView;

/// <summary>Darstellung der Galerie: Kachel-Grid oder Listenansicht (DataGrid über dieselbe Page).</summary>
public enum GalleryViewMode
{
    Grid = 0,
    List = 1,
}

/// <summary>Spalten der Listenansicht (Schlüssel für Einstellungen und Kopfzeilen-Kontextmenü).</summary>
public enum GalleryListColumnKey
{
    Name,
    Folder,
    Modified,
    Size,
    Type,
    Rating,
    Favorite,
    Tags,
    Status,
}

/// <summary>Stammdaten einer Spalte: Überschrift, Startbreite, serverseitige Sortierung (auf/ab) oder null = nur innerhalb der Page.</summary>
public sealed record GalleryListColumnDefinition(
    GalleryListColumnKey Key,
    string Header,
    double Width,
    GallerySort? SortAscending,
    GallerySort? SortDescending)
{
    /// <summary>True, wenn ein Klick auf die Kopfzeile die Sortierung der gesamten Trefferliste (DB) ändert.</summary>
    public bool SortsServerSide => SortAscending is not null;
}

/// <summary>Alle Spalten in Standardreihenfolge; „Status“ = fehlt / Eingang / Archiv (ADR-0020).</summary>
public static class GalleryListColumns
{
    public static IReadOnlyList<GalleryListColumnDefinition> All { get; } =
    [
        new(GalleryListColumnKey.Name, "Name", 280, GallerySort.NameAsc, GallerySort.NameDesc),
        new(GalleryListColumnKey.Folder, "Ordner", 260, null, null),
        new(GalleryListColumnKey.Modified, "Geändert", 130, GallerySort.ModifiedAsc, GallerySort.ModifiedDesc),
        new(GalleryListColumnKey.Size, "Größe", 90, GallerySort.SizeAsc, GallerySort.SizeDesc),
        new(GalleryListColumnKey.Type, "Typ", 70, null, null),
        new(GalleryListColumnKey.Rating, "Bewertung", 100, null, null),
        new(GalleryListColumnKey.Favorite, "Favorit", 70, null, null),
        new(GalleryListColumnKey.Tags, "Tags", 180, null, null),
        new(GalleryListColumnKey.Status, "Status", 80, null, null),
    ];

    public static GalleryListColumnDefinition Get(GalleryListColumnKey key) => All.First(c => c.Key == key);

    /// <summary>Spalte + Richtung zu einer Galerie-Sortierung (für den Sortierpfeil in der Kopfzeile); null bei Page-Sortierung.</summary>
    public static (GalleryListColumnKey Key, bool Descending)? ForSort(GallerySort sort)
    {
        foreach (var column in All)
        {
            if (column.SortAscending == sort)
            {
                return (column.Key, false);
            }

            if (column.SortDescending == sort)
            {
                return (column.Key, true);
            }
        }

        return null;
    }

    /// <summary>
    /// Nächste Sortierung nach Klick auf eine Spalte mit serverseitiger Sortierung: erst aufsteigend, bei erneutem
    /// Klick auf dieselbe Spalte die Richtung wechseln. Null für Spalten, die nur innerhalb der Page sortieren.
    /// </summary>
    public static GallerySort? NextSort(GalleryListColumnKey key, GallerySort current)
    {
        var column = Get(key);
        if (!column.SortsServerSide)
        {
            return null;
        }

        return current == column.SortAscending ? column.SortDescending : column.SortAscending;
    }
}

/// <summary>
/// Sichtbare Spalten der Listenansicht in Reihenfolge (Setting <c>ui.gallery.listColumns</c>, Schlüssel durch Komma).
/// Unbekannte Schlüssel werden beim Parsen übergangen, „Name“ bleibt immer sichtbar.
/// </summary>
public sealed class GalleryListColumnSettings
{
    private readonly List<GalleryListColumnKey> _visible;

    private GalleryListColumnSettings(List<GalleryListColumnKey> visible)
    {
        _visible = visible;
    }

    public static GalleryListColumnSettings Default
        => new(GalleryListColumns.All.Select(c => c.Key).ToList());

    /// <summary>Sichtbare Spalten in Anzeigereihenfolge.</summary>
    public IReadOnlyList<GalleryListColumnKey> Visible => _visible;

    public bool IsVisible(GalleryListColumnKey key) => _visible.Contains(key);

    public static GalleryListColumnSettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var keys = new List<GalleryListColumnKey>();
        foreach (var token in value.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            if (System.Enum.TryParse<GalleryListColumnKey>(token, ignoreCase: true, out var key) && !keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        if (!keys.Contains(GalleryListColumnKey.Name))
        {
            keys.Insert(0, GalleryListColumnKey.Name);
        }

        return new GalleryListColumnSettings(keys);
    }

    public string Serialize() => string.Join(",", _visible);

    /// <summary>Blendet eine Spalte ein (ans Ende) oder aus; „Name“ lässt sich nicht ausblenden.</summary>
    public GalleryListColumnSettings WithVisibility(GalleryListColumnKey key, bool visible)
    {
        var keys = new List<GalleryListColumnKey>(_visible);
        if (visible)
        {
            if (!keys.Contains(key))
            {
                keys.Add(key);
            }
        }
        else if (key != GalleryListColumnKey.Name)
        {
            keys.Remove(key);
        }

        return new GalleryListColumnSettings(keys);
    }

    /// <summary>Neue Reihenfolge aus den sichtbaren Spalten, wie das DataGrid sie nach dem Ziehen anzeigt.</summary>
    public GalleryListColumnSettings WithOrder(IEnumerable<GalleryListColumnKey> orderedVisible)
    {
        var keys = new List<GalleryListColumnKey>();
        foreach (var key in orderedVisible)
        {
            if (_visible.Contains(key) && !keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        foreach (var key in _visible)
        {
            if (!keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        return new GalleryListColumnSettings(keys);
    }

    public override string ToString() => Serialize();
}

using System.Collections.Generic;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumora.Stories;

/// <summary>Highlights oder alle Fotos (Umschalter der fixierten Leiste).</summary>
public enum StoryViewMode
{
    Highlights,
    All,
}

/// <summary>Klick auf ein Foto wählt das Titelbild bzw. markiert zum Entfernen.</summary>
public enum StoryPickMode
{
    None,
    Cover,
    Remove,
}

/// <summary>Tagesabschnitt der Story-Ansicht (vom Dienst bzw. bei einem einzigen Tag nach Tageszeit geteilt).</summary>
/// <param name="Index">1-basiert.</param>
/// <param name="Kicker">„Tag 1“ bzw. „Vormittag“.</param>
/// <param name="Chip">Text in der Tagesleiste: „Sa 12.“ bzw. „Vormittag · Prater“.</param>
public sealed record StoryDayModel(int Index, string Kicker, string Title, string? Places, string Chip, System.DateOnly Date, IReadOnlyList<int> FileIds, int PhotoCount, int VideoCount);

/// <summary>Chip der Tagesleiste (springt zum Abschnitt).</summary>
public sealed partial class StoryDayChip : ObservableObject
{
    public StoryDayChip(int index, string label)
    {
        Index = index;
        Label = label;
    }

    public int Index { get; }

    public string Label { get; }

    public StoryViewModel? Owner { get; set; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Ein Foto in der Story-Ansicht: Platz im Block bzw. in der dichten Reihe (DIP), Video mit Dauer, Serien-Hinweis
/// „+n ähnliche“, Grund der Auswahl („warum ausgewählt“), Favorit, blass, wenn kein Highlight (Modus „Alle Fotos“).
/// </summary>
public sealed partial class StoryTileViewModel : ObservableObject
{
    public StoryTileViewModel(int fileId, double x, double y, double width, double height, bool isLarge, StoryMediaInfo? media, string? reason, int similar, bool isHighlight, bool pale)
    {
        FileId = fileId;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsLarge = isLarge;
        Media = media;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        SimilarText = similar > 0 ? $"+{similar:N0} ähnliche" : null;
        IsHighlight = isHighlight;
        Opacity = pale ? 0.38 : 1.0;
    }

    public int FileId { get; }

    public StoryViewModel? Owner { get; set; }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public bool IsLarge { get; }

    public StoryMediaInfo? Media { get; }

    public bool IsVideo => Media?.IsVideo == true;

    public string? DurationText => Media?.DurationText;

    public bool IsFavorite => Media?.IsFavorite == true;

    public string? Reason { get; }

    public string? SimilarText { get; }

    public bool IsHighlight { get; }

    public double Opacity { get; }

    /// <summary>Automation-Name (Kachel ist ein Blatt): Dateiname, Video, Highlight.</summary>
    public string AutomationText => (Media?.Name ?? $"Datei {FileId}") + (IsVideo ? ", Video" : string.Empty) + (IsHighlight ? ", Highlight" : string.Empty);

    /// <summary>Tooltip: Grund der Auswahl bzw. „Nicht in den Highlights“.</summary>
    public string ToolTipText => Reason ?? (IsHighlight ? "Highlight" : "Nicht in den Highlights (z. B. unscharf oder Teil einer Serie)");

    [ObservableProperty] private ImageSource? _image;

    /// <summary>Zum Entfernen markiert („Fotos entfernen…“).</summary>
    [ObservableProperty] private bool _isMarked;
}

/// <summary>Eine Zeile der virtualisierten Story-Ansicht.</summary>
public abstract class StoryRow
{
    /// <summary>Story-Ansicht, deren Befehle die Vorlagen binden (statt FindAncestor).</summary>
    public StoryViewModel? Owner { get; set; }
}

/// <summary>Titelbild mit Überschrift (scrollt mit; die Leiste darunter bleibt oben stehen).</summary>
public sealed class StoryCoverRow(StoryViewModel story) : StoryRow
{
    public StoryViewModel Story { get; } = story;
}

/// <summary>Platzhalter unter dem Titelbild, über dem die fixierte Leiste liegt.</summary>
public sealed class StoryBarSpacerRow : StoryRow
{
    public const double Height = 56;
}

/// <summary>Kopf eines Tagesabschnitts: „TAG 1 · Samstag, 12. Juli · Grado · 58 Fotos · 2 Videos · 9 ausgewählt“.</summary>
public sealed class StoryDayHeaderRow(StoryDayModel day, string countsText) : StoryRow
{
    public StoryDayModel Day { get; } = day;

    public string Kicker => Day.Kicker;

    public string Title => Day.Title;

    public string? Places => Day.Places;

    public bool HasPlaces => !string.IsNullOrEmpty(Day.Places);

    public string CountsText { get; } = countsText;
}

/// <summary>Collage-Block bzw. eine Reihe der dichten Collage: Fotos mit festen Plätzen.</summary>
public sealed class StoryTilesRow(double height, double width, IReadOnlyList<StoryTileViewModel> tiles, bool dense) : StoryRow
{
    public double Height { get; } = height;

    public double Width { get; } = width;

    public IReadOnlyList<StoryTileViewModel> Tiles { get; } = tiles;

    /// <summary>Reihe der dichten Collage („Alle Fotos“), sonst Collage-Block der Highlights.</summary>
    public bool IsDense { get; } = dense;
}

/// <summary>„Alle 58 Fotos dieses Tages“ (aufklappen) bzw. „Weniger anzeigen“.</summary>
public sealed class StoryDayFooterRow(StoryDayModel day, string text, bool expanded, string? note = null) : StoryRow
{
    public StoryDayModel Day { get; } = day;

    public string Text { get; } = text;

    public bool IsExpanded { get; } = expanded;

    /// <summary>„Keine Highlights an diesem Tag“.</summary>
    public string? Note { get; } = note;

    public bool HasNote => Note is not null;
}

/// <summary>„5 weitere Tage · Di 15. – Sa 19. Juli · Aquileia, Grado“ mit „Anzeigen“.</summary>
public sealed class StoryCollapsedRow(string title, string text) : StoryRow
{
    public string Title { get; } = title;

    public string Text { get; } = text;
}

/// <summary>Ende-Kasten: „64 von 421 Fotos als Highlights · 37 ähnliche weggelassen“, Album, Diashow.</summary>
public sealed class StoryEndRow(StoryViewModel story, string text) : StoryRow
{
    public StoryViewModel Story { get; } = story;

    public string Text { get; } = text;
}

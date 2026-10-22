using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumora.Stories;

/// <summary>Art-Filter der Übersicht (Mockup 09: Alle, Ereignisse &amp; Reisen, Rückblicke, Personen, Orte).</summary>
public enum StoryKindFilter
{
    All,
    Events,
    Memories,
    Persons,
    Places,
}

/// <summary>Knopf des Art-Filters mit Zähler.</summary>
public sealed partial class StoryKindOption : ObservableObject
{
    public StoryKindOption(StoryKindFilter filter, string label)
    {
        Filter = filter;
        Label = label;
    }

    public StoryKindFilter Filter { get; }

    public string Label { get; }

    public StoriesViewModel? Owner { get; set; }

    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Jahres-Chip über „Reisen &amp; Ereignisse“ („2025 31“, „ältere 34“); <see cref="FromYear"/> null = alle Jahre.</summary>
public sealed partial class StoryYearChip : ObservableObject
{
    public StoryYearChip(string label, int count, int? fromYear, int? toYear)
    {
        Label = label;
        Count = count;
        FromYear = fromYear;
        ToYear = toYear;
    }

    public string Label { get; }

    public int Count { get; }

    public int? FromYear { get; }

    public int? ToYear { get; }

    public StoriesViewModel? Owner { get; set; }

    public bool Matches(int year) => (FromYear is null || year >= FromYear) && (ToYear is null || year <= ToYear);

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Eine Zeile der virtualisierten Übersicht (Banner, Abschnitt, Jahres-Chips, Kartenreihe, Hinweis).</summary>
public abstract class StoryOverviewRow
{
    /// <summary>Seite, deren Befehle die Vorlagen binden (statt FindAncestor — der meldet beim Abbau virtualisierter Zeilen Fehler).</summary>
    public StoriesViewModel? Owner { get; set; }
}

/// <summary>Rückblick-Banner „Heute vor N Jahren“.</summary>
public sealed class OverviewMemoryRow(StoryMemoryViewModel memory) : StoryOverviewRow
{
    public StoryMemoryViewModel Memory { get; } = memory;
}

/// <summary>Abschnitts-Kopf („Angeheftet 2“, „Personen · Alle 24“).</summary>
public sealed class OverviewSectionRow(string title, string? countText, string? moreText = null, StoryKindFilter? moreTarget = null) : StoryOverviewRow
{
    public string Title { get; } = title;

    public string? CountText { get; } = countText;

    /// <summary>„Alle anzeigen“ / „Alle 24“ — wechselt den Art-Filter; null = kein Link.</summary>
    public string? MoreText { get; } = moreText;

    public StoryKindFilter? MoreTarget { get; } = moreTarget;

    public bool HasMore => MoreText is not null;
}

/// <summary>Jahres-Chips über den Ereignissen.</summary>
public sealed class OverviewYearsRow(IReadOnlyList<StoryYearChip> chips) : StoryOverviewRow
{
    public IReadOnlyList<StoryYearChip> Chips { get; } = chips;
}

/// <summary>Eine Reihe Karten (so viele, wie in die Breite passen).</summary>
public sealed class OverviewCardRow(IReadOnlyList<StoryCardViewModel> cards, double cardWidth) : StoryOverviewRow
{
    public IReadOnlyList<StoryCardViewModel> Cards { get; } = cards;

    public double CardWidth { get; } = cardWidth;
}

/// <summary>Hinweis (keine Storys, keine Treffer).</summary>
public sealed class OverviewNoteRow(string title, string text) : StoryOverviewRow
{
    public string Title { get; } = title;

    public string Text { get; } = text;
}

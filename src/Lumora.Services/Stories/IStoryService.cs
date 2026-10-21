using Lumora.Services.Albums;
using Lumora.Services.Quality;

namespace Lumora.Services.Stories;

/// <summary>
/// Storys (ADR-0058) für die Story-Seite: Übersicht, Details, Highlights, Rückblicke und die Aktionen des Mockups 09.
/// Alles gilt für die Bibliotheks-Gruppe des aktuellen Bereichs (ADR-0048) und zeigt/zählt nur dessen Dateien; in
/// „Alle verknüpften“ die ganze Gruppe. Überschriften sind sachlich (<see cref="StoryTitleBuilder"/>), eigener Text nur
/// über <see cref="SetCustomTitleAsync"/>. Änderungen gehen als <c>StoriesChangedMessage</c> hinaus.
/// </summary>
public interface IStoryService
{
    /// <summary>Übersicht: Rückblicke von heute, Angeheftet, Neu, Ereignisse &amp; Reisen mit Jahren, Personen, Orte, eigene.</summary>
    Task<StoryOverview> GetOverviewAsync(CancellationToken ct = default);

    /// <summary>Ausgeblendete Storys („Ausgeblendet (3)“ → „Wieder zeigen“).</summary>
    Task<IReadOnlyList<StoryCard>> GetHiddenAsync(CancellationToken ct = default);

    /// <summary>Eine Story als Karte; null, wenn es sie nicht (mehr) gibt oder sie zu einer anderen Gruppe gehört.</summary>
    Task<StoryCard?> GetAsync(int storyId, CancellationToken ct = default);

    /// <summary>Story-Ansicht: Karte, Kopfzeile, Orte, Personen, Tagesabschnitte mit Datei-Ids in Aufnahme-Reihenfolge.</summary>
    Task<StoryDetails?> GetDetailsAsync(int storyId, CancellationToken ct = default);

    /// <summary>Highlights der Story (Anteil aus den Einstellungen) über <see cref="IHighlightSelector"/>, in Aufnahme-Reihenfolge.</summary>
    Task<IReadOnlyList<HighlightPick>> GetHighlightsAsync(int storyId, CancellationToken ct = default);

    /// <summary>Vorschaubilder je Story (Titelbild zuerst, dann über die Zeit verteilt) — für die Collage der Karten.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetPreviewFileIdsAsync(IReadOnlyCollection<int> storyIds, int perStory = 3, CancellationToken ct = default);

    /// <summary>Storys, in denen die Datei steht (Kontextmenü „In Storys“).</summary>
    Task<IReadOnlyList<StoryCard>> GetStoriesForFileAsync(int fileId, CancellationToken ct = default);

    /// <summary>Rückblicke eines Tages (Standard: heute) — dynamisch berechnet, nicht gespeichert.</summary>
    Task<IReadOnlyList<StoryMemory>> GetMemoriesAsync(DateOnly? day = null, CancellationToken ct = default);

    /// <summary>Speichert einen Rückblick als Story (öffnen, anheften, Titelbild …) und liefert ihre Id; vorhandene wird aufgefrischt.</summary>
    Task<int> OpenMemoryAsync(string key, CancellationToken ct = default);

    Task SetPinnedAsync(int storyId, bool pinned, CancellationToken ct = default);

    Task SetHiddenAsync(int storyId, bool hidden, CancellationToken ct = default);

    Task MarkSeenAsync(IReadOnlyCollection<int> storyIds, CancellationToken ct = default);

    /// <summary>Alle Storys des Bereichs als gesehen markieren.</summary>
    Task MarkAllSeenAsync(CancellationToken ct = default);

    /// <summary>Eigener Titel; null/leer = zurück zur sachlichen Überschrift.</summary>
    Task SetCustomTitleAsync(int storyId, string? title, CancellationToken ct = default);

    /// <summary>Titelbild setzen (Datei der Story) bzw. mit null zurück zur automatischen Wahl.</summary>
    Task SetCoverAsync(int storyId, int? fileId, CancellationToken ct = default);

    /// <summary>Dateien hinzufügen (bleiben über jede Neuberechnung); nur Dateien der Gruppe. Liefert die Zahl neu zählender.</summary>
    Task<int> AddFilesAsync(int storyId, IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>Dateien entfernen (bei automatischen Storys als Ausschluss, der eine Neuberechnung übersteht). Liefert die Zahl.</summary>
    Task<int> RemoveFilesAsync(int storyId, IReadOnlyList<int> fileIds, CancellationToken ct = default);

    /// <summary>„Story aus Auswahl“: neue eigene Story aus Dateien des Bereichs; liefert ihre Id.</summary>
    Task<int> CreateFromSelectionAsync(IReadOnlyList<int> fileIds, string? title = null, CancellationToken ct = default);

    /// <summary>Löscht eine eigene Story bzw. einen gespeicherten Rückblick (automatische lassen sich nur ausblenden).</summary>
    Task DeleteAsync(int storyId, CancellationToken ct = default);

    /// <summary>„Als Album speichern“: neues eigenes Album (Name = Überschrift, eindeutig gemacht) mit den Highlights oder allen Fotos.</summary>
    Task<AlbumInfo> SaveAsAlbumAsync(int storyId, string? albumName = null, bool highlightsOnly = false, CancellationToken ct = default);

    /// <summary>„Neu berechnen“: die Gruppe der Story (bzw. des Bereichs) jetzt neu erkennen.</summary>
    Task RecomputeAsync(int? storyId = null, CancellationToken ct = default);
}

using Lumora.Services.Stories;

namespace Lumora.Services.Messaging;

// Storys (ADR-0058). Gesendet vom Thread-Pool — UI-Empfänger müssen selbst dispatchen.

/// <summary>Was sich an Storys geändert hat.</summary>
public enum StoriesChangeKind
{
    /// <summary>Hintergrund-Erkennung: Storys neu, geändert oder entfernt.</summary>
    Detected = 0,

    /// <summary>Benutzer-Aktion an einer Story (anheften, ausblenden, gesehen, Titel, Titelbild, Dateien).</summary>
    Updated = 1,

    /// <summary>Neue Story (aus Auswahl, geöffneter Rückblick).</summary>
    Created = 2,

    /// <summary>Story gelöscht.</summary>
    Deleted = 3,
}

/// <summary>
/// Storys der Gruppe <paramref name="GroupId"/> haben sich geändert — die Story-Seite lädt nach.
/// <paramref name="StoryIds"/> leer = unbestimmt viele (ganze Gruppe neu lesen).
/// </summary>
public sealed record StoriesChangedMessage(int GroupId, StoriesChangeKind Kind, IReadOnlyList<int> StoryIds);

/// <summary>Fortschritt der Story-Erkennung (Statusleiste „Storys aktuell“ / „Storys werden berechnet …“).</summary>
public sealed record StoryIndexStatusMessage(StoryIndexStatus Status);

/// <summary>Einstellungen › Storys wurden gespeichert — die Erkennung rechnet neu.</summary>
public sealed record StorySettingsChangedMessage(StorySettings Settings);

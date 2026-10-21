using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Datei in einer Story (ADR-0058, Tabelle "StoryItems", Schlüssel StoryId + FileId). Beide Fremdschlüssel löschen mit
/// (ON DELETE CASCADE): Story gelöscht → Einträge weg; Files-Zeile gelöscht → die Datei verschwindet aus allen Storys
/// (manuelle Einträge sichern Datei-Operationen vorher in <see cref="FileLinkUndo"/>). <see cref="Day"/> ist der
/// Tagesabschnitt (Kalendertag in Ortszeit der Aufnahme als Tageszahl seit 1970-01-01).
/// </summary>
public sealed class StoryItem
{
    public int StoryId { get; set; }

    public int FileId { get; set; }

    public int Day { get; set; }

    public StoryItemOrigin Origin { get; set; }

    public DateTime AddedAtUtc { get; set; }
}

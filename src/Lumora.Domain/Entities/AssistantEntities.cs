namespace Lumora.Domain.Entities;

// Story-Assistent (ADR-0063, Migration AddStoryAssistant): Verlauf der Sätze und gemerkte Antworten auf Rückfragen.
// Beide gehören einer Bibliotheks-Gruppe (GroupId ohne Fremdschlüssel wie Tags/Alben/Storys, ADR-0048) und liegen im
// Katalog der Sitzung — in einer versteckten Bibliothek also nur im versteckten Katalog (ADR-0049).

/// <summary>
/// Ein Satz aus „Zuletzt gefragt“ (Tabelle "AssistantHistory"): der Satz des Benutzers, die von der Sprach-KI erkannte
/// Absicht als JSON (<see cref="IntentJson"/>, ohne Fotos, Pfade oder Koordinaten) und die gewählte Aktion. „Öffnen“
/// rechnet aus der gespeicherten Absicht neu, ohne die Sprach-KI erneut zu fragen. Löschbar („Verlauf löschen“).
/// </summary>
public sealed class AssistantHistoryEntry
{
    public int Id { get; set; }

    /// <summary>Bibliotheks-Gruppe (ADR-0048).</summary>
    public int GroupId { get; set; }

    public required string Sentence { get; set; }

    public required string IntentJson { get; set; }

    /// <summary>Aktion als Zahl (<c>IntentAction</c> in den Services).</summary>
    public int Action { get; set; }

    /// <summary>Trefferzahl beim letzten Ausführen.</summary>
    public int ResultCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastUsedAtUtc { get; set; }
}

/// <summary>
/// Gemerkte Antwort auf eine Rückfrage (Tabelle "AssistantAliases"): ein Begriff aus einem Satz („oma“, „nicole“, „ich“)
/// zeigt auf eine Person. Mehrere Zeilen je Begriff = „beide“. Person gelöscht → Zeile weg (ON DELETE CASCADE).
/// </summary>
public sealed class AssistantAlias
{
    public int Id { get; set; }

    public int GroupId { get; set; }

    /// <summary>Normalisierter Begriff (klein, ohne Akzente).</summary>
    public required string Term { get; set; }

    public int PersonId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

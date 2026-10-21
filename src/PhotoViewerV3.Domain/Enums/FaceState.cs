namespace PhotoViewerV3.Domain.Enums;

/// <summary>
/// Zustand eines erkannten Gesichts (Tabelle "Faces", Spalte State, ADR-0035). Numerische Werte sind Vertrag mit der DB.
/// Teil 1 (0.2.34) schreibt nur <see cref="Unconfirmed"/>; Vorschläge, Bestätigung und „kein Gesicht“ setzt die
/// Personen-Seite (Welle 2).
/// </summary>
public enum FaceState
{
    /// <summary>Erkannt, noch keiner Person zugeordnet.</summary>
    Unconfirmed = 0,

    /// <summary>Automatischer Vorschlag (<c>SuggestedPersonId</c>, <c>SuggestionScore</c>), noch nicht bestätigt.</summary>
    Suggested = 1,

    /// <summary>Vom Benutzer bestätigt (<c>PersonId</c> gesetzt).</summary>
    Confirmed = 2,

    /// <summary>Vom Benutzer ignoriert bzw. als „kein Gesicht“ markiert.</summary>
    Ignored = 3,
}

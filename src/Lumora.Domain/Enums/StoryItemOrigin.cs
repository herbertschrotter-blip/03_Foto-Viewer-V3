namespace Lumora.Domain.Enums;

/// <summary>
/// Herkunft einer Datei in einer Story (ADR-0058, Spalte <c>StoryItems.Origin</c>). Numerische Werte sind Vertrag mit der DB.
/// Die Erkennung schreibt und löscht nur <see cref="Auto"/>; manuelle Zu- und Abgänge bleiben über jede Neuberechnung.
/// </summary>
public enum StoryItemOrigin
{
    /// <summary>Vom Benutzer entfernt: die Zeile bleibt als Ausschluss stehen, zählt nicht und wird nie wieder automatisch aufgenommen.</summary>
    Excluded = -1,

    /// <summary>Von der Erkennung aufgenommen.</summary>
    Auto = 0,

    /// <summary>Vom Benutzer hinzugefügt (bleibt, auch wenn die Erkennung die Datei nicht zur Story zählt).</summary>
    Added = 1,
}

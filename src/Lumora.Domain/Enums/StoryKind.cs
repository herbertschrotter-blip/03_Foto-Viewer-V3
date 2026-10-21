namespace Lumora.Domain.Enums;

/// <summary>
/// Art einer Story (ADR-0058, Spalte <c>Stories.Kind</c>). Numerische Werte sind Vertrag mit der DB.
/// </summary>
public enum StoryKind
{
    /// <summary>Ereignis: Fotos eines Zeitraums ohne lange Pause und ohne großen Ortswechsel (auch „Ausflug“ = ein Tag weg von zu Hause).</summary>
    Event = 0,

    /// <summary>Reise: aufeinanderfolgende Tage bzw. Ereignisse weg von zu Hause.</summary>
    Trip = 1,

    /// <summary>Rückblick („Heute vor N Jahren“, „dieser Monat in früheren Jahren“) — nur gespeichert, sobald der Benutzer ihn öffnet oder anheftet.</summary>
    Memory = 2,

    /// <summary>Person: Jahr bzw. Jahresspanne mit vielen bestätigten Fotos einer Person.</summary>
    Person = 3,

    /// <summary>Ort mit Besuchen in mehreren Jahren.</summary>
    Place = 4,

    /// <summary>Eigene Story aus einer Auswahl („Story aus Auswahl“) — die Erkennung fasst sie nie an.</summary>
    Custom = 5,
}

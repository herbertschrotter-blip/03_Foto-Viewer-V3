namespace Lumora.Domain.Enums;

/// <summary>
/// Rolle eines Wurzelordners einer Bibliothek (ADR-0020): Eingangs-Ordner nehmen neues Material auf, das
/// „Ins Archiv…“ in den einen Archiv-Ordner der Bibliothek verschoben wird.
/// </summary>
public enum LibraryRootRole
{
    /// <summary>Eingang: Dateien darunter gelten als „nicht archiviert“.</summary>
    Inbox = 0,

    /// <summary>Archiv: genau ein Wurzelordner je Bibliothek; Ziel von „Ins Archiv…“.</summary>
    Archive = 1,
}

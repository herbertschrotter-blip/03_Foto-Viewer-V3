using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Services.Messaging;

// Transport: CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
// Empfänger registrieren sich mit messenger.Register<TMessage>(this, (r, m) => …).
// Nachrichten können von Worker-Threads kommen — UI-Empfänger müssen selbst dispatchen.

/// <summary>Die aktive Library hat gewechselt (neu angelegt, aktiviert oder nach Scan).</summary>
public sealed record ActiveLibraryChangedMessage(int LibraryId);

/// <summary>
/// Wurzeln einer Library wurden hinzugefügt, entfernt, umbenannt oder in der Rolle geändert (ADR-0020).
/// Galerie und Ordnerbaum laden neu; beim Entfernen einer Wurzel sind ihre Dateien aus dem Katalog verschwunden.
/// </summary>
public sealed record LibraryRootsChangedMessage(int LibraryId);

/// <summary>Fortschritt eines laufenden Scans (für Statusleiste, ADR-0013).</summary>
public sealed record ScanProgressMessage(int LibraryId, ScanProgress Progress);

/// <summary>Ein Scan ist abgeschlossen (Gallery lädt neu).</summary>
public sealed record ScanCompletedMessage(int LibraryId, ScanResult Result);

public enum FilesChangeKind
{
    /// <summary>Favorit/Bewertung geändert — Zeilen bleiben, nur Anzeige aktualisieren.</summary>
    Metadata = 0,

    /// <summary>Dateien verschoben oder umbenannt — Pfad/FolderId geändert.</summary>
    Moved = 1,

    /// <summary>Dateien aus dem Katalog entfernt (Papierkorb, aus Library heraus verschoben).</summary>
    Removed = 2,

    /// <summary>Neue Dateien im Katalog (Undo, Incremental-Scan).</summary>
    Added = 3,
}

/// <summary>Katalog-Änderung an konkreten Dateien.</summary>
public sealed record FilesChangedMessage(int LibraryId, FilesChangeKind Kind, IReadOnlyList<int> FileIds);

/// <summary>Ordner angelegt oder umbenannt (Folders-Zeilen geändert, ohne dass Dateien dazukamen oder fehlen) — Ordnerbaum lädt neu.</summary>
public sealed record FoldersChangedMessage(int LibraryId);

/// <summary>Benutzer hat im Ordnerbaum einen Ordner gewählt; FolderId null = ganze Library.</summary>
public sealed record FolderSelectedMessage(int LibraryId, int? FolderId, bool IncludeSubfolders);

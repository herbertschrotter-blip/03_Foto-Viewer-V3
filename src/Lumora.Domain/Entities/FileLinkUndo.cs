using Lumora.Domain.Enums;

namespace Lumora.Domain.Entities;

/// <summary>
/// Verknüpfungen einer Datei, deren Files-Zeile eine Datei-Operation entfernt hat (Papierkorb, aus der Bibliothek heraus
/// verschoben), ADR-0054 / ADR-0012, Tabelle "FileLinkUndo". „Rückgängig“ der Operation bzw. die Rückkehr der Datei an
/// ihren alten Pfad (Windows-Papierkorb „Wiederherstellen“ + Scan) stellt sie auf die neue FileId wieder her. Gehört zur
/// <see cref="FileOperation"/> (ON DELETE CASCADE).
/// </summary>
public sealed class FileLinkUndo
{
    public int Id { get; set; }

    public int FileOperationId { get; set; }

    public FileLinkKind Kind { get; set; }

    /// <summary>AlbumId bzw. FolderId.</summary>
    public int TargetId { get; set; }

    public DateTime AddedAtUtc { get; set; }

    /// <summary>Voller Pfad der entfernten Datei — die Rückkehr an genau diesen Ort stellt die Verknüpfung wieder her.</summary>
    public required string SourcePath { get; set; }

    /// <summary>Größe der entfernten Datei: eine andere Datei am selben Pfad (neuer Import) erbt nichts.</summary>
    public long FileSize { get; set; }

    /// <summary>Änderungszeit der entfernten Datei (Windows „Wiederherstellen“ behält sie bei).</summary>
    public DateTime FileModifiedAtUtc { get; set; }
}

using Lumora.Services.Scanning;

namespace Lumora.Services.Archive;

/// <summary>Ein Eintrag (Datei) in einem Archiv; <paramref name="Path"/> ist der relative Pfad im Archiv mit „\“.</summary>
public sealed record ArchiveEntryInfo(
    string Path,
    string Name,
    long Size,
    long CompressedSize,
    DateTime? ModifiedAtUtc,
    bool IsEncrypted,
    bool IsMedia,
    bool IsArchive);

/// <summary>Inhaltsverzeichnis eines Archivs (nur Dateien, keine Ordnereinträge).</summary>
public sealed record ArchiveListing(
    string ArchivePath,
    IReadOnlyList<ArchiveEntryInfo> Entries)
{
    public int FileCount => Entries.Count;
    public int MediaCount => Entries.Count(e => e.IsMedia);
    public int NestedArchiveCount => Entries.Count(e => e.IsArchive);
    public long TotalSize => Entries.Sum(e => e.Size);
    public bool HasEncryptedEntries => Entries.Any(e => e.IsEncrypted);
}

/// <summary>Verhalten, wenn die Zieldatei bereits existiert.</summary>
public enum ArchiveOverwriteMode
{
    /// <summary>Vorhandene Datei bleibt, der Eintrag wird übersprungen (Standard).</summary>
    Skip = 0,

    /// <summary>Vorhandene Datei wird ersetzt.</summary>
    Overwrite = 1,

    /// <summary>Neue Datei bekommt einen freien Namen („Name (2).jpg“).</summary>
    Rename = 2,
}

/// <summary>Optionen für <see cref="IArchiveService.ExtractAsync"/>.</summary>
public sealed record ArchiveExtractOptions
{
    public static ArchiveExtractOptions Default { get; } = new();

    /// <summary>Zielordner; null = Ordner des Archivs + Unterordner mit dem Archivnamen ohne Endung.</summary>
    public string? TargetFolder { get; init; }

    public ArchiveOverwriteMode OverwriteMode { get; init; } = ArchiveOverwriteMode.Skip;

    /// <summary>Nur Fotos/Videos (laut Formatregister) entpacken; verschachtelte Archive werden bei <see cref="ExtractNested"/> trotzdem geholt.</summary>
    public bool MediaOnly { get; init; } = true;

    /// <summary>Archive im Archiv in weiteren Runden entpacken (V2-Runden-System) und danach entfernen.</summary>
    public bool ExtractNested { get; init; } = true;

    /// <summary>Höchste Verschachtelungstiefe (Runden nach dem Hauptarchiv).</summary>
    public int MaxNestedDepth { get; init; } = 3;

    /// <summary>Nach fehlerfreiem Entpacken das Archiv über IFileOperationService in den Papierkorb (braucht <see cref="ArchiveFileId"/>).</summary>
    public bool RecycleArchiveAfterExtract { get; init; }

    /// <summary>Katalog-Id des Archivs (Files.Id) für den Papierkorb; null = Archiv ist kein Katalog-Eintrag.</summary>
    public int? ArchiveFileId { get; init; }

    /// <summary>Bibliothek, die nach dem Entpacken inkrementell gescannt wird; null = kein Scan.</summary>
    public int? LibraryId { get; init; }

    public bool ScanAfterExtract { get; init; } = true;
}

/// <summary>Fortschritt beim Entpacken: Runde 0 = Hauptarchiv, 1… = verschachtelte Archive.</summary>
public sealed record ArchiveExtractProgress(
    int Round,
    string ArchiveName,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentEntry);

/// <summary>Fehler an einem einzelnen Eintrag oder Schritt (der Lauf geht weiter).</summary>
public sealed record ArchiveExtractError(string Entry, string Message);

/// <summary>Ergebnis eines Entpack-Laufs inkl. der verschachtelten Runden.</summary>
public sealed record ArchiveExtractResult(
    string ArchivePath,
    string TargetFolder,
    int FilesExtracted,
    int FilesSkipped,
    int NestedArchivesExtracted,
    int Rounds,
    long BytesExtracted,
    IReadOnlyList<ArchiveExtractError> Errors,
    bool Canceled,
    bool ArchiveRecycled,
    ScanResult? Scan)
{
    public bool HasErrors => Errors.Count > 0;
}

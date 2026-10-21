namespace Lumora.Services.Archive;

/// <summary>
/// Archive im Katalog (ADR-0022): Inhaltsverzeichnis lesen und entpacken. ZIP, RAR, 7z, TAR sowie GZ/TGZ/BZ2/XZ
/// über SharpCompress (ZIP zusätzlich mit System.IO.Compression als Fallback). Verschachtelte Archive werden in
/// Runden entpackt (V2-Vorbild), Pfade gegen Zip-Slip geprüft, passwortgeschützte Archive mit
/// <see cref="ArchiveEncryptedException"/> abgelehnt. Nach dem Entpacken optional Incremental-Scan der Bibliothek
/// (IScanService) und Archiv in den Papierkorb (IFileOperationService, protokolliert).
/// </summary>
public interface IArchiveService
{
    /// <summary>True, wenn die Endung im Formatregister als Archiv geführt ist.</summary>
    bool IsArchive(string? path);

    /// <summary>Standard-Zielordner: Ordner des Archivs + Unterordner mit dem Archivnamen ohne Endung („fotos.tar.gz“ → „fotos“).</summary>
    string GetDefaultTargetFolder(string archivePath);

    /// <summary>Liest das Inhaltsverzeichnis (nur Dateien); Medien- und Archiv-Einträge sind markiert.</summary>
    Task<ArchiveListing> ListAsync(string archivePath, CancellationToken ct = default);

    /// <summary>
    /// Entpackt das Archiv. Fehler einzelner Einträge brechen nicht ab (Liste im Ergebnis); Abbruch liefert ein Ergebnis mit
    /// <c>Canceled = true</c>. Nicht lesbare oder passwortgeschützte Archive werfen <see cref="ArchiveFormatException"/>
    /// bzw. <see cref="ArchiveEncryptedException"/>.
    /// </summary>
    Task<ArchiveExtractResult> ExtractAsync(
        string archivePath,
        ArchiveExtractOptions options,
        IProgress<ArchiveExtractProgress>? progress = null,
        CancellationToken ct = default);
}

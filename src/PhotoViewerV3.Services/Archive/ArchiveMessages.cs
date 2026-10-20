namespace PhotoViewerV3.Services.Archive;

/// <summary>
/// Ein Modul (z. B. die Lightbox-Inhaltsansicht) möchte ein Archiv entpacken lassen; die Galerie öffnet dafür den
/// Entpacken-Dialog. Transport wie alle Nachrichten: WeakReferenceMessenger.Default (siehe Services/Messaging/Messages.cs).
/// </summary>
public sealed record ExtractArchiveRequestedMessage(int FileId, string Path, string Name);

/// <summary>
/// Ein Archiv wurde in <paramref name="TargetFolder"/> entpackt (manuell oder automatisch; gesendet vom
/// <see cref="ArchiveService"/> nach den Runden, vor Papierkorb und Scan). Das automatische Entpacken (ADR-0032) merkt sich
/// den Ordner und fasst Archive darin nicht an — verschachtelte Archive behandelt nur das Runden-System des Services.
/// </summary>
public sealed record ArchiveExtractedMessage(string ArchivePath, string TargetFolder);

/// <summary>
/// Fortschritt bzw. Ergebnis des automatischen Entpackens (ADR-0032) für die Statusleiste. Kommt vom Hintergrund-Thread
/// des <see cref="IArchiveAutoExtractService"/> — UI-Empfänger dispatchen selbst.
/// </summary>
public sealed record ArchiveAutoExtractStatusMessage(ArchiveAutoExtractStatus Status);

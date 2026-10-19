namespace PhotoViewerV3.Services.Archive;

/// <summary>
/// Ein Modul (z. B. die Lightbox-Inhaltsansicht) möchte ein Archiv entpacken lassen; die Galerie öffnet dafür den
/// Entpacken-Dialog. Transport wie alle Nachrichten: WeakReferenceMessenger.Default (siehe Services/Messaging/Messages.cs).
/// </summary>
public sealed record ExtractArchiveRequestedMessage(int FileId, string Path, string Name);

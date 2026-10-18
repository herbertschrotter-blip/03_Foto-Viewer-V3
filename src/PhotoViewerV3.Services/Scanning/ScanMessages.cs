namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Ein Scan wurde abgebrochen (Benutzer) oder ist fehlgeschlagen — Ergänzung zu
/// <c>ScanCompletedMessage</c>, damit Statusleiste und Seiten den Scan-Zustand sauber beenden können.
/// Transport wie alle Nachrichten: WeakReferenceMessenger.Default (siehe Services/Messaging/Messages.cs).
/// </summary>
public sealed record ScanAbortedMessage(int LibraryId, bool Canceled, string Reason);

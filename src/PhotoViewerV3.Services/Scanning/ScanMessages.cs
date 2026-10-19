namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Ein Scan wurde abgebrochen (Benutzer), ist fehlgeschlagen oder endete, weil der Bibliotheks-Root nicht
/// erreichbar war (<paramref name="LibraryOffline"/>, PLAN Kap. 12 — es wurde nichts als fehlend markiert).
/// Ergänzung zu <c>ScanCompletedMessage</c>, damit Statusleiste und Seiten den Scan-Zustand sauber beenden können.
/// Transport wie alle Nachrichten: WeakReferenceMessenger.Default (siehe Services/Messaging/Messages.cs).
/// </summary>
public sealed record ScanAbortedMessage(int LibraryId, bool Canceled, string Reason, bool LibraryOffline = false);

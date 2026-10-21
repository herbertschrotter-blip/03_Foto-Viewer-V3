namespace Lumora.Services.Messaging;

// Wartende Schreiber der Katalog-DB (0.2.29, ADR-0030). Gesendet von SqliteBusyRetry auf einem Pool-Thread — UI-Empfänger
// müssen selbst dispatchen.

/// <summary>
/// Die Zahl der Schreibvorgänge, die auf die Schreibsperre der Katalog-DB warten, hat sich geändert (z. B. eine
/// Bewertung, während „Neu aufbauen“ den Katalog schreibt). <paramref name="WaitingWriters"/> = 0: niemand wartet mehr.
/// Ein Schreibvorgang meldet sich erst, wenn sein erster Versuch an der Sperre gescheitert ist (nach busy_timeout).
/// </summary>
public sealed record CatalogWriteWaitMessage(int WaitingWriters);

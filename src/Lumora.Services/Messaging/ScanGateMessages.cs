using Lumora.Services.Scanning;

namespace Lumora.Services.Messaging;

// Wartezustände der Scan-Sperre (0.2.22, ADR-0025). Gesendet von IScanGate auf dem Thread des Wartenden bzw. dem
// Thread, der das Warten beendet — UI-Empfänger müssen selbst dispatchen. Kein Warten, keine Nachricht: wer die Sperre
// sofort bekommt, meldet nichts.

/// <summary>Ein Scan oder Datei-Batch wartet, weil die Bibliothek belegt ist.</summary>
public sealed record ScanGateWaitStartedMessage(ScanGateWait Wait);

/// <summary>
/// Das Warten ist vorbei: <paramref name="Acquired"/> = true, wenn der Wartende die Sperre bekommen hat; false nach einem
/// Abbruch (CancellationToken des Aufrufers oder <see cref="IScanGate.CancelWait"/>, dann <paramref name="CanceledByUser"/>).
/// </summary>
public sealed record ScanGateWaitEndedMessage(ScanGateWait Wait, bool Acquired, bool CanceledByUser = false);

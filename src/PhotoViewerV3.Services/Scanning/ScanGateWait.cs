namespace PhotoViewerV3.Services.Scanning;

/// <summary>Wofür eine Bibliothek über <see cref="IScanGate"/> belegt wird bzw. werden soll.</summary>
public enum ScanGateUse
{
    /// <summary>Scan (jeder Modus, jeder Aufrufer: Bibliotheksseite, Entpacken, Ordnerüberwachung).</summary>
    Scan = 0,

    /// <summary>Datei-Batch des <c>FileOperationService</c> (auch die Ruhephase beim Umzug der Katalog-DB).</summary>
    FileOperation = 1,
}

/// <summary>
/// Ein Aufrufer wartet auf die Sperre einer Bibliothek (0.2.22). <paramref name="Holder"/> ist die Belegung, die beim
/// Beginn des Wartens bestand; <paramref name="WaitId"/> ist je <see cref="IScanGate"/> eindeutig und dient
/// <see cref="IScanGate.CancelWait"/>. <paramref name="CallerId"/> (0.2.29) ist die Kennung, die der Aufrufer bei
/// <see cref="IScanGate.AcquireAsync(int, Guid?, CancellationToken)"/> mitgegeben hat (z. B. die Bibliotheksseite je
/// Scan-Lauf über <see cref="ScanOptions.GateCallerId"/>) — so erkennt er sein eigenes Warten; null = ohne Kennung.
/// </summary>
public sealed record ScanGateWait(long WaitId, int LibraryId, ScanGateUse Waiter, ScanGateUse Holder, Guid? CallerId = null);

/// <summary>
/// Das Warten auf die Sperre wurde über <see cref="IScanGate.CancelWait"/> abgebrochen (z. B. „Abbrechen“ in der
/// Statusleiste) — nicht über das CancellationToken des Aufrufers. Ist ein <see cref="OperationCanceledException"/>,
/// damit bestehende Abbruch-Pfade greifen; die Meldung taugt für Fehlertexte der Aufrufer, die nur
/// <see cref="Exception"/> fangen.
/// </summary>
public sealed class ScanGateWaitCanceledException(ScanGateWait wait)
    : OperationCanceledException(Describe(wait))
{
    public ScanGateWait Wait { get; } = wait;

    private static string Describe(ScanGateWait wait) => wait.Holder == ScanGateUse.Scan
        ? "Abgebrochen, während auf den laufenden Scan gewartet wurde."
        : "Abgebrochen, während auf die laufende Datei-Operation gewartet wurde.";
}

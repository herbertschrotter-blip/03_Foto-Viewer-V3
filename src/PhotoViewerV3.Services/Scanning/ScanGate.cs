using System.Collections.Concurrent;

namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Hält Scans derselben Bibliothek auseinander: <see cref="IScanService"/> belegt die Bibliothek für die Dauer eines
/// Scans (jeder Modus, jeder Aufrufer — Bibliotheksseite, Entpacken, Ordnerüberwachung). Ein zweiter Scan derselben
/// Bibliothek wartet, bis der erste fertig ist; Scans verschiedener Bibliotheken laufen unabhängig.
/// </summary>
public interface IScanGate
{
    /// <summary>
    /// Wird ausgelöst, sobald ein Scan die Bibliothek belegt hat und bevor er das Dateisystem liest (synchron auf dem
    /// Scan-Thread; Handler müssen kurz sein und dürfen nicht werfen). Alle Dateiänderungen, die vorher geschehen sind,
    /// sieht dieser Scan — die Ordnerüberwachung verwirft deshalb hier ihre bis dahin gesammelten Ereignisse.
    /// </summary>
    event Action<int>? Acquired;

    /// <summary>Wartet, bis kein anderer Scan der Bibliothek läuft, und belegt sie; Dispose gibt sie frei.</summary>
    Task<IDisposable> AcquireAsync(int libraryId, CancellationToken ct);

    /// <summary>true, solange ein Scan die Bibliothek belegt (Momentaufnahme).</summary>
    bool IsBusy(int libraryId);
}

/// <summary>Ein <see cref="SemaphoreSlim"/>(1, 1) je Bibliothek (wenige Bibliotheken → nie entfernt).</summary>
public sealed class ScanGate : IScanGate
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    public event Action<int>? Acquired;

    public async Task<IDisposable> AcquireAsync(int libraryId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(libraryId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Acquired?.Invoke(libraryId);
        }
        catch
        {
            // Beobachter-Fehler dürfen den Scan nicht verhindern (die Ordnerüberwachung protokolliert selbst).
        }

        return new Lease(gate);
    }

    public bool IsBusy(int libraryId) => _gates.TryGetValue(libraryId, out var gate) && gate.CurrentCount == 0;

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

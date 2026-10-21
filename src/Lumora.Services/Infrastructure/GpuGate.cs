namespace Lumora.Services.Infrastructure;

/// <summary>
/// Gemeinsame Sperre für Rechnen auf der Grafikkarte (ADR-0052): Gesichtserkennung (ADR-0035) und Motiv-Erkennung
/// laufen beide über ONNX Runtime/DirectML auf derselben Grafikkarte. Gleichzeitige Läufe verdrängen sich gegenseitig
/// den Grafikspeicher (buffalo_l ≈ 0,3 GB, SigLIP-Bildmodell ≈ 0,4 GB plus Arbeitsspeicher) und machen beide langsamer
/// — deshalb hält jeder Hintergrund-Dienst die Sperre genau für die Inferenz EINES Fotos. Die Dienste wechseln sich so
/// Foto um Foto ab (Warteschlange der <see cref="SemaphoreSlim"/>, ungefähr in Ankunftsreihenfolge). Auf der CPU
/// laufende Analysen brauchen die Sperre nicht.
/// </summary>
public interface IGpuGate
{
    /// <summary>Wartet, bis kein anderer Dienst die Grafikkarte nutzt; Dispose gibt sie frei.</summary>
    Task<IDisposable> AcquireAsync(CancellationToken ct);

    /// <summary>true, solange ein Dienst die Grafikkarte hält (Momentaufnahme, Tests).</summary>
    bool IsBusy { get; }
}

/// <inheritdoc />
public sealed class GpuGate : IGpuGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsBusy => _semaphore.CurrentCount == 0;

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

namespace PhotoViewerV3.Tests.Fixtures;

/// <summary>
/// <see cref="IProgress{T}"/>, das jede Meldung sofort auf dem meldenden Thread zustellt — anders als
/// <see cref="Progress{T}"/>, das ohne SynchronizationContext auf den Thread-Pool postet. Mit <see cref="Progress{T}"/>
/// ist in Tests weder garantiert, dass alle Meldungen nach dem <c>await</c> angekommen sind, noch in welcher
/// Reihenfolge; ein Abbruch aus der Meldung heraus kann zu spät kommen (unter Last war die Arbeit dann schon fertig).
/// </summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly object _gate = new();

    public void Report(T value)
    {
        lock (_gate)
        {
            report(value);
        }
    }
}

using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;

namespace Lumora.Tests.Watch;

/// <summary>
/// Scan-Fake für die Ordnerüberwachung: zählt Aufrufe, meldet den Start über <see cref="Started"/>, kann über
/// <see cref="Gate"/> angehalten werden (laufender Scan) und sendet wie der echte ScanService
/// ScanProgress-/ScanCompleted-/ScanAbortedMessages, damit der Dienst seine eigenen Nachrichten ignorieren muss.
/// </summary>
public sealed class FakeScanService : IScanService
{
    private readonly IMessenger _messenger;
    private int _calls;

    public FakeScanService(IMessenger messenger)
    {
        _messenger = messenger;
    }

    public int Calls => Volatile.Read(ref _calls);

    public ScanOutcome Outcome { get; set; } = ScanOutcome.Completed;

    public int MissingDetected { get; set; }

    public int Added { get; set; } = 1;

    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Wenn gesetzt, wartet der Scan auf dieses Signal (simuliert einen lange laufenden Scan).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Wird bei jedem Scan-Start freigegeben.</summary>
    public SemaphoreSlim Started { get; } = new(0);

    /// <summary>Wird freigegeben, wenn ein Scan sein Ergebnis zurückgibt (auch bei Abbruch/Fehler nicht).</summary>
    public SemaphoreSlim Finished { get; } = new(0);

    /// <summary>true: <see cref="Gate"/> wartet ohne CancellationToken — der Scan liefert sein Ergebnis trotz Abbruch (z. B. Abbruch kam zu spät).</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>Wenn gesetzt, belegt der Scan wie der echte ScanService die Bibliothek (der spätere wartet).</summary>
    public IScanGate? ScanGate { get; set; }

    public List<ScanMode> Modes { get; } = new();

    public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

    public async Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Modes)
        {
            Modes.Add(mode);
        }

        Started.Release();
        using var lease = ScanGate is { } scanGate ? await scanGate.AcquireAsync(libraryId, cancellationToken) : null;
        _messenger.Send(new ScanProgressMessage(libraryId, new ScanProgress(0, 0, 0, null)));

        var gate = Gate;
        if (gate is not null)
        {
            await (IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken));
        }
        else
        {
            await Task.Delay(Duration, cancellationToken);
        }

        var result = new ScanResult(libraryId, string.Empty, 0, 0, 0, 0, Duration, Duration, TimeSpan.Zero, TimeSpan.Zero)
        {
            Mode = mode,
            Outcome = Outcome,
            Added = Outcome == ScanOutcome.Completed ? Added : 0,
            MissingDetected = MissingDetected,
        };

        if (Outcome == ScanOutcome.LibraryOffline)
        {
            _messenger.Send(new ScanAbortedMessage(libraryId, Canceled: false, "offline", LibraryOffline: true));
        }
        else
        {
            _messenger.Send(new ScanCompletedMessage(libraryId, result));
        }

        Finished.Release();
        return result;
    }
}

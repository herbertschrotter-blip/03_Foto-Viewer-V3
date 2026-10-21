using CommunityToolkit.Mvvm.Messaging;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Watch;

/// <summary>
/// Befund 1 (0.2.14): nie zwei Scans derselben Bibliothek gleichzeitig — weder „Aktualisieren“ neben dem automatischen
/// Scan der Ordnerüberwachung noch umgekehrt. Der echte <see cref="ScanService"/> belegt die Bibliothek über
/// <see cref="IScanGate"/>; der spätere Scan wartet.
/// </summary>
public sealed class ScanGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Hält den Scan-Thread beim ersten Fortschritt an (der Reporter ruft IProgress synchron auf).</summary>
    private sealed class BlockingProgress : IProgress<ScanProgress>
    {
        private int _reports;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Report(ScanProgress value)
        {
            if (Interlocked.Increment(ref _reports) == 1)
            {
                Entered.Set();
                Release.Wait(Timeout);
            }
        }
    }

    private sealed class CountingProgress : IProgress<ScanProgress>
    {
        private int _reports;

        public int Reports => Volatile.Read(ref _reports);

        public void Report(ScanProgress value) => Interlocked.Increment(ref _reports);
    }

    private static async Task<int> SeedLibraryAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "IMG_0001.jpg"));
        fx.WriteLibraryFile(Path.Combine("b", "IMG_0002.jpg"));
        (await fx.ScanAsync(library.Id)).FilesInserted.Should().Be(2);
        return library.Id;
    }

    [Fact]
    public async Task Second_scan_of_the_same_library_waits_until_the_first_is_finished()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedLibraryAsync(fx);
        var scanner = fx.CreateScanService(); // ein Singleton wie in der App

        var blocking = new BlockingProgress();
        var first = scanner.ScanAsync(libraryId, ScanMode.Incremental, blocking, CancellationToken.None);
        blocking.Entered.Wait(Timeout).Should().BeTrue("der erste Scan meldet Fortschritt");

        var counting = new CountingProgress();
        var second = scanner.ScanAsync(libraryId, ScanMode.Incremental, counting, CancellationToken.None);
        var winner = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1.5)));

        winner.Should().NotBeSameAs(second, "der zweite Scan derselben Bibliothek darf nicht neben dem ersten laufen");
        counting.Reports.Should().Be(0, "der zweite Scan liest das Dateisystem erst, wenn der erste fertig ist");

        blocking.Release.Set();
        (await first).Outcome.Should().Be(ScanOutcome.Completed);
        (await second.WaitAsync(Timeout)).Outcome.Should().Be(ScanOutcome.Completed);
        counting.Reports.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Scan_canceled_while_waiting_reports_aborted_and_does_not_keep_the_library_busy()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedLibraryAsync(fx);
        var scanner = fx.CreateScanService();
        var aborted = new List<ScanAbortedMessage>();
        fx.Messenger.Register<List<ScanAbortedMessage>, ScanAbortedMessage>(aborted, static (r, m) =>
        {
            lock (r)
            {
                r.Add(m);
            }
        });

        var blocking = new BlockingProgress();
        var first = scanner.ScanAsync(libraryId, ScanMode.Incremental, blocking, CancellationToken.None);
        blocking.Entered.Wait(Timeout).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        var waiting = scanner.ScanAsync(libraryId, ScanMode.Incremental, null, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await FluentActions.Awaiting(() => waiting.WaitAsync(Timeout)).Should().ThrowAsync<OperationCanceledException>();
        lock (aborted)
        {
            aborted.Should().ContainSingle(m => m.LibraryId == libraryId && m.Canceled, "Statusleiste und Seiten beenden den Scan-Zustand");
        }

        blocking.Release.Set();
        (await first).Outcome.Should().Be(ScanOutcome.Completed);
        (await scanner.ScanAsync(libraryId, ScanMode.Incremental, null, CancellationToken.None).WaitAsync(Timeout))
            .Outcome.Should().Be(ScanOutcome.Completed, "der abgebrochene Warteplatz hält die Bibliothek nicht belegt");
    }

    [Fact]
    public async Task Gate_serializes_per_library_and_announces_each_acquisition()
    {
        var gate = new ScanGate();
        var acquired = new List<int>();
        gate.Acquired += id =>
        {
            lock (acquired)
            {
                acquired.Add(id);
            }
        };

        var first = await gate.AcquireAsync(1, CancellationToken.None);
        gate.IsBusy(1).Should().BeTrue();
        gate.IsBusy(2).Should().BeFalse();

        using (await gate.AcquireAsync(2, CancellationToken.None).WaitAsync(Timeout))
        {
            gate.IsBusy(2).Should().BeTrue("eine andere Bibliothek wartet nicht");
        }

        var second = gate.AcquireAsync(1, CancellationToken.None);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("dieselbe Bibliothek ist belegt");

        first.Dispose();
        first.Dispose(); // doppelt freigeben ist harmlos
        using (await second.WaitAsync(Timeout))
        {
            gate.IsBusy(1).Should().BeTrue();
        }

        gate.IsBusy(1).Should().BeFalse();
        lock (acquired)
        {
            acquired.Should().Equal(1, 2, 1);
        }
    }
}

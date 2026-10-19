using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Watch;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Watch;

/// <summary>
/// Härtung der Ordnerüberwachung (0.2.14): Befunde aus dem Code-Review — fremder Scan ohne Fortschrittsmeldung,
/// Zusammenlegen mit „Aktualisieren“, Bibliothekswechsel während des automatischen Scans, Ordner mit Punkt im Namen,
/// Reihenfolge der Speichervorgänge und Rate der Statusnachrichten.
/// </summary>
public sealed partial class LibraryWatchServiceTests
{
    private static ScanResult ManualResult(Harness h)
        => new(h.LibraryId, h.Fixture.LibraryRoot, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = ScanMode.Incremental };

    // ---- Befund 1: nie zwei Scans derselben Bibliothek ----------------------------------------------

    [Fact]
    public async Task Scan_holding_the_library_defers_the_automatic_scan_even_before_its_first_progress_message()
    {
        await using var h = await Harness.CreateAsync();

        // „Aktualisieren“ hat die Bibliothek belegt, aber noch keinen Fortschritt gemeldet (Wurzeln prüfen, Katalog laden).
        using (await h.Gate.AcquireAsync(h.LibraryId, CancellationToken.None))
        {
            h.WriteFile("m\\IMG_0001.jpg");
            await Task.Delay(NoEventWindow);
            h.Scans.Calls.Should().Be(0, "kein automatischer Scan neben einem laufenden Scan derselben Bibliothek");
            h.Service.Status.State.Should().Be(WatchState.Idle, "kein Countdown während eines fremden Scans");
        }

        h.Fixture.Messenger.Send(new ScanCompletedMessage(h.LibraryId, ManualResult(h)));

        await h.WaitForScanStartAsync("Ereignisse während des fremden Scans werden danach geprüft");
        await h.WaitForStateAsync(WatchState.Idle, "automatischer Scan fertig");
        h.Scans.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Changes_before_a_manual_scan_are_covered_by_it_and_not_scanned_again()
    {
        await using var h = await Harness.CreateAsync();
        await h.Service.SetQuietPeriodAsync(TimeSpan.FromSeconds(2));

        h.WriteFile("IMG_merge.jpg");
        await h.WaitForStateAsync(WatchState.ChangesPending, "Countdown nach dem Anlegen");
        await Task.Delay(500); // nachlaufende Changed-Ereignisse derselben Datei abwarten

        using (await h.Gate.AcquireAsync(h.LibraryId, CancellationToken.None))
        {
            h.Service.Status.State.Should().Be(WatchState.Idle, "der manuelle Scan sieht die Änderung — kein eigener Countdown mehr");
        }

        h.Fixture.Messenger.Send(new ScanCompletedMessage(h.LibraryId, ManualResult(h)));
        await Task.Delay(TimeSpan.FromSeconds(3.5));

        h.Scans.Calls.Should().Be(0, "die Änderung war vor dem manuellen Scan da — zusammengelegt statt zweimal gescannt");
        h.Service.Status.State.Should().Be(WatchState.Idle);
    }

    [Fact]
    public async Task Changes_covered_by_a_canceled_manual_scan_are_scanned_afterwards()
    {
        await using var h = await Harness.CreateAsync();
        await h.Service.SetQuietPeriodAsync(TimeSpan.FromSeconds(2));

        h.WriteFile("IMG_cancel.jpg");
        await h.WaitForStateAsync(WatchState.ChangesPending, "Countdown nach dem Anlegen");
        await Task.Delay(500);

        using (await h.Gate.AcquireAsync(h.LibraryId, CancellationToken.None))
        {
            h.Service.Status.State.Should().Be(WatchState.Idle);
        }

        h.Fixture.Messenger.Send(new ScanAbortedMessage(h.LibraryId, Canceled: true, "Scan abgebrochen."));

        await h.WaitForScanStartAsync("abgebrochener Scan: die abgedeckten Änderungen gelten wieder");
        await h.WaitForStateAsync(WatchState.Idle, "automatischer Scan fertig");
        h.Scans.Calls.Should().Be(1);
    }

    // ---- Befund 2: Bibliothekswechsel während des automatischen Scans --------------------------------

    [Theory]
    [InlineData(ScanOutcome.MassMissingBlocked)]
    [InlineData(ScanOutcome.Completed)]
    public async Task Result_of_an_automatic_scan_is_not_assigned_to_a_library_activated_meanwhile(ScanOutcome outcome)
    {
        await using var h = await Harness.CreateAsync(configureScans: s =>
        {
            s.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s.IgnoreCancellation = true; // Abbruch kommt zu spät — der Scan liefert sein Ergebnis trotzdem
            s.Outcome = outcome;
            s.MissingDetected = 12_000;
        });

        h.WriteFile("s\\IMG_0001.jpg");
        await h.WaitForScanStartAsync("automatischer Scan der ersten Bibliothek");
        h.Service.Status.State.Should().Be(WatchState.Scanning);

        var otherRoot = Path.Combine(h.Fixture.OutsideDir, "other-lib");
        Directory.CreateDirectory(otherRoot);
        var libraries = new LibraryService(h.Fixture.ContextFactory, h.Fixture.Paths, NullLogger<LibraryService>.Instance);
        var other = await libraries.CreateAsync("Andere", otherRoot);
        await libraries.SetActiveAsync(other.Id);
        h.Fixture.Messenger.Send(new ActiveLibraryChangedMessage(other.Id));
        await Harness.WaitUntilAsync(() => h.Service.Status.LibraryId == other.Id, "Wechsel auf die andere Bibliothek");

        var gate = h.Scans.Gate!;
        h.Scans.Gate = null;
        gate.SetResult();
        (await h.Scans.Finished.WaitAsync(EventTimeout)).Should().BeTrue("der Scan der ersten Bibliothek endet");
        await Task.Delay(500);

        h.Service.Status.Should().BeEquivalentTo(new WatchStatus(other.Id, WatchState.Idle));
        h.Statuses.Where(s => s.LibraryId == other.Id).Should().NotContain(
            s => s.State == WatchState.Blocked || s.LastAutoScan != null,
            "Sperre und Ergebnis gehören zur vorigen Bibliothek");

        TempLibraryFixture.WriteFile(Path.Combine(otherRoot, "IMG_0002.jpg"));
        await h.WaitForScanStartAsync("die neue Bibliothek wird weiter automatisch aktualisiert");
        await Harness.WaitUntilAsync(() => h.Service.Status is { State: WatchState.Idle or WatchState.Blocked } s && s.LibraryId == other.Id, "Scan der neuen Bibliothek fertig");
        h.Scans.Calls.Should().Be(2);
    }

    // ---- Befund 3: Ordner mit Punkt im Namen ----------------------------------------------------------

    [Fact]
    public async Task Folder_with_a_dot_in_its_name_moved_in_and_out_triggers_a_scan_each_time()
    {
        await using var h = await Harness.CreateAsync();
        var outside = Path.Combine(h.Fixture.OutsideDir, "2024.06 Urlaub");
        TempLibraryFixture.WriteFile(Path.Combine(outside, "IMG_0001.jpg"));
        var inside = Path.Combine(h.Fixture.LibraryRoot, "2024.06 Urlaub");

        Directory.Move(outside, inside); // liefert nur ein Ordner-Ereignis, keins je Datei
        await h.WaitForScanStartAsync("Ordner „2024.06 Urlaub“ hereinverschoben");
        await h.WaitForStateAsync(WatchState.Idle, "erster Scan fertig");

        Directory.Move(inside, outside); // aus der Wurzel hinaus = Deleted
        await h.WaitForScanStartAsync("Ordner „2024.06 Urlaub“ hinausverschoben");
        await h.WaitForStateAsync(WatchState.Idle, "zweiter Scan fertig");
        h.Scans.Calls.Should().Be(2);
    }

    // ---- Befund 4: Reihenfolge der Speichervorgänge ---------------------------------------------------

    /// <summary>Settings-Store, dessen erster Speichervorgang hängt, bis der Test ihn freigibt.</summary>
    private sealed class GatedSettingsStore : IWatchSettingsStore
    {
        private readonly List<WatchSettings> _saved = new();
        private int _calls;

        public TaskCompletionSource FirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<WatchSettings> Saved
        {
            get
            {
                lock (_saved)
                {
                    return _saved.ToList();
                }
            }
        }

        public Task<WatchSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(WatchSettings.Default);

        public async Task SaveAsync(WatchSettings settings, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await FirstSave.Task.WaitAsync(EventTimeout, ct);
            }

            lock (_saved)
            {
                _saved.Add(settings);
            }
        }
    }

    [Fact]
    public async Task Rapid_setting_changes_are_persisted_in_order_and_the_last_value_wins()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var store = new GatedSettingsStore();
        using var service = new LibraryWatchService(
            fx.CreateLibraryService(),
            new FakeScanService(fx.Messenger),
            store,
            fx.Formats,
            fx.Paths,
            NullLogger<LibraryWatchService>.Instance,
            fx.Messenger);

        // NumberBox mit UpdateSourceTrigger=PropertyChanged: jeder Pfeilklick/Tastendruck speichert ohne await.
        var first = service.SetQuietPeriodAsync(TimeSpan.FromSeconds(5));
        var second = service.SetQuietPeriodAsync(TimeSpan.FromSeconds(7));
        var third = service.SetEnabledAsync(false);
        await Task.Delay(200);
        store.FirstSave.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(EventTimeout);

        service.QuietPeriod.Should().Be(TimeSpan.FromSeconds(7));
        service.IsEnabled.Should().BeFalse();
        store.Saved.Should().NotBeEmpty();
        store.Saved[^1].Should().Be(new WatchSettings(false, TimeSpan.FromSeconds(7)), "der zuletzt gesetzte Stand steht zuletzt in der Settings-Tabelle");
    }

    // ---- Befund 5: Rate der Statusnachrichten ---------------------------------------------------------

    [Fact]
    public async Task Burst_of_file_events_sends_few_status_messages_and_one_scan()
    {
        await using var h = await Harness.CreateAsync();
        await h.Service.SetQuietPeriodAsync(TimeSpan.FromSeconds(3));
        var before = h.Statuses.Count;
        const int files = 300;

        var firstWrite = DateTime.UtcNow;
        for (var i = 0; i < files; i++)
        {
            h.WriteFile($"burst\\IMG_{i:D4}.jpg");
        }

        var lastWrite = DateTime.UtcNow;
        await h.WaitForScanStartAsync("nach dem Kopieren vieler Dateien");
        await h.WaitForStateAsync(WatchState.Idle, "Scan fertig");

        var countdowns = h.Statuses.Skip(before).Where(s => s.State == WatchState.ChangesPending).ToList();
        countdowns.Should().NotBeEmpty();
        // Ungedrosselt käme je Ereignis eine Nachricht (≥ eine je Datei); gedrosselt höchstens eine je 500 ms plus Zustandswechsel.
        var allowed = (int)Math.Ceiling((lastWrite - firstWrite) / LibraryWatchService.CountdownPublishInterval) + 6;
        countdowns.Count.Should().BeLessThanOrEqualTo(allowed, "reine Countdown-Verschiebungen werden gedrosselt (sonst je Ereignis eine Nachricht an den UI-Thread)")
            .And.BeLessThan(files / 4);
        countdowns[^1].ScanDueAtUtc.Should().BeOnOrAfter(lastWrite + TimeSpan.FromSeconds(2), "der letzte Countdown kommt nachlaufend und zeigt die tatsächliche Fälligkeit");
        h.Scans.Calls.Should().Be(1);
    }
}

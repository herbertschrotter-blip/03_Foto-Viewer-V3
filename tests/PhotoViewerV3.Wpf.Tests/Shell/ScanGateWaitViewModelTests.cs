using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Libraries;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.Shell;

/// <summary>
/// Wartezustände der Scan-Sperre in der Oberfläche (0.2.22, ADR-0025): Statusleiste mit „Wartet auf laufenden Scan…“
/// und „Abbrechen“ für Datei-Operationen, Bibliotheksseite mit „Wartet auf …“ statt „Ordner werden durchsucht…“.
/// Alles läuft auf dem Test-UI-Thread mit echtem <see cref="ScanGate"/> und eigenem Messenger — nicht parallel zu den
/// Fenster-Tests, die auf demselben Dispatcher die Reihenfolge von Loaded/ContentRendered prüfen.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScanGateWaitViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static Task OnUiAsync(Func<Task> scenario)
        => TestUiThread.Dispatcher.InvokeAsync(scenario).Task.Unwrap().WaitAsync(Timeout);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(10);
        }
    }

    // ---- ScanGateWaitIndicator ------------------------------------------------------------------

    [Fact]
    public Task Indicator_shows_a_waiting_file_operation_and_cancels_it() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        using var indicator = new ScanGateWaitIndicator(gate, w => w.Waiter == ScanGateUse.FileOperation, messenger);
        var ended = new List<ScanGateWaitEndedMessage>();
        indicator.Ended += ended.Add;
        indicator.IsWaiting.Should().BeFalse();
        indicator.CancelCommand.CanExecute(null).Should().BeFalse();

        using var scan = await gate.AcquireAsync(1, CancellationToken.None);
        var batch = gate.AcquireForFileOperationAsync(1, CancellationToken.None);
        await WaitUntilAsync(() => indicator.IsWaiting, "Hinweis erscheint");

        indicator.Text.Should().Be("Wartet auf laufenden Scan…");
        indicator.ToolTip.Should().Contain("Datei-Operation");
        indicator.CancelCommand.CanExecute(null).Should().BeTrue();

        indicator.CancelCommand.Execute(null);
        var act = () => batch;
        await act.Should().ThrowAsync<ScanGateWaitCanceledException>();
        await WaitUntilAsync(() => !indicator.IsWaiting, "Hinweis verschwindet");

        indicator.Text.Should().BeNull();
        indicator.CancelCommand.CanExecute(null).Should().BeFalse();
        ended.Should().ContainSingle().Which.CanceledByUser.Should().BeTrue();
        gate.IsBusy(1).Should().BeTrue("nur das Warten wurde abgebrochen, der Scan läuft weiter");
    });

    [Fact]
    public Task Indicator_ignores_waits_its_filter_rejects_and_clears_when_the_wait_succeeds() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        using var indicator = new ScanGateWaitIndicator(gate, w => w.Waiter == ScanGateUse.FileOperation, messenger);

        var batch = await gate.AcquireForFileOperationAsync(2, CancellationToken.None);
        var scan = gate.AcquireAsync(2, CancellationToken.None); // ein Scan wartet — nicht Sache der Statusleiste
        await WaitUntilAsync(() => gate.GetWaits().Count == 1, "Scan wartet");
        await Task.Delay(50);
        indicator.IsWaiting.Should().BeFalse();

        batch.Dispose();
        var scanLease = await scan;
        var secondBatch = gate.AcquireForFileOperationAsync(2, CancellationToken.None);
        await WaitUntilAsync(() => indicator.IsWaiting, "Datei-Operation wartet");
        scanLease.Dispose();
        (await secondBatch).Dispose();
        await WaitUntilAsync(() => !indicator.IsWaiting, "Hinweis verschwindet nach dem Start");
        indicator.Text.Should().BeNull();
    });

    [Fact]
    public Task Indicator_picks_up_a_wait_that_began_before_it_was_created() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        var batch = await gate.AcquireForFileOperationAsync(3, CancellationToken.None);
        var waiting = gate.AcquireForFileOperationAsync(3, CancellationToken.None);
        await WaitUntilAsync(() => gate.GetWaits().Count == 1, "Datei-Operation wartet");

        using var indicator = new ScanGateWaitIndicator(gate, _ => true, messenger);
        indicator.IsWaiting.Should().BeTrue();
        indicator.Text.Should().Be("Wartet auf laufende Datei-Operation…");

        batch.Dispose();
        (await waiting).Dispose();
        await WaitUntilAsync(() => !indicator.IsWaiting, "Hinweis verschwindet");
    });

    // ---- Statusleiste ---------------------------------------------------------------------------

    [Fact]
    public Task Status_bar_shows_the_wait_and_confirms_the_cancel() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        var statusBar = new StatusBarViewModel(
            NullService.Create<IThumbnailService>(),
            NullService.Create<ILibraryService>(),
            NullService.Create<IFileOperationService>(),
            gate,
            NullLogger<StatusBarViewModel>.Instance,
            messenger);
        try
        {
            using var scan = await gate.AcquireAsync(4, CancellationToken.None);
            var batch = gate.AcquireForFileOperationAsync(4, CancellationToken.None);
            await WaitUntilAsync(() => statusBar.FileOperationWait.IsWaiting, "Statusleiste zeigt das Warten");
            statusBar.FileOperationWait.Text.Should().Be("Wartet auf laufenden Scan…");

            statusBar.FileOperationWait.CancelCommand.Execute(null);
            var act = () => batch;
            await act.Should().ThrowAsync<OperationCanceledException>();
            await WaitUntilAsync(() => !statusBar.FileOperationWait.IsWaiting, "Hinweis verschwindet");

            statusBar.ScanResultText.Should().Be("Wartende Datei-Operation abgebrochen");
            statusBar.ScanResultIsError.Should().BeFalse();
        }
        finally
        {
            statusBar.Dispose();
        }
    });

    // ---- Bibliotheksseite -----------------------------------------------------------------------

    /// <summary>Scan über die echte Sperre: belegt die Bibliothek, meldet einen Fortschritt und endet ohne Änderungen.</summary>
    private sealed class GateScanService(IScanGate gate) : IScanService
    {
        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
            => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
            => Task.Run(async () =>
            {
                using var lease = await gate.AcquireAsync(libraryId, options.GateCallerId, cancellationToken).ConfigureAwait(false);
                progress?.Report(new ScanProgress(1, 1, 1024, @"C:\Fotos"));
                return new ScanResult(libraryId, @"C:\Fotos", 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = mode };
            });
    }

    private static LibraryViewModel CreateLibraryViewModel(IScanGate gate, IMessenger messenger) => new(
        NullService.Create<ILibraryService>(),
        new GateScanService(gate),
        NullService.Create<IDialogService>(),
        NullService.Create<INavigationService>(),
        NullLogger<LibraryViewModel>.Instance,
        messenger);

    private static LibraryListItem Library(int id) => new(
        new LibraryInfo(id, "Fotos", @"C:\Fotos", DateTime.UtcNow, DateTime.UtcNow, 1, 1, true, []),
        isActive: true);

    [Fact]
    public Task Library_scan_waiting_for_a_file_operation_says_so_until_it_starts() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        var vm = CreateLibraryViewModel(gate, messenger);

        var batch = await gate.AcquireForFileOperationAsync(10, CancellationToken.None);
        var run = vm.StartScanAsync(Library(10), ScanMode.Incremental);
        await WaitUntilAsync(() => vm.IsWaitingForGate, "Scan wartet");

        vm.IsScanning.Should().BeTrue();
        vm.ScanPhaseText.Should().Be("Wartet auf laufende Datei-Operation…");

        batch.Dispose();
        await run;

        vm.IsWaitingForGate.Should().BeFalse();
        vm.IsScanning.Should().BeFalse();
        vm.ScanSummaryTitle.Should().Be("Aktualisierung abgeschlossen");
    });

    [Fact]
    public Task Library_scan_canceled_while_waiting_reports_an_untouched_catalog() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        var vm = CreateLibraryViewModel(gate, messenger);

        using var otherScan = await gate.AcquireAsync(11, CancellationToken.None);
        var run = vm.StartScanAsync(Library(11), ScanMode.Fresh);
        await WaitUntilAsync(() => vm.IsWaitingForGate, "Scan wartet");
        vm.ScanPhaseText.Should().Be("Wartet auf laufenden Scan…");

        vm.CancelScanCommand.Execute(null);
        await run;

        vm.IsWaitingForGate.Should().BeFalse();
        vm.IsScanning.Should().BeFalse();
        vm.ScanSummaryTitle.Should().Be("Scan abgebrochen");
        vm.ScanSummary.Should().Be("Der Scan wurde abgebrochen, bevor er begann; der Katalog ist unverändert.");
    });

    [Fact]
    public Task Library_scan_without_contention_never_shows_a_wait() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        var vm = CreateLibraryViewModel(gate, messenger);
        var sawWait = false;
        vm.PropertyChanged += (_, e) => sawWait |= e.PropertyName == nameof(LibraryViewModel.IsWaitingForGate) && vm.IsWaitingForGate;

        // Fremde Wartenachrichten (andere Bibliothek, Datei-Operation) zählen nicht.
        var run = vm.StartScanAsync(Library(12), ScanMode.Incremental);
        messenger.Send(new ScanGateWaitStartedMessage(new ScanGateWait(900, 99, ScanGateUse.Scan, ScanGateUse.Scan)));
        messenger.Send(new ScanGateWaitStartedMessage(new ScanGateWait(901, 12, ScanGateUse.FileOperation, ScanGateUse.Scan)));
        await run;

        sawWait.Should().BeFalse();
        vm.ScanSummaryTitle.Should().Be("Aktualisierung abgeschlossen");
    });
}

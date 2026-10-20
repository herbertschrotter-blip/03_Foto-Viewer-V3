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
/// 0.2.29: die Bibliotheksseite zeigt nur das Warten ihres eigenen Scans (Aufrufer-Kennung statt Bibliothek + Zeitpunkt,
/// offener Punkt aus 0.2.22), und die Statusleiste zeigt wartende Schreibvorgänge der Katalog-DB (ADR-0030).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ScanGateCallerIdViewModelTests
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

    /// <summary>
    /// Scan über die echte Sperre wie der ScanService (Kennung aus den Optionen); belegt die Bibliothek erst nach
    /// <see cref="Release"/> — so kann ein fremder Scan vorher zu warten beginnen, während die Seite schon scannt.
    /// </summary>
    private sealed class DelayedGateScanService(IScanGate gate) : IScanService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
            => ScanAsync(libraryId, mode, ScanOptions.Default, progress, cancellationToken);

        public Task<ScanResult> ScanAsync(int libraryId, ScanMode mode, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
            => Task.Run(async () =>
            {
                await _release.Task.ConfigureAwait(false);
                using var lease = await gate.AcquireAsync(libraryId, options.GateCallerId, cancellationToken).ConfigureAwait(false);
                progress?.Report(new ScanProgress(1, 1, 1024, @"C:\Fotos"));
                return new ScanResult(libraryId, @"C:\Fotos", 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = mode };
            });
    }

    private static LibraryListItem Library(int id) => new(
        new LibraryInfo(id, "Fotos", @"C:\Fotos", DateTime.UtcNow, DateTime.UtcNow, 1, 1, true, []),
        isActive: true);

    [Fact]
    public Task Library_page_ignores_a_watch_scan_waiting_for_the_same_library_and_shows_only_its_own_wait() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var gate = new ScanGate(messenger);
        var scans = new DelayedGateScanService(gate);
        var vm = new LibraryViewModel(
            NullService.Create<ILibraryService>(),
            scans,
            NullService.Create<IDialogService>(),
            NullService.Create<INavigationService>(),
            NullLogger<LibraryViewModel>.Instance,
            messenger);

        var batch = await gate.AcquireForFileOperationAsync(20, CancellationToken.None);
        var run = vm.StartScanAsync(Library(20), ScanMode.Incremental);
        vm.IsScanning.Should().BeTrue();

        // Die Ordnerüberwachung beginnt zuerst zu warten — vor 0.2.29 hielt die Seite das für ihr eigenes Warten.
        var watchScan = gate.AcquireAsync(20, Guid.NewGuid(), CancellationToken.None);
        await WaitUntilAsync(() => gate.GetWaits().Count == 1, "Ordnerüberwachung wartet");
        await Task.Delay(100); // Wartenachricht über UiThread.Post zustellen lassen
        vm.IsWaitingForGate.Should().BeFalse("das Warten der Ordnerüberwachung ist nicht das der Seite");
        vm.ScanPhaseText.Should().Be(Formatting.ScanPhase(ScanPhase.Enumerating));

        scans.Release();
        await WaitUntilAsync(() => vm.IsWaitingForGate, "eigener Scan wartet");
        vm.ScanPhaseText.Should().Be("Wartet auf laufende Datei-Operation…");

        // Der Batch endet, die Ordnerüberwachung (zuerst gekommen) bekommt die Sperre — die Seite wartet weiter.
        batch.Dispose();
        var watchLease = await watchScan.WaitAsync(Timeout);
        await Task.Delay(100);
        vm.IsWaitingForGate.Should().BeTrue("der eigene Scan wartet noch auf den Scan der Ordnerüberwachung");
        vm.IsScanning.Should().BeTrue();

        watchLease.Dispose();
        await run;
        vm.IsWaitingForGate.Should().BeFalse();
        vm.IsScanning.Should().BeFalse();
        vm.ScanSummaryTitle.Should().Be("Aktualisierung abgeschlossen");
    });

    [Fact]
    public Task Status_bar_shows_writes_waiting_for_the_catalog() => OnUiAsync(async () =>
    {
        var messenger = new WeakReferenceMessenger();
        var statusBar = new StatusBarViewModel(
            NullService.Create<IThumbnailService>(),
            NullService.Create<ILibraryService>(),
            NullService.Create<IFileOperationService>(),
            new ScanGate(messenger),
            NullLogger<StatusBarViewModel>.Instance,
            messenger);
        try
        {
            statusBar.CatalogWriteWaitText.Should().BeNull();

            await Task.Run(() => messenger.Send(new CatalogWriteWaitMessage(1))); // kommt von einem Pool-Thread
            await WaitUntilAsync(() => statusBar.CatalogWriteWaitText is not null, "Hinweis erscheint");
            statusBar.CatalogWriteWaitText.Should().Be("Änderung wartet auf den Katalog…");

            messenger.Send(new CatalogWriteWaitMessage(2));
            await WaitUntilAsync(() => statusBar.CatalogWriteWaitText != "Änderung wartet auf den Katalog…", "Zahl wird nachgeführt");
            statusBar.CatalogWriteWaitText.Should().Be("2 Änderungen warten auf den Katalog…");

            messenger.Send(new CatalogWriteWaitMessage(0));
            await WaitUntilAsync(() => statusBar.CatalogWriteWaitText is null, "Hinweis verschwindet");
        }
        finally
        {
            statusBar.Dispose();
        }
    });
}

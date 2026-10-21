using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Services.Archive;
using Lumora.Services.Scanning;
using Lumora.Services.Sorter;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Sorter;
using static Lumora.Tests.Archive.ArchiveTestHelper;

namespace Lumora.Tests.Operations;

/// <summary>
/// Scan-Sperre für Datei-Operationen (0.2.17): ein Batch des <see cref="Services.Operations.FileOperationService"/>
/// belegt dieselbe <see cref="IScanGate"/>-Sperre wie der Scan — nie liest ein Scan die Bibliothek, während ein Batch
/// Dateien verschiebt, und umgekehrt. Aufrufer, die nach dem Batch selbst scannen (Entpacken, Sortierer, „Ins Archiv“
/// mit anschließendem Aktualisieren), laufen ohne Deadlock durch.
/// </summary>
public sealed class FileOperationScanGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StillWaiting = TimeSpan.FromMilliseconds(800);

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

    private static async Task<(int LibraryId, List<int> FileIds)> SeedAsync(TempLibraryFixture fx, params string[] relativePaths)
    {
        var library = await fx.CreateLibraryAsync();
        foreach (var path in relativePaths)
        {
            fx.WriteLibraryFile(path);
        }

        (await fx.ScanAsync(library.Id)).FilesInserted.Should().Be(relativePaths.Length);
        var ids = new List<int>();
        foreach (var path in relativePaths)
        {
            ids.Add((await fx.FindFileAsync(Path.GetFileName(path)))!.Id);
        }

        return (library.Id, ids);
    }

    /// <summary>Ein Incremental-Scan danach findet nichts: Dateisystem und Katalog stimmen überein.</summary>
    private static async Task ExpectConsistentAsync(ScanService scanner, int libraryId)
    {
        var check = await scanner.ScanAsync(libraryId, ScanMode.Incremental, null, CancellationToken.None).WaitAsync(Timeout);
        check.Outcome.Should().Be(ScanOutcome.Completed);
        check.Added.Should().Be(0, "keine Datei darf als neu erscheinen");
        check.MarkedMissing.Should().Be(0, "keine Datei darf als fehlend erscheinen");
    }

    [Fact]
    public async Task Move_waits_for_a_running_scan_of_the_library_and_runs_afterwards()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, ids) = await SeedAsync(fx, Path.Combine("a", "IMG_0001.jpg"), Path.Combine("a", "IMG_0002.jpg"));
        var gate = new ScanGate();
        var scanner = fx.CreateScanService(gate);
        var fileOps = fx.CreateFileOperationService(scanGate: gate);
        var target = Path.Combine(fx.LibraryRoot, "b");

        var blocking = new BlockingProgress();
        var scan = scanner.ScanAsync(libraryId, ScanMode.Incremental, blocking, CancellationToken.None);
        blocking.Entered.Wait(Timeout).Should().BeTrue("der Scan liest gerade das Dateisystem");

        var move = fileOps.MoveAsync(ids, target);
        (await Task.WhenAny(move, Task.Delay(StillWaiting))).Should().NotBeSameAs(move, "der Batch darf nicht neben dem Scan laufen");
        File.Exists(Path.Combine(fx.LibraryRoot, "a", "IMG_0001.jpg")).Should().BeTrue("noch nichts verschoben");
        Directory.Exists(target).Should().BeFalse();

        blocking.Release.Set();
        (await scan.WaitAsync(Timeout)).Outcome.Should().Be(ScanOutcome.Completed);
        var result = await move.WaitAsync(Timeout);

        result.Succeeded.Should().Be(2);
        gate.IsBusy(libraryId).Should().BeFalse("der Batch gibt die Bibliothek vor der Rückkehr frei");
        await ExpectConsistentAsync(scanner, libraryId);
    }

    [Fact]
    public async Task Scan_waits_until_the_batch_is_finished_and_sees_its_result()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, ids) = await SeedAsync(fx, "IMG_0001.jpg", "IMG_0002.jpg", "IMG_0003.jpg");
        var gate = new ScanGate();
        var scanner = fx.CreateScanService(gate);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recycleBin = new FakeRecycleBin
        {
            OnRecycle = path =>
            {
                if (path.EndsWith("IMG_0001.jpg", StringComparison.OrdinalIgnoreCase))
                {
                    entered.Set();
                    release.Wait(Timeout);
                }
            },
        };
        var fileOps = fx.CreateFileOperationService(recycleBin, gate);

        var recycle = Task.Run(() => fileOps.RecycleAsync(ids.Take(2).ToList()));
        entered.Wait(Timeout).Should().BeTrue("der Batch steht mitten im Dateisystem-Schritt");
        gate.IsBusy(libraryId).Should().BeTrue();

        var counting = new CountingProgress();
        var scan = scanner.ScanAsync(libraryId, ScanMode.Incremental, counting, CancellationToken.None);
        (await Task.WhenAny(scan, Task.Delay(StillWaiting))).Should().NotBeSameAs(scan, "der Scan darf nicht neben dem Batch laufen");
        counting.Reports.Should().Be(0, "der Scan liest das Dateisystem erst nach dem Batch");

        release.Set();
        (await recycle.WaitAsync(Timeout)).Succeeded.Should().Be(2);
        var result = await scan.WaitAsync(Timeout);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.MarkedMissing.Should().Be(0, "der Batch hat die Katalogzeilen schon entfernt — nichts „fehlt“");
        result.Added.Should().Be(0);
        await using var ctx = fx.CreateContext();
        (await ctx.Files.CountAsync(f => f.LibraryId == libraryId)).Should().Be(1);
    }

    [Fact]
    public async Task Batch_canceled_while_waiting_for_a_scan_changes_nothing_and_frees_the_library()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, ids) = await SeedAsync(fx, "IMG_0001.jpg");
        var gate = new ScanGate();
        var fileOps = fx.CreateFileOperationService(scanGate: gate);
        var scanLease = await gate.AcquireAsync(libraryId, CancellationToken.None); // ein laufender Scan

        using var cts = new CancellationTokenSource();
        var move = fileOps.MoveAsync(ids, Path.Combine(fx.LibraryRoot, "neu"), cts.Token);
        await Task.Delay(200);
        cts.Cancel();

        await FluentActions.Awaiting(() => move.WaitAsync(Timeout)).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(fx.LibraryRoot, "IMG_0001.jpg")).Should().BeTrue();
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.OperationBatches.CountAsync()).Should().Be(0, "kein Protokoll ohne Dateisystem-Schritt");
        }

        scanLease.Dispose();
        gate.IsBusy(libraryId).Should().BeFalse("der abgebrochene Warteplatz hält die Bibliothek nicht belegt");
        (await fileOps.MoveAsync(ids, Path.Combine(fx.LibraryRoot, "neu")).WaitAsync(Timeout)).Succeeded.Should().Be(1);
    }

    [Fact]
    public async Task Rename_folder_create_folder_and_undo_also_wait_for_a_running_scan()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, ids) = await SeedAsync(fx, Path.Combine("alt", "IMG_0001.jpg"));
        var gate = new ScanGate();
        var fileOps = fx.CreateFileOperationService(scanGate: gate);
        var folderId = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "alt")))!.Id;

        async Task<T> WaitsForScanAsync<T>(Func<Task<T>> operation, string what)
        {
            var scanLease = await gate.AcquireAsync(libraryId, CancellationToken.None);
            var task = operation();
            (await Task.WhenAny(task, Task.Delay(StillWaiting))).Should().NotBeSameAs(task, $"{what} darf nicht neben einem Scan laufen");
            scanLease.Dispose();
            return await task.WaitAsync(Timeout);
        }

        (await WaitsForScanAsync(() => fileOps.RenameFolderAsync(folderId, "neu"), "Ordner umbenennen")).Success.Should().BeTrue();
        Directory.Exists(Path.Combine(fx.LibraryRoot, "neu")).Should().BeTrue();

        (await WaitsForScanAsync(() => fileOps.CreateFolderAsync(libraryId, fx.LibraryRoot, "leer"), "Ordner anlegen")).Success.Should().BeTrue();

        var undo = await WaitsForScanAsync(() => fileOps.UndoLastAsync(), "Rückgängig");
        undo!.Succeeded.Should().BeGreaterThan(0);
        Directory.Exists(Path.Combine(fx.LibraryRoot, "alt")).Should().BeTrue();

        (await WaitsForScanAsync(() => fileOps.RenameAsync(ids[0], "IMG_umbenannt.jpg"), "Umbenennen")).Succeeded.Should().Be(1);
        gate.IsBusy(libraryId).Should().BeFalse();
    }

    [Fact]
    public async Task Move_from_inbox_into_archive_and_concurrent_scans_finish_without_deadlock()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        await fx.AddInboxRootAsync(library.Id);
        for (var i = 0; i < 20; i++)
        {
            fx.WriteInboxFile(Path.Combine("kamera", $"IMG_{i:D4}.jpg"));
        }

        await fx.ScanAsync(library.Id);
        await using var read = fx.CreateContext();
        var ids = await read.Files.Select(f => f.Id).ToListAsync();
        var gate = new ScanGate();
        var scanner = fx.CreateScanService(gate);
        var fileOps = fx.CreateFileOperationService(scanGate: gate);

        // „Ins Archiv…“ (ArchiveMoveService → MoveAsync) während „Aktualisieren“ und Ordnerüberwachung scannen
        var scans = Enumerable.Range(0, 3)
            .Select(_ => scanner.ScanAsync(library.Id, ScanMode.Incremental, null, CancellationToken.None))
            .ToList();
        var move = fileOps.MoveAsync(ids, Path.Combine(fx.LibraryRoot, "2024", "2024-07"));
        scans.Add(scanner.ScanAsync(library.Id, ScanMode.Incremental, null, CancellationToken.None));

        (await move.WaitAsync(Timeout)).Succeeded.Should().Be(20);
        foreach (var result in await Task.WhenAll(scans).WaitAsync(Timeout))
        {
            result.Outcome.Should().Be(ScanOutcome.Completed);
            result.MarkedMissing.Should().Be(0, "kein Scan sieht eine halb verschobene Bibliothek");
            result.Added.Should().Be(0);
        }

        await ExpectConsistentAsync(scanner, library.Id);
    }

    [Fact]
    public async Task Extract_with_recycle_and_scan_finishes_with_the_shared_gate()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.LibraryRoot, "Unsortiert", "neu.zip"), ("a.jpg", Jpeg(100)), ("sub/b.jpg", Jpeg(100)));
        var library = await fx.CreateLibraryAsync();
        await fx.ScanAsync(library.Id);
        var archiveRow = (await fx.FindFileAsync("neu.zip"))!;
        var gate = new ScanGate();
        var scanner = fx.CreateScanService(gate);
        var svc = CreateService(scanner, fx.CreateFileOperationService(scanGate: gate));

        var result = await svc.ExtractAsync(zip, new ArchiveExtractOptions
        {
            LibraryId = library.Id,
            ArchiveFileId = archiveRow.Id,
            RecycleArchiveAfterExtract = true,
        }).WaitAsync(Timeout);

        result.Errors.Should().BeEmpty();
        result.ArchiveRecycled.Should().BeTrue("der Papierkorb-Batch belegt die Bibliothek und gibt sie wieder frei");
        result.Scan.Should().NotBeNull("der anschließende Scan bekommt die Sperre");
        result.Scan!.Added.Should().Be(2);
        gate.IsBusy(library.Id).Should().BeFalse();
        await ExpectConsistentAsync(scanner, library.Id);
    }

    [Fact]
    public async Task Sorter_run_undo_and_scans_finish_with_the_shared_gate()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var gate = new ScanGate();
        using var s = new SorterServices(fx, gate);
        var library = await fx.CreateLibraryAsync();
        await fx.AddInboxRootAsync(library.Id);
        fx.WriteInboxFile(Path.Combine("Handy", "p006_0001.jpg"), 1000);
        fx.WriteInboxFile(Path.Combine("Handy", "p006_0002.jpg"), 2000);
        fx.WriteInboxFile(Path.Combine("Handy", "p020_0001.jpg"), 3000);
        await fx.ScanAsync(library.Id);
        var scanner = fx.CreateScanService(gate);
        var source = Path.Combine(fx.InboxDir, "Handy");

        var analysis = await s.Sorter.AnalyzeAsync(source);
        var plan = s.Sorter.BuildPlan(analysis, SorterTestHelper.Mappings(("p006", "p006"), ("p020", "p020")), new SorterPlanOptions
        {
            TargetRoot = Path.Combine(fx.LibraryRoot, "2026"),
            DeleteEmptySourceFolders = true,
        });

        var parallelScan = scanner.ScanAsync(library.Id, ScanMode.Incremental, null, CancellationToken.None);
        var run = await s.Sorter.ExecuteAsync(plan).WaitAsync(Timeout);
        await parallelScan.WaitAsync(Timeout);

        run.Moved.Should().Be(3);
        run.Failed.Should().Be(0);
        await ExpectConsistentAsync(scanner, library.Id);

        var undo = await s.Sorter.UndoLastRunAsync().WaitAsync(Timeout);
        undo!.Restored.Should().Be(3);
        await ExpectConsistentAsync(scanner, library.Id);
        gate.IsBusy(library.Id).Should().BeFalse();
    }
}

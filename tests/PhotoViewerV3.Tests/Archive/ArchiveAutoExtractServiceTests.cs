using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Watch;
using PhotoViewerV3.Tests.Fixtures;
using static PhotoViewerV3.Tests.Archive.ArchiveTestHelper;

namespace PhotoViewerV3.Tests.Archive;

/// <summary>
/// Automatisches Entpacken neu gefundener Archive (ADR-0032): Einstellung aus/an, nur Eingang, Papierkorb, Fehlerfälle,
/// gemeinsame Scan-Sperre ohne Deadlock, keine Schleife über verschachtelte Archive, eigene Änderungen für die Ordnerüberwachung.
/// Echte Dienste (ScanService, FileOperationService, ArchiveService) auf einer Temp-Bibliothek mit Eingangs-Wurzel.
/// </summary>
public sealed class ArchiveAutoExtractServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Zählt die Aufrufe und prüft, ob die Scan-Sperre beim Entpacken belegt ist (sie muss es sein).</summary>
    private sealed class RecordingArchiveService(IArchiveService inner, IScanGate gate, int libraryId) : IArchiveService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool GateBusyDuringEveryExtract { get; private set; } = true;

        public bool IsArchive(string? path) => inner.IsArchive(path);

        public string GetDefaultTargetFolder(string archivePath) => inner.GetDefaultTargetFolder(archivePath);

        public Task<ArchiveListing> ListAsync(string archivePath, CancellationToken ct = default) => inner.ListAsync(archivePath, ct);

        public Task<ArchiveExtractResult> ExtractAsync(string archivePath, ArchiveExtractOptions options, IProgress<ArchiveExtractProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            GateBusyDuringEveryExtract &= gate.IsBusy(libraryId);
            return inner.ExtractAsync(archivePath, options, progress, ct);
        }
    }

    private sealed class Env : IDisposable
    {
        private readonly List<ArchiveAutoExtractStatus> _statuses = new();

        private Env(TempLibraryFixture fx, int libraryId)
        {
            Fx = fx;
            LibraryId = libraryId;
            Scanner = fx.CreateScanService(Gate);
            FileOps = fx.CreateFileOperationService(RecycleBin, Gate, Own);
            Real = new ArchiveService(fx.Formats, NullLogger<ArchiveService>.Instance, Scanner, FileOps, fx.Messenger);
            Archives = new RecordingArchiveService(Real, Gate, libraryId);
            Store = new ArchiveAutoExtractSettingsStore(fx.ContextFactory);
            Service = new ArchiveAutoExtractService(fx.ContextFactory, Store, Archives, Scanner, FileOps, Gate, Own,
                NullLogger<ArchiveAutoExtractService>.Instance, fx.Messenger);
            fx.Messenger.Register<Env, ArchiveAutoExtractStatusMessage>(this, static (r, m) =>
            {
                lock (r._statuses)
                {
                    r._statuses.Add(m.Status);
                }
            });
        }

        public TempLibraryFixture Fx { get; }

        public int LibraryId { get; }

        public ScanGate Gate { get; } = new();

        public OwnChangeRegistry Own { get; } = new();

        public FakeRecycleBin RecycleBin { get; } = new();

        public ScanService Scanner { get; }

        public PhotoViewerV3.Services.Operations.FileOperationService FileOps { get; }

        public ArchiveService Real { get; }

        public RecordingArchiveService Archives { get; }

        public ArchiveAutoExtractSettingsStore Store { get; }

        public ArchiveAutoExtractService Service { get; }

        public IReadOnlyList<ArchiveAutoExtractStatus> Statuses
        {
            get
            {
                lock (_statuses)
                {
                    return _statuses.ToList();
                }
            }
        }

        public string Inbox => Fx.InboxDir;

        /// <summary>Bibliothek mit Archiv-Wurzel (library) und Eingangs-Wurzel (inbox); Dienst gestartet, Fresh-Scan gelaufen.</summary>
        public static async Task<Env> CreateAsync(bool enabled, bool recycle = false, Action<TempLibraryFixture>? beforeFreshScan = null)
        {
            var fx = await TempLibraryFixture.CreateAsync();
            var library = await fx.CreateLibraryAsync();
            await fx.AddInboxRootAsync(library.Id);
            fx.WriteLibraryFile(Path.Combine("2024", "alt.jpg"));
            fx.WriteInboxFile("vorhanden.jpg");
            beforeFreshScan?.Invoke(fx);
            var env = new Env(fx, library.Id);
            await env.Store.SaveAsync(library.Id, new ArchiveAutoExtractSettings(enabled, recycle));
            await env.Service.StartAsync();
            await env.Scanner.ScanAsync(library.Id, ScanMode.Fresh, null, CancellationToken.None);
            await env.Service.WhenIdleAsync().WaitAsync(Timeout);
            return env;
        }

        public async Task<ScanResult> IncrementalAndWaitAsync()
        {
            var result = await Scanner.ScanAsync(LibraryId, ScanMode.Incremental, null, CancellationToken.None);
            await Service.WhenIdleAsync().WaitAsync(Timeout);
            return result;
        }

        public async Task<int> RecycleBatchCountAsync()
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.OperationBatches.CountAsync(b => b.OperationType == OperationType.Recycle && b.Status == BatchStatus.Applied);
        }

        public void Dispose()
        {
            Service.Dispose();
            Fx.Messenger.UnregisterAll(this);
            Fx.Dispose();
        }
    }

    private static string NewZip(Env env, string name, params (string Key, byte[] Content)[] entries)
        => WriteZip(Path.Combine(env.Inbox, name), entries);

    [Fact]
    public async Task Einstellung_ist_standardmaessig_aus()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var store = new ArchiveAutoExtractSettingsStore(fx.ContextFactory);

        (await store.LoadAsync(1)).Should().Be(ArchiveAutoExtractSettings.Default);
        ArchiveAutoExtractSettings.Default.Enabled.Should().BeFalse();
        ArchiveAutoExtractSettings.Default.RecycleAfterExtract.Should().BeFalse();

        await store.SaveAsync(1, new ArchiveAutoExtractSettings(true, true));
        (await store.LoadAsync(1)).Should().Be(new ArchiveAutoExtractSettings(true, true));
        (await store.LoadAsync(2)).Should().Be(ArchiveAutoExtractSettings.Default, "je Bibliothek (ADR-0033)");
    }

    [Fact]
    public async Task Aus_entpackt_nichts()
    {
        using var env = await Env.CreateAsync(enabled: false);
        var zip = NewZip(env, "neu.zip", ("a.jpg", Jpeg(100)));

        var scan = await env.IncrementalAndWaitAsync();

        scan.Added.Should().Be(1, "das Archiv selbst kommt in den Katalog");
        env.Service.GetSettings(env.LibraryId).Enabled.Should().BeFalse();
        env.Archives.Calls.Should().Be(0);
        Directory.Exists(Path.Combine(env.Inbox, "neu")).Should().BeFalse();
        File.Exists(zip).Should().BeTrue();
        env.Service.LastSummary.Should().BeNull();
        env.Statuses.Should().BeEmpty();
    }

    [Fact]
    public async Task An_entpackt_neues_Archiv_neben_sich_und_scannt_danach()
    {
        using var env = await Env.CreateAsync(enabled: true, beforeFreshScan: fx =>
            WriteZip(Path.Combine(fx.InboxDir, "alt.zip"), ("alt-inhalt.jpg", Jpeg(100))));
        File.Exists(Path.Combine(env.Inbox, "alt.zip")).Should().BeTrue();
        Directory.Exists(Path.Combine(env.Inbox, "alt")).Should().BeFalse("ein Fresh-Scan löst kein Entpacken aus — nur neu gefundene Archive");

        var zip = NewZip(env, "urlaub.zip", ("a.jpg", Jpeg(100)), ("sub/b.png", Jpeg(100)), ("liesmich.txt", Text("x")));
        await env.IncrementalAndWaitAsync();

        var target = Path.Combine(env.Inbox, "urlaub");
        File.Exists(Path.Combine(target, "a.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "sub", "b.png")).Should().BeTrue();
        File.Exists(Path.Combine(target, "liesmich.txt")).Should().BeTrue("automatisch wird alles entpackt, nicht nur Medien");
        File.Exists(zip).Should().BeTrue("ohne Papierkorb-Einstellung bleibt das Archiv liegen");
        (await env.Fx.FindFileAsync("urlaub.zip")).Should().NotBeNull();

        (await env.Fx.FindFileAsync("a.jpg")).Should().NotBeNull("der Folge-Scan nimmt die entpackten Dateien auf");
        (await env.Fx.FindFileAsync("b.png")).Should().NotBeNull();
        env.RecycleBin.Recycled.Should().BeEmpty();
        Directory.Exists(Path.Combine(env.Inbox, "alt")).Should().BeFalse();

        var summary = env.Service.LastSummary!;
        summary.Should().NotBeNull();
        summary.Items.Should().ContainSingle().Which.State.Should().Be(ArchiveAutoExtractItemState.Extracted);
        summary.FilesExtracted.Should().Be(3);
        summary.Scan.Should().NotBeNull();
        summary.Scan!.Added.Should().Be(2, "a.jpg und b.png (liesmich.txt ist kein Katalog-Format)");
        summary.HasProblems.Should().BeFalse();
        env.Archives.Calls.Should().Be(1);
        env.Archives.GateBusyDuringEveryExtract.Should().BeTrue("entpackt wird nur mit belegter Sperre — nie neben einem Scan");

        env.Statuses.Select(s => s.State).Should().ContainInOrder(ArchiveAutoExtractState.Running, ArchiveAutoExtractState.Scanning, ArchiveAutoExtractState.Finished);
        env.Own.IsOwn(Path.Combine(target, "sub", "b.png")).Should().BeTrue("die Ordnerüberwachung zählt den entpackten Baum nicht als fremde Änderung");
        env.Own.IsOwn(Path.Combine(env.Inbox, "andere.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task An_mit_Papierkorb_verschiebt_das_Archiv_protokolliert()
    {
        using var env = await Env.CreateAsync(enabled: true, recycle: true);
        var zip = NewZip(env, "set.zip", ("a.jpg", Jpeg(100)));

        await env.IncrementalAndWaitAsync();

        File.Exists(Path.Combine(env.Inbox, "set", "a.jpg")).Should().BeTrue();
        File.Exists(zip).Should().BeFalse();
        env.RecycleBin.Recycled.Should().ContainSingle().Which.Should().Be(zip);
        (await env.Fx.FindFileAsync("set.zip")).Should().BeNull("der FileOperationService entfernt die Katalogzeile");
        (await env.RecycleBatchCountAsync()).Should().Be(1, "Papierkorb nur über IFileOperationService (OperationBatch, ADR-0012)");
        env.Service.LastSummary!.Items.Should().ContainSingle().Which.Recycled.Should().BeTrue();
        env.Service.LastSummary!.RecycledCount.Should().Be(1);
    }

    [Fact]
    public async Task Archive_unter_der_Archiv_Wurzel_bleiben_unberuehrt()
    {
        using var env = await Env.CreateAsync(enabled: true, recycle: true);
        var zip = WriteZip(Path.Combine(env.Fx.LibraryRoot, "2024", "archiviert.zip"), ("a.jpg", Jpeg(100)));

        var scan = await env.IncrementalAndWaitAsync();

        scan.Added.Should().Be(1);
        env.Archives.Calls.Should().Be(0);
        Directory.Exists(Path.Combine(env.Fx.LibraryRoot, "2024", "archiviert")).Should().BeFalse();
        File.Exists(zip).Should().BeTrue();
        env.RecycleBin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Verschluesselte_beschaedigte_und_vorhandene_Ziele_bleiben_liegen_und_werden_gemeldet()
    {
        using var env = await Env.CreateAsync(enabled: true, recycle: true);
        var encrypted = WriteEncryptedZip(Path.Combine(env.Inbox, "geheim.zip"));
        var broken = Path.Combine(env.Inbox, "kaputt.zip");
        await File.WriteAllBytesAsync(broken, Enumerable.Range(0, 512).Select(i => (byte)(i * 7)).ToArray());
        Directory.CreateDirectory(Path.Combine(env.Inbox, "schon-da"));
        var existing = NewZip(env, "schon-da.zip", ("a.jpg", Jpeg(100)));

        await env.IncrementalAndWaitAsync();

        File.Exists(encrypted).Should().BeTrue();
        File.Exists(broken).Should().BeTrue();
        File.Exists(existing).Should().BeTrue();
        Directory.Exists(Path.Combine(env.Inbox, "geheim")).Should().BeFalse();
        Directory.Exists(Path.Combine(env.Inbox, "kaputt")).Should().BeFalse("kein leerer Zielordner bleibt zurück");
        Directory.EnumerateFileSystemEntries(Path.Combine(env.Inbox, "schon-da")).Should().BeEmpty("ein vorhandener Zielordner wird nie befüllt");
        env.RecycleBin.Recycled.Should().BeEmpty();
        (await env.RecycleBatchCountAsync()).Should().Be(0);

        var summary = env.Service.LastSummary!;
        summary.Items.Should().HaveCount(3);
        summary.Items.Single(i => i.Name == "geheim.zip").State.Should().Be(ArchiveAutoExtractItemState.Failed);
        summary.Items.Single(i => i.Name == "geheim.zip").Message.Should().Contain("passwortgeschützt");
        summary.Items.Single(i => i.Name == "kaputt.zip").State.Should().Be(ArchiveAutoExtractItemState.Failed);
        summary.Items.Single(i => i.Name == "schon-da.zip").State.Should().Be(ArchiveAutoExtractItemState.Skipped);
        summary.Items.Single(i => i.Name == "schon-da.zip").Message.Should().Contain("existiert bereits");
        summary.HasProblems.Should().BeTrue();
        summary.Scan.Should().BeNull("nichts entpackt — kein Folge-Scan nötig");
        env.Archives.Calls.Should().Be(2, "das Archiv mit vorhandenem Zielordner wird gar nicht erst geöffnet");
        env.Statuses.Last().Summary.Should().BeSameAs(summary);

        // Ein weiterer Scan fasst die gemeldeten Archive nicht erneut an (sie sind nicht mehr neu)
        await env.IncrementalAndWaitAsync();
        env.Archives.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Entpackt_erst_wenn_die_Sperre_frei_ist_und_ohne_Deadlock_mit_gemeinsamer_Sperre()
    {
        using var env = await Env.CreateAsync(enabled: true, recycle: true);
        var zip = NewZip(env, "warten.zip", ("a.jpg", Jpeg(100)));
        var target = Path.Combine(env.Inbox, "warten");

        // Wie ein Scan: Sperre belegen (löst Acquired aus), Archiv aufnehmen, Nachrichten senden — Sperre noch gehalten.
        var lease = await env.Gate.AcquireAsync(env.LibraryId, CancellationToken.None);
        int archiveId;
        await using (var ctx = env.Fx.CreateContext())
        {
            var folder = await ctx.Folders.SingleAsync(f => f.Path == env.Inbox);
            var row = new PhotoViewerV3.Domain.Entities.MediaFile
            {
                LibraryId = env.LibraryId,
                FolderId = folder.Id,
                Name = "warten.zip",
                Extension = ".zip",
                Size = new FileInfo(zip).Length,
                CreatedAtUtc = DateTime.UtcNow,
                ModifiedAtUtc = File.GetLastWriteTimeUtc(zip),
                MediaType = MediaType.Archive,
            };
            ctx.Files.Add(row);
            await ctx.SaveChangesAsync();
            archiveId = row.Id;
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO FilesFts(rowid, Name, Extension) VALUES ({0}, {1}, {2})", row.Id, row.Name, row.Extension);
        }

        env.Fx.Messenger.Send(new FilesChangedMessage(env.LibraryId, FilesChangeKind.Added, [archiveId]));
        env.Fx.Messenger.Send(new ScanCompletedMessage(env.LibraryId, new ScanResult(env.LibraryId, env.Fx.LibraryRoot, 0, 1, 1, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) { Mode = ScanMode.Incremental }));

        await Task.Delay(500);
        Directory.Exists(target).Should().BeFalse("solange die Sperre gehalten wird, wird nicht entpackt");
        env.Archives.Calls.Should().Be(0);

        lease.Dispose();
        await env.Service.WhenIdleAsync().WaitAsync(Timeout); // Entpacken → Papierkorb (holt die Sperre) → Folge-Scan (holt die Sperre)

        File.Exists(Path.Combine(target, "a.jpg")).Should().BeTrue();
        File.Exists(zip).Should().BeFalse();
        env.Gate.IsBusy(env.LibraryId).Should().BeFalse("alle Leases sind wieder frei");
        env.Service.LastSummary!.Scan.Should().NotBeNull();
        (await env.Fx.FindFileAsync("a.jpg")).Should().NotBeNull();
    }

    [Fact]
    public async Task Verschachtelte_Reste_werden_nicht_erneut_entpackt_keine_Schleife()
    {
        using var env = await Env.CreateAsync(enabled: true, recycle: true);
        var l5 = ZipBytes(("tief.jpg", Jpeg(100)));
        var l4 = ZipBytes(("l5.zip", l5));
        var l3 = ZipBytes(("l4.zip", l4));
        var l2 = ZipBytes(("l3.zip", l3));
        var zip = NewZip(env, "tief.zip", ("oben.jpg", Jpeg(100)), ("l2.zip", l2));

        await env.IncrementalAndWaitAsync();

        var target = Path.Combine(env.Inbox, "tief");
        File.Exists(Path.Combine(target, "oben.jpg")).Should().BeTrue();
        var leftover = Directory.EnumerateFiles(target, "l5.zip", SearchOption.AllDirectories).Should().ContainSingle(
            "Tiefe 3 erreicht: das innerste Archiv bleibt nach der bestehenden Regel liegen").Which;
        (await env.Fx.FindFileAsync("l5.zip")).Should().NotBeNull("der Folge-Scan nimmt es als neues Archiv auf …");
        env.Archives.Calls.Should().Be(1, "… und es wird trotzdem nicht automatisch entpackt (keine Schleife)");
        File.Exists(leftover).Should().BeTrue();
        File.Exists(zip).Should().BeTrue("mit Hinweisen entpackt — das Archiv bleibt erhalten");

        var item = env.Service.LastSummary!.Items.Should().ContainSingle().Which;
        item.State.Should().Be(ArchiveAutoExtractItemState.ExtractedWithErrors);
        item.Message.Should().Contain("Verschachtelungstiefe");
    }

    [Fact]
    public async Task Archive_aus_manuellem_Entpacken_bleiben_unberuehrt()
    {
        using var env = await Env.CreateAsync(enabled: true);
        var zip = NewZip(env, "manuell.zip", ("a.jpg", Jpeg(100)), ("innen.zip", ZipBytes(("b.jpg", Jpeg(100)))));
        await env.IncrementalAndWaitAsync(); // automatisch entpackt (neu)
        env.Archives.Calls.Should().Be(1);

        // Manuell (Entpacken-Dialog) ohne verschachtelte Runden: innen.zip landet als Datei im Zielordner und der Scan nimmt es auf
        var manualTarget = Path.Combine(env.Inbox, "manuell-kopie");
        var result = await env.Real.ExtractAsync(zip, new ArchiveExtractOptions
        {
            TargetFolder = manualTarget,
            ExtractNested = false,
            MediaOnly = false,
            LibraryId = env.LibraryId,
        });
        await env.Service.WhenIdleAsync().WaitAsync(Timeout);

        result.Scan.Should().NotBeNull();
        await using (var ctx = env.Fx.CreateContext())
        {
            (await ctx.Files.CountAsync(f => f.Name == "innen.zip")).Should().Be(1);
        }

        File.Exists(Path.Combine(manualTarget, "innen.zip")).Should().BeTrue();
        Directory.Exists(Path.Combine(manualTarget, "innen")).Should().BeFalse("Archive in einem entpackten Ordner werden nie automatisch entpackt");
        env.Archives.Calls.Should().Be(1);
    }
}

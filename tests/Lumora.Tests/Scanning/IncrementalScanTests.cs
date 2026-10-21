using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data.RawSql;
using Lumora.Domain.Entities;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

/// <summary>
/// ScanMode.Incremental: Diff-Re-Scan mit stabilen FileIds und Offline-vs-Missing-Guard (PLAN Kap. 12).
/// Jeder Test arbeitet auf einer eigenen Temp-Library (TempLibraryFixture) und einem eigenen Messenger.
/// </summary>
public sealed class IncrementalScanTests
{
    // ---- Helfer ---------------------------------------------------------------------------------

    private static ScanService CreateScanner(TempLibraryFixture fx) => new(
        fx.ContextFactory,
        new FolderEnumerator(fx.Formats, NullLogger<FolderEnumerator>.Instance),
        new BulkScanWriter(),
        new FtsIndexer(),
        new LibraryService(fx.ContextFactory, fx.Paths, NullLogger<LibraryService>.Instance),
        NullLogger<ScanService>.Instance,
        fx.Messenger);

    private static Task<ScanResult> IncrementalAsync(TempLibraryFixture fx, int libraryId, bool confirmMassMissing = false)
        => CreateScanner(fx).ScanAsync(
            libraryId,
            ScanMode.Incremental,
            new ScanOptions { ConfirmMassMissing = confirmMassMissing },
            progress: null,
            CancellationToken.None);

    /// <summary>Legt count Dateien in den Ordnern a und b an und macht den Fresh-Scan.</summary>
    private static async Task<int> SeedAndFreshScanAsync(TempLibraryFixture fx, int count = 10)
    {
        var library = await fx.CreateLibraryAsync();
        for (var i = 1; i <= count; i++)
        {
            fx.WriteLibraryFile(RelativePath(i));
        }

        var fresh = await fx.ScanAsync(library.Id);
        fresh.FilesInserted.Should().Be(count);
        return library.Id;
    }

    private static string RelativePath(int i) => Path.Combine(i % 2 == 0 ? "a" : "b", $"file{i:00}.jpg");

    private static string FullPath(TempLibraryFixture fx, int i) => Path.Combine(fx.LibraryRoot, RelativePath(i));

    private static async Task<Dictionary<string, MediaFile>> LoadFilesAsync(TempLibraryFixture fx, int libraryId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Files.AsNoTracking().Where(f => f.LibraryId == libraryId).ToDictionaryAsync(f => f.Name);
    }

    private static async Task<int> CountMissingAsync(TempLibraryFixture fx, int libraryId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Files.CountAsync(f => f.LibraryId == libraryId && f.Missing);
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Sammelt ScanCompleted-/ScanAbortedMessages eines Messengers (thread-sicher).</summary>
    private sealed class ScanMessageCollector
    {
        private readonly object _gate = new();
        private readonly List<ScanCompletedMessage> _completed = new();
        private readonly List<ScanAbortedMessage> _aborted = new();

        public ScanMessageCollector(IMessenger messenger)
        {
            messenger.Register<ScanMessageCollector, ScanCompletedMessage>(this, static (r, m) => r.Add(m));
            messenger.Register<ScanMessageCollector, ScanAbortedMessage>(this, static (r, m) => r.Add(m));
        }

        public IReadOnlyList<ScanCompletedMessage> Completed
        {
            get
            {
                lock (_gate)
                {
                    return _completed.ToList();
                }
            }
        }

        public IReadOnlyList<ScanAbortedMessage> Aborted
        {
            get
            {
                lock (_gate)
                {
                    return _aborted.ToList();
                }
            }
        }

        private void Add(ScanCompletedMessage m)
        {
            lock (_gate)
            {
                _completed.Add(m);
            }
        }

        private void Add(ScanAbortedMessage m)
        {
            lock (_gate)
            {
                _aborted.Add(m);
            }
        }
    }

    // ---- Tests ----------------------------------------------------------------------------------

    [Fact]
    public async Task Incremental_ohne_Aenderungen_liefert_Nullzaehler_und_keine_Dateinachrichten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var scanMessages = new ScanMessageCollector(fx.Messenger);

        var first = await IncrementalAsync(fx, libraryId);
        var second = await IncrementalAsync(fx, libraryId);

        foreach (var result in new[] { first, second })
        {
            result.Outcome.Should().Be(ScanOutcome.Completed);
            result.Mode.Should().Be(ScanMode.Incremental);
            result.Added.Should().Be(0);
            result.Modified.Should().Be(0);
            result.MarkedMissing.Should().Be(0);
            result.MissingDetected.Should().Be(0);
            result.Reappeared.Should().Be(0);
            result.FoldersAdded.Should().Be(0);
            result.FilesInserted.Should().Be(0);
            result.HasChanges.Should().BeFalse();
            result.TotalBytes.Should().Be(6 * 2048);
        }

        fx.Messages.FilesChanged.Should().BeEmpty("ohne Änderungen gibt es nichts nachzuladen");
        scanMessages.Completed.Should().HaveCount(2);
        scanMessages.Aborted.Should().BeEmpty();
    }

    [Fact]
    public async Task Neue_Datei_bekommt_fortlaufende_Id_und_ist_im_FTS_auffindbar()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var before = await LoadFilesAsync(fx, libraryId);
        var maxIdBefore = before.Values.Max(f => f.Id);
        fx.WriteLibraryFile(Path.Combine("a", "neu_zeta.jpg"));

        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Added.Should().Be(1);
        result.FilesInserted.Should().Be(1);
        result.FtsRowsInserted.Should().Be(1);
        result.FoldersAdded.Should().Be(0, "der Ordner a existiert bereits");
        result.HasChanges.Should().BeTrue();

        var after = await LoadFilesAsync(fx, libraryId);
        var added = after["neu_zeta.jpg"];
        added.Id.Should().BeGreaterThan(maxIdBefore);
        added.Missing.Should().BeFalse();
        added.FolderId.Should().Be(before["file02.jpg"].FolderId);
        fx.FtsRowIds("neu_zeta").Should().Equal(added.Id);
        foreach (var (name, file) in before)
        {
            after[name].Id.Should().Be(file.Id, "bestehende Ids bleiben stabil");
        }

        var addedMessages = fx.Messages.OfKind(FilesChangeKind.Added);
        addedMessages.Should().ContainSingle().Which.FileIds.Should().Equal(added.Id);
        addedMessages[0].LibraryId.Should().Be(libraryId);
    }

    [Fact]
    public async Task Geloeschte_Datei_wird_als_fehlend_markiert_Favorit_und_Bewertung_bleiben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 10);
        var before = await LoadFilesAsync(fx, libraryId);
        var victim = before["file05.jpg"];
        await using (var ctx = fx.CreateContext())
        {
            await ctx.Files.Where(f => f.Id == victim.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsFavorite, true).SetProperty(f => f.Rating, (byte?)4));
        }

        File.Delete(FullPath(fx, 5));

        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.MarkedMissing.Should().Be(1);
        result.MissingDetected.Should().Be(1);
        result.Added.Should().Be(0);
        result.Modified.Should().Be(0);

        var after = (await LoadFilesAsync(fx, libraryId))["file05.jpg"];
        after.Id.Should().Be(victim.Id);
        after.Missing.Should().BeTrue();
        after.IsFavorite.Should().BeTrue();
        after.Rating.Should().Be(4);
        fx.FtsRowIds("file05").Should().Equal(new[] { victim.Id }, "die Zeile bleibt im Katalog und im Suchindex");
        (await CountMissingAsync(fx, libraryId)).Should().Be(1);

        fx.Messages.OfKind(FilesChangeKind.Removed).Should().ContainSingle().Which.FileIds.Should().Equal(victim.Id);
        fx.Messages.OfKind(FilesChangeKind.Added).Should().BeEmpty();
    }

    [Fact]
    public async Task Zurueckgelegte_Datei_ist_wieder_vorhanden_mit_gleicher_Id()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 10);
        var original = (await LoadFilesAsync(fx, libraryId))["file03.jpg"];
        File.Delete(FullPath(fx, 3));
        (await IncrementalAsync(fx, libraryId)).MarkedMissing.Should().Be(1);
        fx.Messages.Clear();

        fx.WriteLibraryFile(RelativePath(3), size: 4096); // zurück, mit anderer Größe
        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Reappeared.Should().Be(1);
        result.Added.Should().Be(0);
        result.Modified.Should().Be(0, "Wiederauftauchen zählt nicht zusätzlich als Änderung");
        result.MarkedMissing.Should().Be(0);

        var after = (await LoadFilesAsync(fx, libraryId))["file03.jpg"];
        after.Id.Should().Be(original.Id);
        after.Missing.Should().BeFalse();
        after.Size.Should().Be(4096, "Metadaten werden beim Wiederauftauchen nachgeführt");
        (await CountMissingAsync(fx, libraryId)).Should().Be(0);

        fx.Messages.OfKind(FilesChangeKind.Added).Should().ContainSingle().Which.FileIds.Should().Equal(original.Id);
    }

    [Fact]
    public async Task Geaenderte_Datei_aktualisiert_Groesse_und_Aenderungszeit_bei_stabiler_Id()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var original = (await LoadFilesAsync(fx, libraryId))["file02.jpg"];
        var path = fx.WriteLibraryFile(RelativePath(2), size: 8192);
        var expectedModified = ToUnix(new FileInfo(path).LastWriteTimeUtc);

        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Modified.Should().Be(1);
        result.Added.Should().Be(0);
        result.MarkedMissing.Should().Be(0);

        var after = (await LoadFilesAsync(fx, libraryId))["file02.jpg"];
        after.Id.Should().Be(original.Id);
        after.Size.Should().Be(8192);
        ToUnix(after.ModifiedAtUtc).Should().Be(expectedModified);
        after.Missing.Should().BeFalse();

        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().ContainSingle().Which.FileIds.Should().Equal(original.Id);
    }

    [Fact]
    public async Task Geaenderte_Aenderungszeit_bei_gleicher_Groesse_zaehlt_als_geaendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var path = FullPath(fx, 4);
        var newTime = new FileInfo(path).LastWriteTimeUtc.AddHours(2);
        File.SetLastWriteTimeUtc(path, newTime);

        var result = await IncrementalAsync(fx, libraryId);

        result.Modified.Should().Be(1);
        var after = (await LoadFilesAsync(fx, libraryId))["file04.jpg"];
        ToUnix(after.ModifiedAtUtc).Should().Be(ToUnix(newTime));
        after.Size.Should().Be(2048);
    }

    [Fact]
    public async Task Neuer_Unterordner_mit_Dateien_legt_die_Ordnerkette_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 4);
        fx.WriteLibraryFile(Path.Combine("neu", "tief", "ganz", "x.jpg"));

        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.FoldersAdded.Should().Be(3, "neu, tief und ganz fehlten");
        result.Added.Should().Be(1);

        var root = (await fx.FindFolderAsync(fx.LibraryRoot))!;
        var neu = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "neu")))!;
        var tief = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "neu", "tief")))!;
        var ganz = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "neu", "tief", "ganz")))!;
        neu.ParentId.Should().Be(root.Id);
        tief.ParentId.Should().Be(neu.Id);
        ganz.ParentId.Should().Be(tief.Id);
        neu.Name.Should().Be("neu");
        ganz.LibraryId.Should().Be(libraryId);

        var file = (await fx.FindFileAsync("x.jpg"))!;
        file.FolderId.Should().Be(ganz.Id);
    }

    [Fact]
    public async Task Massenfehlen_blockiert_die_Markierung_schreibt_aber_neue_Dateien()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 10);
        var scanMessages = new ScanMessageCollector(fx.Messenger);
        File.Delete(FullPath(fx, 1));
        File.Delete(FullPath(fx, 2));
        File.Delete(FullPath(fx, 3)); // 30 % > 20 %
        fx.WriteLibraryFile(Path.Combine("a", "trotzdem_neu.jpg"));

        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.MassMissingBlocked);
        result.MissingDetected.Should().Be(3);
        result.MarkedMissing.Should().Be(0);
        result.Added.Should().Be(1, "neue Dateien werden auch bei blockierter Markierung übernommen");
        (await CountMissingAsync(fx, libraryId)).Should().Be(0, "nichts darf ohne Bestätigung als fehlend markiert werden");
        (await fx.FindFileAsync("trotzdem_neu.jpg")).Should().NotBeNull();

        fx.Messages.OfKind(FilesChangeKind.Removed).Should().BeEmpty();
        fx.Messages.OfKind(FilesChangeKind.Added).Should().ContainSingle();
        scanMessages.Completed.Should().ContainSingle().Which.Result.Outcome.Should().Be(ScanOutcome.MassMissingBlocked);
        scanMessages.Aborted.Should().BeEmpty();
    }

    [Fact]
    public async Task Massenfehlen_mit_Bestaetigung_markiert_die_Dateien()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 10);
        File.Delete(FullPath(fx, 1));
        File.Delete(FullPath(fx, 2));
        File.Delete(FullPath(fx, 3));
        (await IncrementalAsync(fx, libraryId)).Outcome.Should().Be(ScanOutcome.MassMissingBlocked);

        var confirmed = await IncrementalAsync(fx, libraryId, confirmMassMissing: true);

        confirmed.Outcome.Should().Be(ScanOutcome.Completed);
        confirmed.MarkedMissing.Should().Be(3);
        confirmed.MissingDetected.Should().Be(3);
        (await CountMissingAsync(fx, libraryId)).Should().Be(3);
        var after = await LoadFilesAsync(fx, libraryId);
        after["file01.jpg"].Missing.Should().BeTrue();
        after["file04.jpg"].Missing.Should().BeFalse();
        fx.Messages.OfKind(FilesChangeKind.Removed).Should().ContainSingle().Which.FileIds.Should().HaveCount(3);

        // Danach ist der Katalog konsistent: ein weiterer Lauf findet nichts mehr
        var again = await IncrementalAsync(fx, libraryId);
        again.HasChanges.Should().BeFalse();
        again.MissingDetected.Should().Be(0);
    }

    [Fact]
    public async Task Genau_zwanzig_Prozent_fehlend_werden_ohne_Rueckfrage_markiert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 10);
        File.Delete(FullPath(fx, 1));
        File.Delete(FullPath(fx, 2)); // 20 % ist nicht > 20 %

        var result = await IncrementalAsync(fx, libraryId);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.MarkedMissing.Should().Be(2);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1_000, false)]
    [InlineData(2, 10, false)]
    [InlineData(3, 10, true)]
    [InlineData(2_000, 10_000, false)]
    [InlineData(2_001, 10_000, true)]
    [InlineData(10_000, 1_000_000, false)]
    [InlineData(10_001, 1_000_000, true)]
    public void MassMissingGuard_greift_ueber_10000_oder_ueber_20_Prozent(int newlyMissing, int presentBefore, bool expected)
    {
        MassMissingGuard.Exceeds(newlyMissing, presentBefore).Should().Be(expected);
    }

    [Fact]
    public async Task Nicht_erreichbarer_Root_liefert_LibraryOffline_und_laesst_den_Katalog_unveraendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var before = await LoadFilesAsync(fx, libraryId);
        var scanMessages = new ScanMessageCollector(fx.Messenger);
        var moved = fx.LibraryRoot + "_weg";
        Directory.Move(fx.LibraryRoot, moved);

        try
        {
            var result = await IncrementalAsync(fx, libraryId);

            result.Outcome.Should().Be(ScanOutcome.LibraryOffline);
            result.RootPath.Should().Be(fx.LibraryRoot);
            result.HasChanges.Should().BeFalse();
            result.MarkedMissing.Should().Be(0);
            result.MissingDetected.Should().Be(0);
        }
        finally
        {
            Directory.Move(moved, fx.LibraryRoot);
        }

        (await CountMissingAsync(fx, libraryId)).Should().Be(0, "offline darf nie als fehlend gelten");
        var after = await LoadFilesAsync(fx, libraryId);
        after.Should().HaveCount(before.Count);
        after.Values.Select(f => f.Id).Should().BeEquivalentTo(before.Values.Select(f => f.Id));

        fx.Messages.FilesChanged.Should().BeEmpty();
        scanMessages.Completed.Should().BeEmpty();
        var aborted = scanMessages.Aborted.Should().ContainSingle().Subject;
        aborted.Canceled.Should().BeFalse();
        aborted.Reason.Should().Contain("nicht erreichbar");

        // Nach dem Zurückstellen ist alles wie vorher
        (await IncrementalAsync(fx, libraryId)).HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Fresh_bei_nicht_erreichbarem_Root_liefert_LibraryOffline_ohne_Katalogaenderung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("one.jpg");
        var moved = fx.LibraryRoot + "_weg";
        Directory.Move(fx.LibraryRoot, moved);

        try
        {
            var result = await CreateScanner(fx).ScanAsync(library.Id, ScanMode.Fresh, null, CancellationToken.None);

            result.Outcome.Should().Be(ScanOutcome.LibraryOffline);
            result.Mode.Should().Be(ScanMode.Fresh);
            result.FilesInserted.Should().Be(0);
        }
        finally
        {
            Directory.Move(moved, fx.LibraryRoot);
        }

        await using var ctx = fx.CreateContext();
        (await ctx.Files.CountAsync(f => f.LibraryId == library.Id)).Should().Be(0);
        (await ctx.Folders.CountAsync(f => f.LibraryId == library.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Ids_bleiben_ueber_Fresh_und_Incremental_stabil()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 8);
        var before = await LoadFilesAsync(fx, libraryId);
        var maxIdBefore = before.Values.Max(f => f.Id);

        fx.WriteLibraryFile(Path.Combine("b", "neu.jpg"));          // neu
        fx.WriteLibraryFile(RelativePath(2), size: 3000);           // geändert
        File.Delete(FullPath(fx, 7));                               // fehlt (1 von 8 = 12,5 %)

        var result = await IncrementalAsync(fx, libraryId);

        result.Added.Should().Be(1);
        result.Modified.Should().Be(1);
        result.MarkedMissing.Should().Be(1);
        var after = await LoadFilesAsync(fx, libraryId);
        after.Should().HaveCount(9);
        foreach (var (name, file) in before)
        {
            after[name].Id.Should().Be(file.Id, "{0} behält seine Id", name);
        }

        after["neu.jpg"].Id.Should().Be(maxIdBefore + 1, "Ids werden lückenlos fortgesetzt");
        after["file07.jpg"].Missing.Should().BeTrue();
        after["file02.jpg"].Size.Should().Be(3000);
    }

    [Fact]
    public async Task Incremental_auf_leerem_Katalog_nimmt_alles_auf_wie_ein_Erstscan()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "bravo.mp4"));
        fx.WriteLibraryFile(Path.Combine("b", "charlie.png"));
        fx.WriteLibraryFile(Path.Combine("b", "notes.txt")); // kein Medienformat

        var result = await IncrementalAsync(fx, library.Id);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Added.Should().Be(3);
        result.FilesInserted.Should().Be(3);
        result.FtsRowsInserted.Should().Be(3);
        result.FoldersAdded.Should().Be(3, "Root, a und b");
        result.MarkedMissing.Should().Be(0);
        fx.FtsRowIds("bravo").Should().HaveCount(1);
        fx.FtsRowIds("charlie").Should().HaveCount(1);
        (await fx.FindFolderAsync(fx.LibraryRoot))!.ParentId.Should().BeNull();

        (await IncrementalAsync(fx, library.Id)).HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAndRescan_bleibt_funktionsfaehig_und_vergibt_neue_Ids()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var maxIdBefore = (await LoadFilesAsync(fx, libraryId)).Values.Max(f => f.Id);

        var result = await CreateScanner(fx).ScanAsync(libraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Mode.Should().Be(ScanMode.ClearAndRescan);
        result.FilesInserted.Should().Be(6);
        result.Added.Should().Be(6);
        result.FoldersAdded.Should().Be(result.FoldersInserted);
        result.FtsRowsInserted.Should().Be(6);
        var after = await LoadFilesAsync(fx, libraryId);
        after.Should().HaveCount(6);
        after.Values.Min(f => f.Id).Should().BeGreaterThan(maxIdBefore, "ClearAndRescan vergibt neue Ids (Thumbnails sind FileId-basiert)");
        fx.FtsRowIds("file01").Should().HaveCount(1);
    }

    [Fact]
    public async Task Fresh_Ergebnis_traegt_Outcome_Completed_und_Diffzaehler()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "one.jpg"));
        fx.WriteLibraryFile("two.jpg");

        var result = await fx.ScanAsync(library.Id);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.Mode.Should().Be(ScanMode.Fresh);
        result.Added.Should().Be(result.FilesInserted).And.Be(2);
        result.FoldersAdded.Should().Be(result.FoldersInserted).And.Be(2);
        result.Modified.Should().Be(0);
        result.MarkedMissing.Should().Be(0);
        result.Reappeared.Should().Be(0);
        result.HasChanges.Should().BeTrue();
    }

    [Fact]
    public async Task Umbenannte_Datei_ergibt_eine_neue_und_eine_fehlende()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var oldId = (await LoadFilesAsync(fx, libraryId))["file01.jpg"].Id;
        File.Move(FullPath(fx, 1), Path.Combine(fx.LibraryRoot, "b", "umbenannt.jpg"));

        var result = await IncrementalAsync(fx, libraryId);

        result.Added.Should().Be(1);
        result.MarkedMissing.Should().Be(1);
        var after = await LoadFilesAsync(fx, libraryId);
        after["file01.jpg"].Missing.Should().BeTrue();
        after["file01.jpg"].Id.Should().Be(oldId);
        after["umbenannt.jpg"].Missing.Should().BeFalse();
        after["umbenannt.jpg"].Id.Should().BeGreaterThan(oldId);
    }

    [Fact]
    public async Task Schreibweise_des_Dateinamens_wird_wie_bisher_ignoriert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var path = FullPath(fx, 2);
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "FILE02.JPG"));

        var result = await IncrementalAsync(fx, libraryId);

        result.HasChanges.Should().BeFalse("Katalog und Dateisystem vergleichen Namen case-insensitiv");
        result.MissingDetected.Should().Be(0);
        (await LoadFilesAsync(fx, libraryId)).Should().ContainKey("file02.jpg");
    }

    [Fact]
    public async Task Abbruch_vor_der_Persistenz_schreibt_nichts_und_meldet_Abbruch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 6);
        var scanMessages = new ScanMessageCollector(fx.Messenger);
        fx.WriteLibraryFile(Path.Combine("a", "neu.jpg"));
        File.Delete(FullPath(fx, 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => CreateScanner(fx).ScanAsync(libraryId, ScanMode.Incremental, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await fx.FindFileAsync("neu.jpg")).Should().BeNull();
        (await CountMissingAsync(fx, libraryId)).Should().Be(0);
        scanMessages.Aborted.Should().ContainSingle().Which.Canceled.Should().BeTrue();
        scanMessages.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Default_Overload_ohne_Optionen_bleibt_nutzbar()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var libraryId = await SeedAndFreshScanAsync(fx, 4);
        IScanService scanner = CreateScanner(fx);

        var result = await scanner.ScanAsync(libraryId, ScanMode.Incremental, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.HasChanges.Should().BeFalse();
    }
}

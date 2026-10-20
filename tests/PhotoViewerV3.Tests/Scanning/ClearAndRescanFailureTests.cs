using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Scanning;

/// <summary>
/// „Neu aufbauen“ (ClearAndRescan) ersetzt den Katalog in EINER Transaktion (ADR-0028): Abbruch, Ausnahme oder Absturz
/// an jeder Stelle lassen den bisherigen Katalog samt Favoriten, Bewertungen, Tags und Kategorie-Werten unverändert
/// (gleiche FileIds, gleicher Suchindex, gleiche Thumbnail-Zeilen). Die Fehler werden über einen synchronen
/// <see cref="IProgress{T}"/> an den Phasengrenzen ausgelöst — der Scan meldet jede Phase innerhalb der Transaktion.
/// </summary>
public sealed class ClearAndRescanFailureTests
{
    [Theory]
    [InlineData(ScanPhase.Enumerating)] // vor jeder Katalogänderung
    [InlineData(ScanPhase.Clearing)]    // Abbruch greift direkt nach dem Löschen der alten Zeilen
    [InlineData(ScanPhase.Persisting)]  // nach dem Löschen, vor bzw. während des Bulk-Inserts
    [InlineData(ScanPhase.Indexing)]    // nach Insert und Rückschreiben der Benutzerdaten, vor dem Suchindex
    public async Task Abbruch_in_jeder_Phase_laesst_Katalog_und_Benutzerdaten_unveraendert(ScanPhase phase)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var arranged = await ArrangeAsync(fx);
        var before = Snapshot(fx);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == phase)
            {
                cts.Cancel();
            }
        });

        var act = () => fx.CreateScanService().ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, progress, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        progress.Phases.Should().Contain(phase);
        Snapshot(fx).Should().Be(before, "der bisherige Katalog bleibt bis zum Commit unangetastet");
        Directory.Exists(fx.Paths.GetLibraryThumbnailsPath(arranged.LibraryId)).Should().BeTrue("Vorschaubilder erst nach dem Commit verwerfen");
    }

    [Theory]
    [InlineData(ScanPhase.Clearing)]
    [InlineData(ScanPhase.Persisting)]
    [InlineData(ScanPhase.Indexing)]
    public async Task Ausnahme_mitten_im_Scan_laesst_Katalog_und_Benutzerdaten_unveraendert(ScanPhase phase)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var arranged = await ArrangeAsync(fx);
        var before = Snapshot(fx);
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == phase)
            {
                throw new IOException($"simulierter Fehler in Phase {phase}");
            }
        });

        var act = () => fx.CreateScanService().ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, progress, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        Snapshot(fx).Should().Be(before);

        // Danach läuft „Neu aufbauen“ normal durch und übernimmt die Daten auf neue FileIds.
        var result = await fx.CreateScanService().ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);
        result.Outcome.Should().Be(ScanOutcome.Completed);
        UserDataByPath(Snapshot(fx)).Should().Be(UserDataByPath(before));
    }

    [Fact]
    public async Task Simulierter_Absturz_vor_dem_Commit_hinterlaesst_den_alten_Katalog_und_der_naechste_Lauf_gelingt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var arranged = await ArrangeAsync(fx);
        var before = Snapshot(fx);
        var crashDir = Path.Combine(fx.RootDir, "nach-absturz");

        // „Strom weg“ nach Löschen, Neu-Einfügen und Rückschreiben, vor dem Suchindex: die DB-Dateien so kopieren, wie
        // sie in diesem Moment auf der Platte liegen (Hauptdatei + WAL, ohne -shm), dann den Lauf hart beenden.
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == ScanPhase.Indexing)
            {
                Directory.CreateDirectory(crashDir);
                CopyShared(fx.Paths.LibraryDatabasePath, Path.Combine(crashDir, "library.db"));
                var wal = fx.Paths.LibraryDatabasePath + "-wal";
                if (File.Exists(wal))
                {
                    CopyShared(wal, Path.Combine(crashDir, "library.db-wal"));
                }

                throw new SimulatedCrashException();
            }
        });

        var act = () => fx.CreateScanService().ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, progress, CancellationToken.None);
        await act.Should().ThrowAsync<SimulatedCrashException>();

        // Wiederaufnahme: die „abgestürzte“ DB öffnen — SQLite verwirft die nie committete Transaktion.
        var crashPaths = new TestAppDataPaths(crashDir);
        crashPaths.EnsureDirectoriesExist();
        var crashFactory = new LibraryContextFactory(crashPaths, new SqlitePragmaInterceptor());
        Snapshot(crashFactory).Should().Be(before, "nach dem Absturz ist der Katalog samt Benutzerdaten der alte");

        var scanner = new ScanService(
            crashFactory,
            new FolderEnumerator(fx.Formats, NullLogger<FolderEnumerator>.Instance),
            new BulkScanWriter(),
            new FtsIndexer(),
            new LibraryService(crashFactory, crashPaths, NullLogger<LibraryService>.Instance, new WeakReferenceMessenger()),
            NullLogger<ScanService>.Instance,
            new WeakReferenceMessenger());
        var result = await scanner.ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        var after = Snapshot(crashFactory);
        UserDataByPath(after).Should().Be(UserDataByPath(before), "der nächste Lauf übernimmt Favoriten, Bewertungen, Tags und Kategorien");
        MinFileId(crashFactory).Should().BeGreaterThan(arranged.MaxFileId, "weiterhin neue FileIds");
    }

    [Fact]
    public async Task Erfolgreicher_Neuaufbau_uebernimmt_Kategorien_und_Aenderungen_waehrend_der_Enumeration()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var arranged = await ArrangeAsync(fx);
        var catalog = fx.CreateCatalogUpdateService();
        var changed = false;

        // Der alte Katalog bleibt während der Enumeration bedienbar; was der Benutzer dort noch ändert, wird mitgesichert.
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == ScanPhase.Enumerating && !changed)
            {
                changed = true;
                catalog.SetRatingAsync(new[] { arranged.Bravo }, 5).GetAwaiter().GetResult();
            }
        });

        var result = await fx.CreateScanService().ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, progress, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        changed.Should().BeTrue();
        var data = UserDataByPath(Snapshot(fx));
        data.Should().Contain(Path.Combine(fx.LibraryRoot, "alpha.jpg") + "|fav=1|rating=4|tags=Urlaub|cat=Ort:Berg");
        data.Should().Contain(Path.Combine(fx.LibraryRoot, "sub", "bravo.jpg") + "|fav=0|rating=5|tags=|cat=Ort:See", "Kategorie-Wert übernommen, Bewertung aus der Enumeration");
        Directory.Exists(fx.Paths.GetLibraryThumbnailsPath(arranged.LibraryId)).Should().BeFalse("Vorschaubilder der alten FileIds werden nach dem Commit verworfen");
        MinFileId(fx.ContextFactory).Should().BeGreaterThan(arranged.MaxFileId);
    }

    [Fact]
    public async Task Ids_die_ein_anderer_Scan_waehrend_der_Enumeration_vergibt_gehen_nicht_verloren()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var arranged = await ArrangeAsync(fx);
        fx.WriteInboxFile("b1.jpg");
        fx.WriteInboxFile("b2.jpg");
        var other = await fx.CreateLibraryService().CreateAsync("Zweite", fx.InboxDir);
        var before = UserDataByPath(Snapshot(fx));
        var otherScanned = false;

        // Die Scan-Sperre gilt je Bibliothek: während „Neu aufbauen“ von A enumeriert, vergibt ein Scan von B die Ids,
        // die A vor der Enumeration als Start-Ids gelesen hat.
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == ScanPhase.Enumerating && !otherScanned)
            {
                otherScanned = true;
                fx.CreateScanService().ScanAsync(other.Id, ScanMode.Fresh, null, CancellationToken.None).GetAwaiter().GetResult()
                    .FilesInserted.Should().Be(2);
            }
        });

        var result = await fx.CreateScanService().ScanAsync(arranged.LibraryId, ScanMode.ClearAndRescan, progress, CancellationToken.None);

        otherScanned.Should().BeTrue();
        result.FilesInserted.Should().Be(3, "keine Zeile von A wird wegen einer belegten Id still verworfen");
        await using var ctx = fx.CreateContext();
        ctx.Files.Count(f => f.LibraryId == arranged.LibraryId).Should().Be(3);
        ctx.Files.Count(f => f.LibraryId == other.Id).Should().Be(2);
        ctx.Folders.Count(f => f.LibraryId == other.Id).Should().Be(1);
        var foldersA = ctx.Folders.Where(f => f.LibraryId == arranged.LibraryId).ToList();
        foldersA.Where(f => f.ParentId != null).Should().OnlyContain(f => foldersA.Any(p => p.Id == f.ParentId), "verschobene ParentIds zeigen auf Ordner von A");
        ctx.Files.Where(f => f.LibraryId == arranged.LibraryId).ToList().Should().OnlyContain(f => foldersA.Any(d => d.Id == f.FolderId));
        var afterA = UserDataByPath(Snapshot(fx)).Split(Environment.NewLine).Where(l => l.StartsWith(fx.LibraryRoot, StringComparison.OrdinalIgnoreCase));
        string.Join(Environment.NewLine, afterA).Should().Be(before, "Benutzerdaten von A übernommen");
    }

    // ---- Aufbau -----------------------------------------------------------------------------------

    private sealed record Arranged(int LibraryId, int Alpha, int Bravo, int MaxFileId);

    /// <summary>Drei Dateien; alpha: Favorit, Bewertung 4, Tag, Kategorie-Wert, Thumbnail-Zeile + Cache-Datei; bravo: nur Kategorie-Wert.</summary>
    private static async Task<Arranged> ArrangeAsync(TempLibraryFixture fx)
    {
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("alpha.jpg");
        fx.WriteLibraryFile("sub\\bravo.jpg");
        fx.WriteLibraryFile("sub\\charlie.jpg");
        await fx.ScanAsync(lib.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!.Id;
        var bravo = (await fx.FindFileAsync("bravo.jpg"))!.Id;
        var charlie = (await fx.FindFileAsync("charlie.jpg"))!.Id;

        var catalog = fx.CreateCatalogUpdateService();
        await catalog.SetFavoriteAsync(new[] { alpha }, true);
        await catalog.SetRatingAsync(new[] { alpha }, 4);
        var tag = await fx.CreateTagService().CreateAsync("Urlaub");
        await fx.CreateTagService().AssignAsync(new[] { alpha }, new[] { tag.Id });
        using (var categories = fx.CreateCategoryService())
        {
            var ort = await categories.CreateAsync("Ort", ["Berg", "See"]);
            await categories.SetValueAsync([alpha], ort.Id, ort.Values[0].Id);
            await categories.SetValueAsync([bravo], ort.Id, ort.Values[1].Id);
        }

        await using (var ctx = fx.CreateContext())
        {
            ctx.Thumbnails.Add(new Thumbnail { FileId = alpha, Size = 256, Format = "jpg", Status = Domain.Enums.ThumbnailStatus.Generated, CachePath = "x.jpg" });
            await ctx.SaveChangesAsync();
        }

        var thumbDir = fx.Paths.GetLibraryThumbnailsPath(lib.Id);
        Directory.CreateDirectory(thumbDir);
        await File.WriteAllBytesAsync(Path.Combine(thumbDir, $"{alpha}_256_still.jpg"), new byte[] { 1, 2, 3 });
        return new Arranged(lib.Id, alpha, bravo, Math.Max(alpha, Math.Max(bravo, charlie)));
    }

    // ---- Katalog-Abbild ---------------------------------------------------------------------------

    private static string Snapshot(TempLibraryFixture fx) => Snapshot(fx.ContextFactory);

    /// <summary>
    /// Textabbild von allem, was ein abgebrochener Neuaufbau nicht verändern darf: Dateien mit FileId und Benutzerdaten,
    /// Ordner, Thumbnail-Zeilen und der Suchindex (FTS-Treffer je Name).
    /// </summary>
    private static string Snapshot(ILibraryContextFactory factory)
    {
        using var connection = factory.OpenSqliteConnection();
        var lines = new List<string>();
        lines.AddRange(Query(connection, """
            SELECT 'file|' || f.Id || '|' || d.Path || '\' || f.Name
                   || '|fav=' || f.IsFavorite || '|rating=' || COALESCE(f.Rating, '')
                   || '|tags=' || COALESCE((SELECT GROUP_CONCAT(t.Name) FROM FileTags ft JOIN Tags t ON t.Id = ft.TagId WHERE ft.FileId = f.Id), '')
                   || '|cat=' || COALESCE((SELECT GROUP_CONCAT(c.Name || ':' || v.Name) FROM FileCategoryValues x
                                            JOIN Categories c ON c.Id = x.CategoryId JOIN CategoryValues v ON v.Id = x.ValueId
                                            WHERE x.FileId = f.Id), '')
            FROM Files f JOIN Folders d ON d.Id = f.FolderId ORDER BY f.Id;
            """));
        lines.AddRange(Query(connection, "SELECT 'folder|' || Id || '|' || Path FROM Folders ORDER BY Id;"));
        lines.AddRange(Query(connection, "SELECT 'thumb|' || FileId || '|' || Size FROM Thumbnails ORDER BY FileId;"));
        foreach (var term in new[] { "alpha", "bravo", "charlie" })
        {
            lines.Add($"fts|{term}|" + string.Join(",", Query(connection, $"SELECT rowid FROM FilesFts WHERE FilesFts MATCH '\"{term}\"' ORDER BY rowid;")));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Nur die Benutzerdaten je Pfad (ohne FileIds) — vergleichbar über einen erfolgreichen Neuaufbau hinweg.</summary>
    private static string UserDataByPath(string snapshot) =>
        string.Join(
            Environment.NewLine,
            snapshot.Split(Environment.NewLine)
                .Where(l => l.StartsWith("file|", StringComparison.Ordinal))
                .Select(l => l.Split('|', 3)[2])
                .Order(StringComparer.OrdinalIgnoreCase));

    private static int MinFileId(ILibraryContextFactory factory)
    {
        using var connection = factory.OpenSqliteConnection();
        return int.Parse(Query(connection, "SELECT MIN(Id) FROM Files;").Single());
    }

    private static List<string> Query(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return rows;
    }

    /// <summary>Kopiert eine von SQLite geöffnete Datei (Lese-/Schreib-Freigabe wie SQLite selbst).</summary>
    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        input.CopyTo(output);
    }

    private sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException()
            : base("simulierter Absturz")
        {
        }
    }

    /// <summary>Meldet synchron auf dem Scan-Thread (anders als <see cref="Progress{T}"/>), damit der Test mitten in der Transaktion eingreift.</summary>
    private sealed class SyncProgress(Action<ScanProgress> onReport) : IProgress<ScanProgress>
    {
        private readonly List<ScanPhase> _phases = new();

        public IReadOnlyList<ScanPhase> Phases
        {
            get
            {
                lock (_phases)
                {
                    return _phases.ToList();
                }
            }
        }

        public void Report(ScanProgress value)
        {
            lock (_phases)
            {
                _phases.Add(value.Phase);
            }

            onReport(value);
        }
    }
}

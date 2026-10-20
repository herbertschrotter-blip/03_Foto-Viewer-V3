using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Tests.Fixtures;
using Xunit.Abstractions;

namespace PhotoViewerV3.Tests.Scanning;

/// <summary>
/// „Neu aufbauen“ (ClearAndRescan) übernimmt Favorit, Bewertung und Tags über den Pfad auf die neuen FileIds
/// (ADR-0023, <see cref="UserDataCarryOver"/>); nicht mehr vorhandene Dateien verlieren sie.
/// </summary>
public sealed class ClearAndRescanUserDataTests
{
    private readonly ITestOutputHelper _output;

    public ClearAndRescanUserDataTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Favorit_Bewertung_und_Tags_ueberleben_bei_zwei_Wurzeln_und_gleichnamigen_Dateien()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        await fx.AddInboxRootAsync(lib.Id);
        var a2024 = fx.WriteLibraryFile("2024\\a.jpg");
        var a2025 = fx.WriteLibraryFile("2025\\a.jpg");
        var aInbox = fx.WriteInboxFile("kamera\\a.jpg");
        var plain = fx.WriteLibraryFile("2024\\b.jpg");
        (await fx.ScanAsync(lib.Id)).FilesInserted.Should().Be(4);

        var ids = await IdsByPathAsync(fx);
        var urlaub = await AddTagAsync(fx, "Urlaub");
        var familie = await AddTagAsync(fx, "Familie");
        await SetUserDataAsync(fx, ids[a2024], favorite: true, rating: null);
        await SetUserDataAsync(fx, ids[a2025], favorite: false, rating: 3);
        await AssignAsync(fx, ids[a2024], familie);
        await AssignAsync(fx, ids[aInbox], urlaub, familie);

        var result = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.RootsScanned.Should().Be(2);
        var after = await FilesByPathAsync(fx);
        after.Should().HaveCount(4);
        after.Values.Min(f => f.Id).Should().BeGreaterThan(ids.Values.Max(), "ClearAndRescan vergibt weiterhin neue FileIds");

        after[a2024].IsFavorite.Should().BeTrue();
        after[a2024].Rating.Should().BeNull();
        after[a2025].IsFavorite.Should().BeFalse("gleicher Name in anderem Ordner bekommt nicht die Daten des Nachbarn");
        after[a2025].Rating.Should().Be(3);
        after[aInbox].IsFavorite.Should().BeFalse();
        after[aInbox].Rating.Should().BeNull();
        after[plain].IsFavorite.Should().BeFalse();
        after[plain].Rating.Should().BeNull();

        var tags = await TagIdsByFileAsync(fx);
        tags[after[a2024].Id].Should().BeEquivalentTo(new[] { familie });
        tags[after[aInbox].Id].Should().BeEquivalentTo(new[] { urlaub, familie }, "Eingangs-Wurzel wie Archiv-Wurzel");
        tags.Should().NotContainKey(after[a2025].Id);
        tags.Should().NotContainKey(after[plain].Id);
        tags.Keys.Should().OnlyContain(id => after.Values.Any(f => f.Id == id), "keine Zuweisung auf alte FileIds");
    }

    [Fact]
    public async Task Entfernte_Datei_verliert_ihre_Daten_Tags_bleiben_erhalten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var keep = fx.WriteLibraryFile("keep.jpg");
        var gone = fx.WriteLibraryFile("sub\\gone.jpg");
        await fx.ScanAsync(lib.Id);
        var ids = await IdsByPathAsync(fx);
        var tag = await AddTagAsync(fx, "Weg");
        await SetUserDataAsync(fx, ids[gone], favorite: true, rating: 5);
        await AssignAsync(fx, ids[gone], tag);
        await SetUserDataAsync(fx, ids[keep], favorite: true, rating: 1);

        File.Delete(gone);
        var result = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        var after = await FilesByPathAsync(fx);
        after.Keys.Should().BeEquivalentTo(new[] { keep });
        after[keep].IsFavorite.Should().BeTrue();
        after[keep].Rating.Should().Be(1);
        await using var ctx = fx.CreateContext();
        (await ctx.FileTags.CountAsync()).Should().Be(0, "die Zuweisung der entfernten Datei ist weg");
        (await ctx.Tags.AnyAsync(t => t.Id == tag)).Should().BeTrue("die Tabelle Tags bleibt unverändert");
    }

    [Fact]
    public async Task Nur_in_Gross_Kleinschreibung_umbenannte_Datei_behaelt_ihre_Daten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var original = fx.WriteLibraryFile("Ordner\\IMG_0001.JPG");
        await fx.ScanAsync(lib.Id);
        var ids = await IdsByPathAsync(fx);
        var tag = await AddTagAsync(fx, "Fall");
        await SetUserDataAsync(fx, ids[original], favorite: true, rating: 2);
        await AssignAsync(fx, ids[original], tag);

        var renamed = Path.Combine(fx.LibraryRoot, "Ordner", "img_0001.jpg");
        File.Move(original, renamed); // Windows: nur die Schreibweise ändert sich
        await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var file = (await FilesByPathAsync(fx)).Values.Single();
        file.Name.Should().Be("img_0001.jpg");
        file.IsFavorite.Should().BeTrue();
        file.Rating.Should().Be(2);
        (await TagIdsByFileAsync(fx))[file.Id].Should().BeEquivalentTo(new[] { tag });
    }

    [Fact]
    public async Task Suchindex_und_Thumbnails_bleiben_konsistent()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var alpha = fx.WriteLibraryFile("alpha.jpg");
        fx.WriteLibraryFile("bravo.jpg");
        await fx.ScanAsync(lib.Id);
        var ids = await IdsByPathAsync(fx);
        await SetUserDataAsync(fx, ids[alpha], favorite: true, rating: 4);
        await using (var ctx = fx.CreateContext())
        {
            ctx.Thumbnails.Add(new Thumbnail { FileId = ids[alpha], Size = 256, Format = "jpg", Status = Domain.Enums.ThumbnailStatus.Generated, CachePath = "x.jpg" });
            await ctx.SaveChangesAsync();
        }

        await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var after = await FilesByPathAsync(fx);
        fx.FtsRowIds("alpha").Should().Equal(after[alpha].Id);
        fx.FtsRowIds("bravo").Should().HaveCount(1);
        await using var check = fx.CreateContext();
        (await check.Thumbnails.CountAsync()).Should().Be(0, "Vorschaubilder werden wie bisher neu erzeugt");
        after[alpha].IsFavorite.Should().BeTrue();
        after[alpha].Rating.Should().Be(4);
    }

    /// <summary>
    /// Mengen-Messung ohne Dateisystem: synthetischer Katalog, Sichern, Leeren, Neu-Einfügen mit neuen Ids,
    /// Zurückschreiben. Jede Datei hat eine Bewertung (schlechtester Fall), jede zweite ist Favorit, jede zehnte hat
    /// zwei Tags, jede hundertste kommt in anderer Schreibweise zurück. Standard 20 000 Dateien; die Zahl für
    /// 500 000 entsteht mit <c>PV3_CARRYOVER_ROWS=500000</c> (siehe CHANGELOG 0.2.14).
    /// </summary>
    [Fact]
    public async Task Mengen_Messung_synthetischer_Katalog()
    {
        var rows = int.TryParse(Environment.GetEnvironmentVariable("PV3_CARRYOVER_ROWS"), out var n) && n > 0 ? n : 20_000;
        const int filesPerFolder = 200;
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var roots = await fx.GetRootsAsync(lib.Id);
        var writer = new BulkScanWriter();

        InsertCatalog(fx, writer, lib.Id, roots[0].Id, rows, filesPerFolder, firstFolderId: 1, firstFileId: 1, renameEvery: 0);
        var tagA = await AddTagAsync(fx, "A");
        var tagB = await AddTagAsync(fx, "B");
        using (var connection = fx.ContextFactory.OpenSqliteConnection())
        {
            Exec(connection, "UPDATE Files SET Rating = (Id % 5) + 1, IsFavorite = (Id % 2 = 0);");
            Exec(connection, $"INSERT INTO FileTags(FileId, TagId) SELECT Id, {tagA} FROM Files WHERE Id % 10 = 0;");
            Exec(connection, $"INSERT INTO FileTags(FileId, TagId) SELECT Id, {tagB} FROM Files WHERE Id % 10 = 0;");
        }

        // Wie FullScanCatalogWriter (ADR-0028): Sichern, alte Zeilen löschen, neu einfügen, zurückschreiben und
        // Suchindex in EINER Transaktion.
        var total = Stopwatch.StartNew();
        UserDataCarryOver carry;
        UserDataCarryOver.RestoreReport report;
        using (var connection = fx.ContextFactory.OpenSqliteConnection())
        using (var tx = connection.BeginTransaction())
        {
            carry = UserDataCarryOver.Capture(connection, tx, lib.Id);
            Exec(connection, tx, $"DELETE FROM Thumbnails WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = {lib.Id});");
            Exec(connection, tx, $"DELETE FROM Files WHERE LibraryId = {lib.Id};");
            Exec(connection, tx, $"DELETE FROM Folders WHERE LibraryId = {lib.Id};");
            var folderOffset = rows / filesPerFolder + 10;
            InsertCatalog(fx, writer, lib.Id, roots[0].Id, rows, filesPerFolder, firstFolderId: folderOffset, firstFileId: rows + 1, renameEvery: 100, connection, tx);
            report = carry.Restore();
            new FtsIndexer().PopulateAll(connection, tx);
            tx.Commit();
        }

        total.Stop();

        _output.WriteLine(
            $"{rows:N0} Dateien: Sichern {carry.CaptureDuration.TotalMilliseconds:N0} ms ({carry.CapturedFiles:N0} Dateien, {carry.CapturedTagAssignments:N0} Tags), " +
            $"Zurückschreiben {report.Duration.TotalMilliseconds:N0} ms ({report.RestoredFiles:N0} Dateien, {report.TagAssignmentsRestored:N0} Tags), " +
            $"gesamt inkl. Löschen + Neu-Einfügen + Suchindex in einer Transaktion {total.Elapsed.TotalSeconds:N2} s");

        carry.CapturedFiles.Should().Be(rows);
        report.RestoredFiles.Should().Be(rows, "auch die in anderer Schreibweise zurückgekehrten Dateien");
        report.LostFiles.Should().Be(0);
        report.FavoritesOrRatingsRestored.Should().Be(rows);
        report.TagAssignmentsRestored.Should().Be(2 * (rows / 10));
        using (var connection = fx.ContextFactory.OpenSqliteConnection())
        {
            // Neue Id = alte Id + rows → Werte müssen exakt der alten Formel folgen
            Scalar(connection, $"SELECT COUNT(*) FROM Files WHERE Rating = ((Id - {rows}) % 5) + 1 AND IsFavorite = ((Id - {rows}) % 2 = 0)").Should().Be(rows);
            Scalar(connection, $"SELECT COUNT(*) FROM FileTags WHERE (FileId - {rows}) % 10 = 0").Should().Be(2 * (rows / 10));
        }

        report.Duration.Should().BeLessThan(TimeSpan.FromSeconds(Math.Max(10, rows / 25_000)), "mengenbasiert, keine Zeilenschleife");
    }

    // ---- Helfer -----------------------------------------------------------------------------------

    private static void InsertCatalog(
        TempLibraryFixture fx, BulkScanWriter writer, int libraryId, int rootId, int rows, int filesPerFolder,
        int firstFolderId, int firstFileId, int renameEvery,
        Microsoft.Data.Sqlite.SqliteConnection? connection = null, Microsoft.Data.Sqlite.SqliteTransaction? tx = null)
    {
        var folders = new List<BulkScanWriter.FolderRow>();
        var files = new List<BulkScanWriter.FileRow>(rows);
        var folderCount = (rows + filesPerFolder - 1) / filesPerFolder;
        for (var i = 0; i < folderCount; i++)
        {
            var path = Path.Combine(fx.LibraryRoot, $"{2000 + i % 25}", $"Ordner {i:D5}");
            folders.Add(new BulkScanWriter.FolderRow(firstFolderId + i, libraryId, path, null, $"Ordner {i:D5}", 0, rootId));
        }

        for (var i = 0; i < rows; i++)
        {
            var name = $"IMG_{i:D7}.jpg";
            if (renameEvery > 0 && i % renameEvery == 0)
            {
                name = name.ToLowerInvariant();
            }

            files.Add(new BulkScanWriter.FileRow(
                firstFileId + i, libraryId, firstFolderId + i / filesPerFolder, name, ".jpg", 1000 + i, 0, 0, 1,
                null, null, null, null, false, null, false));
        }

        if (connection is not null)
        {
            // In der Transaktion des Aufrufers; den Suchindex baut der Aufrufer.
            writer.InsertFolders(connection, folders, tx);
            writer.InsertFiles(connection, files, transaction: tx);
            return;
        }

        using var own = fx.ContextFactory.OpenSqliteConnection();
        writer.InsertFolders(own, folders);
        writer.InsertFiles(own, files);
        new FtsIndexer().PopulateAll(own);
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
        => Exec(connection, null, sql);

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static async Task<int> AddTagAsync(TempLibraryFixture fx, string name)
        => (await fx.CreateTagService().CreateAsync(name)).Id;

    private static Task AssignAsync(TempLibraryFixture fx, int fileId, params int[] tagIds)
        => fx.CreateTagService().AssignAsync(new[] { fileId }, tagIds);

    private static async Task SetUserDataAsync(TempLibraryFixture fx, int fileId, bool favorite, byte? rating)
    {
        var catalog = fx.CreateCatalogUpdateService();
        await catalog.SetFavoriteAsync(new[] { fileId }, favorite);
        await catalog.SetRatingAsync(new[] { fileId }, rating);
    }

    private static async Task<Dictionary<string, int>> IdsByPathAsync(TempLibraryFixture fx)
        => (await FilesByPathAsync(fx)).ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Dateien nach vollem Pfad (Ordnerpfad + Name, ohne Groß-/Kleinschreibung).</summary>
    private static async Task<Dictionary<string, MediaFile>> FilesByPathAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        var folders = await ctx.Folders.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Path);
        var files = await ctx.Files.AsNoTracking().ToListAsync();
        return files.ToDictionary(f => Path.Combine(folders[f.FolderId], f.Name), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<int, List<int>>> TagIdsByFileAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        var rows = await ctx.FileTags.AsNoTracking().ToListAsync();
        return rows.GroupBy(r => r.FileId).ToDictionary(g => g.Key, g => g.Select(r => r.TagId).ToList());
    }
}

using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data.RawSql;
using Lumora.Domain.Entities;
using Lumora.Gallery.Data;
using Lumora.Services.Categories;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Tags;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Messung Suche und Kategorie-Sortierung der Galerie-SQL an einem synthetischen Katalog (Spike 08). Größe über
/// <c>LUMORA_SEARCHSORT_FILES</c> (Standard 3 000 — im normalen Testlauf nur Funktionsprobe); die Werte aus
/// <c>docs/spikes/08-suche-sortierung.md</c> stammen aus Läufen mit 500 000. Optional <c>LUMORA_SEARCHSORT_DIR</c>: Ordner
/// der Test-DB, wird nicht gelöscht (zum Nachmessen einzelner Abfragen).
/// <para>
/// Katalog: 200 Dateien je Ordner, Änderungsdatum quer zur Id verteilt. Tags: 40 Hintergrund-Tags, je Datei zwei
/// (≈ 2 Zuweisungen je Datei), dazu „Urlaub“ (jede 100.) und „Familie“ (jede 5.). Kategorien: „Qualität“ (5 Werte,
/// 80 % der Dateien), „Projekt“ (12 Werte, 50 %), „Kunde“ (3 Werte, 2 %).
/// </para>
/// </summary>
public sealed class GallerySearchSortMeasurementTests : IAsyncLifetime
{
    private const int PageSize = 500;
    private const int FilesPerFolder = 200;

    private readonly ITestOutputHelper _output;
    private readonly int _files;
    private readonly string _root;
    private readonly bool _keep;
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private CategoryInfo _quality = null!;
    private int _familieTagId;

    public GallerySearchSortMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
        _files = int.TryParse(Environment.GetEnvironmentVariable("LUMORA_SEARCHSORT_FILES"), out var n) && n > 0 ? n : 3_000;
        var dir = Environment.GetEnvironmentVariable("LUMORA_SEARCHSORT_DIR");
        _keep = !string.IsNullOrWhiteSpace(dir);
        _root = _keep ? dir! : Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        if (_keep && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        Directory.CreateDirectory(_root);
        var paths = new AppDataPaths(_root);
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        var build = Stopwatch.StartNew();
        await using (var ctx = _factory.Create())
        {
            var library = new Library { Name = "Messung", RootPath = @"C:\Fotos", CreatedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            _libraryId = library.Id;
        }

        using (var connection = _factory.OpenSqliteConnection())
        {
            InsertCatalog(connection);
        }

        var messenger = new WeakReferenceMessenger();
        var tags = new TagService(_factory, NullLogger<TagService>.Instance, messenger);
        var background = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            background.Add((await tags.CreateAsync($"Motiv {i:D2}")).Id);
        }

        var urlaub = (await tags.CreateAsync("Urlaub")).Id;
        var familie = (await tags.CreateAsync("Familie")).Id;
        _familieTagId = familie;

        using var categories = new CategoryService(_factory, NullLogger<CategoryService>.Instance, messenger);
        _quality = await categories.CreateAsync("Qualität", ["Q1", "Q2", "Q3", "Q4", "Q5"]);
        var projekt = await categories.CreateAsync("Projekt", Enumerable.Range(1, 12).Select(i => $"Bau {i:D2}").ToList());
        var kunde = await categories.CreateAsync("Kunde", ["Meier", "Huber", "Berger"]);

        using (var connection = _factory.OpenSqliteConnection())
        using (var tx = connection.BeginTransaction())
        {
            // Zwei Hintergrund-Tags je Datei (quer verteilt), dazu die beiden Such-Tags.
            Exec(connection, tx, $"INSERT INTO FileTags(FileId, TagId) SELECT Id, {Pick(background, "Id * 7")} FROM Files;");
            Exec(connection, tx, $"INSERT OR IGNORE INTO FileTags(FileId, TagId) SELECT Id, {Pick(background, "Id * 13 + 3")} FROM Files;");
            Exec(connection, tx, $"INSERT INTO FileTags(FileId, TagId) SELECT Id, {urlaub} FROM Files WHERE Id % 100 = 0;");
            Exec(connection, tx, $"INSERT INTO FileTags(FileId, TagId) SELECT Id, {familie} FROM Files WHERE Id % 5 = 1;");

            AssignCategory(connection, tx, _quality, "Id % 5 <> 0", "Id * 31");
            AssignCategory(connection, tx, projekt, "Id % 2 = 0", "Id * 17 + 5");
            AssignCategory(connection, tx, kunde, "Id % 50 = 7", "Id");

            // Datei-Filter (0.2.43, ADR-0041): jedes 4. ein Video, jedes 10. Favorit, Bewertung 1–5 (jedes 6. ohne).
            Exec(connection, tx, "UPDATE Files SET MediaType = CASE WHEN Id % 4 = 0 THEN 1 ELSE 0 END, IsFavorite = CASE WHEN Id % 10 = 0 THEN 1 ELSE 0 END, Rating = CASE WHEN Id % 6 = 0 THEN NULL ELSE Id % 6 END;");
            tx.Commit();

            // Kein ANALYZE: die App führt keins aus, der Planer arbeitet wie beim Benutzer ohne Statistiken.
        }

        build.Stop();
        _output.WriteLine($"Katalog {_files:N0} Dateien aufgebaut in {build.Elapsed.TotalSeconds:N1} s ({_root})");

        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source?.Dispose();
        SqliteConnection.ClearAllPools();
        if (!_keep)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Messung_Suche_und_Kategorie_Sortierung()
    {
        var deepPage = Math.Max(0, Math.Min(800, (_files - 1) / PageSize));
        var midPage = Math.Max(0, (_files / 2) / PageSize);
        var lines = new List<string>
        {
            $"Messung {_files:N0} Dateien, Seitengröße {PageSize}, Median aus 3 Läufen nach 1 Aufwärmlauf (warmer Cache)",
            "| Fall | Treffer | Zählen (ms) | Seite nach Änderung (ms) | Seite wiederholt (ms) |",
            "|---|---:|---:|---:|---:|",
        };

        // Fremder Commit vor jedem Lauf (wie Neuladen nach einer Änderung): macht zwischengespeicherte Zählungen der
        // Kategorie-Sortierung ungültig (PRAGMA data_version). „Seite wiederholt“ = Blättern ohne Änderung.
        using var writer = _factory.OpenSqliteConnection();

        async Task Measure(string label, GalleryQuery query, int page, int? expectedCount = null)
        {
            var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
            if (expectedCount is { } expected)
            {
                count.Should().Be(expected, label);
            }

            var rows = await _source.LoadPageAsync(query, page, PageSize, count, CancellationToken.None);
            rows.Should().HaveCount((int)Math.Clamp(count - (long)page * PageSize, 0, PageSize), label);

            var countMs = new List<double>();
            var pageMs = new List<double>();
            var repeatMs = new List<double>();
            for (var run = 0; run < 3; run++)
            {
                // Echte Änderung nötig: ein UPDATE mit gleichem Wert schreibt keine Seite und ändert data_version nicht.
                Exec(writer, null, "UPDATE Libraries SET LastOpenedAt = COALESCE(LastOpenedAt, 0) + 1;");
                var sw = Stopwatch.StartNew();
                await _source.GetTotalCountAsync(query, CancellationToken.None);
                countMs.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                await _source.LoadPageAsync(query, page, PageSize, count, CancellationToken.None);
                pageMs.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                var again = await _source.LoadPageAsync(query, page, PageSize, count, CancellationToken.None);
                repeatMs.Add(sw.Elapsed.TotalMilliseconds);
                again.Select(r => r.Id).Should().Equal(rows.Select(r => r.Id), label + " (wiederholt gleich)");
            }

            lines.Add($"| {label} | {count:N0} | {Median(countMs):N0} | {Median(pageMs):N0} | {Median(repeatMs):N0} |");
        }

        await Measure("Tag-Suche „urlaub“ (selten), Seite 1", Search("urlaub"), 0, _files / 100);
        await Measure("Tag-Suche „familie“ (häufig), Seite 1", Search("familie"), 0);
        var sort = new GalleryQuery(_libraryId, GalleryFilterState.None, null, CategorySort: new GalleryCategorySort(_quality.Id, false));
        await Measure("Kategorie-Sortierung ↑, Seite 1", sort, 0, _files);
        await Measure($"Kategorie-Sortierung ↑, Seite {midPage + 1} (Mitte)", sort, midPage, _files);
        await Measure($"Kategorie-Sortierung ↑, Seite {deepPage + 1} (tief)", sort, deepPage, _files);
        var sortDesc = sort with { CategorySort = new GalleryCategorySort(_quality.Id, true) };
        await Measure($"Kategorie-Sortierung ↓, Seite {deepPage + 1} (tief)", sortDesc, deepPage, _files);
        await Measure("Kategorie-Namen-Suche „projekt“ (50 %), Seite 1", Search("projekt"), 0);
        await Measure("Kategorie-Namen-Suche „kunde“ (2 %), Seite 1", Search("kunde"), 0);

        // Datei-Filter unabhängig kombiniert (0.2.43): Einzel-Filter wie vorher, dazu UND-Kombinationen.
        var images = new GalleryQuery(_libraryId, new GalleryFilterState(GalleryMediaKind.Images), null);
        await Measure("Datei-Filter Bilder (75 %), Seite 1", images, 0);
        await Measure("Datei-Filter Favoriten (10 %), Seite 1", images with { Filter = new GalleryFilterState(FavoritesOnly: true) }, 0);
        await Measure("Bilder + Favoriten + ab 3 Sternen, Seite 1", images with { Filter = new GalleryFilterState(GalleryMediaKind.Images, true, MinRating: 3) }, 0);
        await Measure("Bilder + ab 3 Sternen + Tag „Familie“, Seite 1", images with { Filter = new GalleryFilterState(GalleryMediaKind.Images, MinRating: 3), TagIds = [_familieTagId] }, 0);

        // Vergleich (nicht Teil der Änderung): Grundsortierungen mit Lookup je Datei.
        var all = new GalleryQuery(_libraryId, GalleryFilterState.None, null);
        await Measure($"Vergleich: Datum neu → alt, Seite {midPage + 1}", all, midPage, _files);
        await Measure($"Vergleich: Name A → Z, Seite {midPage + 1}", all with { Sort = GallerySort.NameAsc }, midPage, _files);

        foreach (var line in lines)
        {
            _output.WriteLine(line);
        }
    }

    private GalleryQuery Search(string term) => new(_libraryId, GalleryFilterState.None, term);

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private void InsertCatalog(SqliteConnection connection)
    {
        var folderCount = (_files + FilesPerFolder - 1) / FilesPerFolder;
        var folders = new List<BulkScanWriter.FolderRow>(folderCount);
        for (var i = 0; i < folderCount; i++)
        {
            folders.Add(new BulkScanWriter.FolderRow(i + 1, _libraryId, $@"C:\Fotos\{2000 + i % 25}\Ordner {i:D5}", null, $"Ordner {i:D5}", 0));
        }

        var baseUnix = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var files = Enumerable.Range(0, _files).Select(i => new BulkScanWriter.FileRow(
            i + 1, _libraryId, i / FilesPerFolder + 1, $"IMG_{i:D7}.jpg", ".jpg", 1000 + i, 0,
            baseUnix + (i * 7919L % _files) * 60, 0, null, null, null, null, false, null, false));

        var writer = new BulkScanWriter();
        writer.InsertFolders(connection, folders);
        writer.InsertFiles(connection, files);
        new FtsIndexer().PopulateAll(connection);
    }

    /// <summary>SQL-Ausdruck, der je Datei eines der Elemente quer zur Id wählt.</summary>
    private static string Pick(IReadOnlyList<int> ids, string expression)
    {
        var cases = string.Join(" ", ids.Select((id, index) => $"WHEN {index} THEN {id}"));
        return $"CASE ({expression}) % {ids.Count} {cases} END";
    }

    private static void AssignCategory(SqliteConnection connection, SqliteTransaction tx, CategoryInfo category, string where, string spread)
        => Exec(connection, tx,
            $"INSERT INTO FileCategoryValues(FileId, CategoryId, ValueId) SELECT Id, {category.Id}, " +
            $"{Pick(category.Values.Select(v => v.Id).ToList(), spread)} FROM Files WHERE {where};");

    private static void Exec(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

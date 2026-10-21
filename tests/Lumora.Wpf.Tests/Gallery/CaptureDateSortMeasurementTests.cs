using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data.RawSql;
using Lumora.Domain.Entities;
using Lumora.Gallery.Data;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Tags;
using Xunit.Abstractions;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>
/// Messung Sortierung nach Aufnahmedatum (ADR-0046, Spike 12) an einem synthetischen Katalog wie
/// <see cref="GallerySearchSortMeasurementTests"/>. Größe über <c>LUMORA_CAPTURESORT_FILES</c> (Standard 3 000 — im normalen
/// Testlauf nur Funktionsprobe); die Werte in <c>docs/spikes/12-aufnahmedatum.md</c> stammen aus Läufen mit 500 000.
/// 70 % der Dateien haben ein Aufnahmedatum (quer zu Id und Änderungsdatum), der Rest sortiert nach Änderungsdatum.
/// Jede Abfrage: kein Vollscan über Files, Reihenfolge = Sortierschlüssel.
/// </summary>
public sealed class CaptureDateSortMeasurementTests : IAsyncLifetime
{
    private const int PageSize = 500;
    private const int FilesPerFolder = 200;

    private readonly ITestOutputHelper _output;
    private readonly int _files;
    private readonly string _root;
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private int _familieTagId;

    public CaptureDateSortMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
        _files = int.TryParse(Environment.GetEnvironmentVariable("LUMORA_CAPTURESORT_FILES"), out var n) && n > 0 ? n : 3_000;
        _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new LibraryContextFactory(new AppDataPaths(_root), new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await using (var ctx = _factory.Create())
        {
            var library = new Library { Name = "Messung", RootPath = @"C:\Fotos", CreatedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            _libraryId = library.Id;
        }

        var build = Stopwatch.StartNew();
        using (var connection = _factory.OpenSqliteConnection())
        {
            var folderCount = (_files + FilesPerFolder - 1) / FilesPerFolder;
            var folders = Enumerable.Range(0, folderCount)
                .Select(i => new BulkScanWriter.FolderRow(i + 1, _libraryId, $@"C:\Fotos\{2000 + (i % 25)}\Ordner {i:D5}", null, $"Ordner {i:D5}", 0))
                .ToList();
            var baseUnix = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var files = Enumerable.Range(0, _files).Select(i => new BulkScanWriter.FileRow(
                i + 1, _libraryId, (i / FilesPerFolder) + 1, $"IMG_{i:D7}.jpg", ".jpg", 1000 + i, 0,
                baseUnix + (i * 7919L % _files * 60), i % 4 == 0 ? 1 : 0, null, null, null, null, false, null, false));
            var writer = new BulkScanWriter();
            writer.InsertFolders(connection, folders);
            writer.InsertFiles(connection, files);
            new FtsIndexer().PopulateAll(connection);
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            // 70 % mit Aufnahmedatum zwischen 1980 und 2015, quer verteilt; der Rest bleibt „unbekannt“.
            cmd.CommandText = """
                UPDATE Files SET CapturedAt = CASE WHEN Id % 10 < 7 THEN 315532800 + (Id * 104729 % 1100000000) END,
                                 CapturedAtSource = CASE WHEN Id % 10 < 7 THEN 1 ELSE 0 END;
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        var tags = new TagService(_factory, NullLogger<TagService>.Instance, new WeakReferenceMessenger());
        _familieTagId = (await tags.CreateAsync("Familie")).Id;
        using (var connection = _factory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO FileTags(FileId, TagId) SELECT Id, {_familieTagId} FROM Files WHERE Id % 5 = 1;";
            cmd.ExecuteNonQuery();
        }

        _output.WriteLine($"Katalog {_files:N0} Dateien aufgebaut in {build.Elapsed.TotalSeconds:N1} s");
        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    public Task DisposeAsync()
    {
        _source?.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Messung_Sortierung_nach_Aufnahmedatum()
    {
        var deepPage = Math.Max(0, (_files - 1) / PageSize);
        var midPage = Math.Max(0, _files / 2 / PageSize);
        var lines = new List<string>
        {
            $"Messung {_files:N0} Dateien, Seitengröße {PageSize}, Median aus 3 Läufen nach 1 Aufwärmlauf",
            "| Fall | Treffer | Zählen (ms) | Seite (ms) |",
            "|---|---:|---:|---:|",
        };
        var plans = new List<string> { string.Empty, "Abfragepläne (Seite):" };

        async Task Measure(string label, GalleryQuery query, int page)
        {
            foreach (var count in new[] { false, true })
            {
                var plan = await _source.ExplainAsync(query, count, CancellationToken.None);
                plan.Should().NotContain(l => Regex.IsMatch(l, "^SCAN f( |$)"), $"{label}: kein Vollscan (Plan: {string.Join(" / ", plan)})");
                if (!count)
                {
                    plans.Add($"- {label}: {string.Join(" / ", plan)}");
                }
            }

            var total = await _source.GetTotalCountAsync(query, CancellationToken.None);
            var rows = await _source.LoadPageAsync(query, page, PageSize, total, CancellationToken.None);
            var keys = rows.Select(r => r.CaptureDate?.Value is { } v ? new DateTimeOffset(v.Utc).ToUnixTimeSeconds() : r.ModifiedAtUnix).ToList();
            if (query.Sort == GallerySort.CapturedDesc)
            {
                keys.Should().BeInDescendingOrder(label);
            }
            else if (query.Sort == GallerySort.CapturedAsc)
            {
                keys.Should().BeInAscendingOrder(label);
            }

            var countMs = new List<double>();
            var pageMs = new List<double>();
            for (var run = 0; run < 3; run++)
            {
                var sw = Stopwatch.StartNew();
                await _source.GetTotalCountAsync(query, CancellationToken.None);
                countMs.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                await _source.LoadPageAsync(query, page, PageSize, total, CancellationToken.None);
                pageMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            lines.Add($"| {label} | {total:N0} | {Median(countMs):N0} | {Median(pageMs):N0} |");
        }

        var all = new GalleryQuery(_libraryId, GalleryFilterState.None, null, Sort: GallerySort.CapturedDesc);
        await Measure("Aufnahme neu → alt, Seite 1", all, 0);
        await Measure($"Aufnahme neu → alt, Seite {midPage + 1} (Mitte)", all, midPage);
        await Measure($"Aufnahme neu → alt, Seite {deepPage + 1} (letzte)", all, deepPage);
        await Measure($"Aufnahme alt → neu, Seite {midPage + 1} (Mitte)", all with { Sort = GallerySort.CapturedAsc }, midPage);
        await Measure("Aufnahme + Bilder (75 %), Seite 1", all with { Filter = new GalleryFilterState(GalleryMediaKind.Images) }, 0);
        await Measure("Aufnahme + Videos (25 %), Seite 1", all with { Filter = new GalleryFilterState(GalleryMediaKind.Videos) }, 0);
        await Measure("Aufnahme + Tag „Familie“ (20 %), Seite 1", all with { TagIds = [_familieTagId] }, 0);
        await Measure("Aufnahme + Ordner (mit Unterordnern), Seite 1", all with { FolderId = 1 }, 0);
        await Measure("Aufnahme + Suche „img“ (alle Treffer), Seite 1", all with { SearchTerm = "img" }, 0);
        await Measure($"Vergleich: Änderungsdatum neu → alt, Seite {midPage + 1}", all with { Sort = GallerySort.ModifiedDesc }, midPage);
        await Measure($"Vergleich: Änderungsdatum alt → neu, Seite {midPage + 1}", all with { Sort = GallerySort.ModifiedAsc }, midPage);
        await Measure("Vergleich: Änderungsdatum + Ordner, Seite 1", all with { Sort = GallerySort.ModifiedDesc, FolderId = 1 }, 0);
        await Measure("Vergleich: Änderungsdatum + Tag „Familie“, Seite 1", all with { Sort = GallerySort.ModifiedDesc, TagIds = [_familieTagId] }, 0);

        foreach (var line in lines.Concat(plans))
        {
            _output.WriteLine(line);
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}

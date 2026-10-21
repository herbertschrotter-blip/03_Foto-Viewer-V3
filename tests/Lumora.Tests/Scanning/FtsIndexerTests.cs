using Lumora.Data.RawSql;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

/// <summary>
/// Regression: FilesFts ist eine External-Content-Tabelle — ein SELECT ohne MATCH liest aus Files,
/// nicht aus dem Index. Der Populate-Pfad muss den Index wirklich füllen (MATCH liefert Treffer).
/// </summary>
public sealed class FtsIndexerTests
{
    [Fact]
    public async Task Scan_fuellt_den_FTS_Index_so_dass_MATCH_Treffer_liefert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "bravo.mp4"));

        var result = await fx.ScanAsync(library.Id);

        result.FilesInserted.Should().Be(2);
        result.FtsRowsInserted.Should().Be(2);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;
        var bravo = (await fx.FindFileAsync("bravo.mp4"))!;
        fx.FtsRowIds("alpha").Should().Equal(alpha.Id);
        fx.FtsRowIds("mp4").Should().Equal(bravo.Id);
        fx.FtsRowIds("charlie").Should().BeEmpty();
    }

    [Fact]
    public async Task PopulateAll_ist_idempotent_und_zaehlt_indexierte_Dokumente()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("alpha.jpg");
        await fx.ScanAsync(library.Id);
        var indexer = new FtsIndexer();

        using var connection = fx.ContextFactory.OpenSqliteConnection();
        var report = indexer.PopulateAll(connection);
        var again = indexer.PopulateAll(connection);

        report.RowsInserted.Should().Be(1);
        again.RowsInserted.Should().Be(1);
        fx.FtsRowIds("alpha").Should().HaveCount(1, "Mehrfach-Populate darf keine doppelten Index-Einträge erzeugen");
    }
}

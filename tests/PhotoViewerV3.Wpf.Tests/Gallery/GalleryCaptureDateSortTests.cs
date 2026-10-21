using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Wpf.Tests.ArchiveTarget;

namespace PhotoViewerV3.Wpf.Tests.Gallery;

/// <summary>
/// Sortierung nach Aufnahmedatum (ADR-0046): Schlüssel COALESCE(CapturedAt, ModifiedAtUtc) — Dateien ohne Aufnahmedatum
/// stehen mit ihrem Änderungsdatum dazwischen; beide Richtungen, Blättern vom Ende (Reverse-Trick), Kombination mit
/// Filtern ohne Vollscan, Aufnahmedatum im DTO. Dazu Sortierfeld-Liste und Listenspalte.
/// </summary>
public sealed class GalleryCaptureDateSortTests(GalleryCaptureDateSortTests.Catalog catalog) : IClassFixture<GalleryCaptureDateSortTests.Catalog>
{
    private readonly GalleryPageSource _source = catalog.Source;
    private readonly int _libraryId = catalog.LibraryId;

    /// <summary>10 Dateien f0…f9, Änderungsdatum 2024-01-01 + i Stunden; f3 aufgenommen 1980, f7 1990 (manuell, Jahr), f5 unbekannt.</summary>
    public sealed class Catalog : IAsyncLifetime
    {
        private ArchiveTestDb _db = null!;

        public int LibraryId => _db.LibraryId;

        public GalleryPageSource Source { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _db = await ArchiveTestDb.CreateAsync();
            var folder = await _db.AddFolderAsync(_db.ArchiveRootId, _db.ArchiveDir);
            var baseUnix = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            using var connection = _db.Factory.OpenSqliteConnection();
            for (var i = 0; i < 10; i++)
            {
                var file = await _db.AddFileAsync(folder, $"f{i}.jpg", i % 4 == 1 ? MediaType.Video : MediaType.Image);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE Files SET ModifiedAtUtc = $m, CapturedAt = $c, CapturedAtSource = $s, CapturedAtPrecision = $p, CapturedAtOffset = $o WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$m", baseUnix + (i * 3600));
                var (captured, source, precision) = i switch
                {
                    3 => ((object)new DateTimeOffset(1980, 5, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), (object)(int)CaptureDateSource.Exif, 0),
                    7 => (new DateTimeOffset(1990, 7, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), (int)CaptureDateSource.Manual, (int)CaptureDatePrecision.Year),
                    5 => (DBNull.Value, (int)CaptureDateSource.Unknown, 0),
                    _ => (DBNull.Value, DBNull.Value, 0),
                };
                cmd.Parameters.AddWithValue("$c", captured);
                cmd.Parameters.AddWithValue("$s", source);
                cmd.Parameters.AddWithValue("$p", precision);
                cmd.Parameters.AddWithValue("$o", captured is DBNull ? DBNull.Value : 120);
                cmd.Parameters.AddWithValue("$id", file.Id);
                cmd.ExecuteNonQuery();
            }

            Source = new GalleryPageSource(_db.Factory);
            Source.Open();
        }

        public Task DisposeAsync()
        {
            Source.Dispose();
            SqliteConnection.ClearAllPools();
            _db.Dispose();
            return Task.CompletedTask;
        }
    }

    private static readonly string[] Ascending = ["f3", "f7", "f0", "f1", "f2", "f4", "f5", "f6", "f8", "f9"];

    private async Task<List<string>> NamesAsync(GalleryQuery query, int pageSize = 100)
    {
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var names = new List<string>();
        for (var page = 0; page * pageSize < count; page++)
        {
            var rows = await _source.LoadPageAsync(query, page, pageSize, count, CancellationToken.None);
            names.AddRange(rows.Select(r => Path.GetFileNameWithoutExtension(r.Name)));
        }

        return names;
    }

    [Fact]
    public async Task Aufnahmedatum_alt_nach_neu_ohne_Datum_nach_Aenderungsdatum()
        => (await NamesAsync(new GalleryQuery(_libraryId, GalleryFilterState.None, null, Sort: GallerySort.CapturedAsc))).Should().Equal(Ascending);

    [Fact]
    public async Task Aufnahmedatum_neu_nach_alt_auch_seitenweise_vom_Ende()
    {
        var query = new GalleryQuery(_libraryId, GalleryFilterState.None, null, Sort: GallerySort.CapturedDesc);
        (await NamesAsync(query)).Should().Equal(Ascending.Reverse());
        (await NamesAsync(query, pageSize: 3)).Should().Equal(Ascending.Reverse(), "hintere Seiten laufen über den Reverse-Trick");
    }

    [Fact]
    public async Task Kombiniert_mit_Filter_Bilder()
    {
        var query = new GalleryQuery(_libraryId, new GalleryFilterState(GalleryMediaKind.Images), null, Sort: GallerySort.CapturedAsc);
        (await NamesAsync(query)).Should().Equal(Ascending.Where(n => int.Parse(n[1..], System.Globalization.CultureInfo.InvariantCulture) % 4 != 1));
    }

    [Fact]
    public async Task DTO_traegt_das_Aufnahmedatum()
    {
        var count = await _source.GetTotalCountAsync(new GalleryQuery(_libraryId, GalleryFilterState.None, null), CancellationToken.None);
        var rows = await _source.LoadPageAsync(new GalleryQuery(_libraryId, GalleryFilterState.None, null, Sort: GallerySort.CapturedAsc), 0, 100, count, CancellationToken.None);

        rows[0].CaptureDate!.Text.Should().Be("01.05.1980 12:00 (EXIF)");
        rows[1].CaptureDate!.Text.Should().Be("ca. 1990 (manuell)");
        rows.Single(r => r.Name == "f5.jpg").CaptureDate!.Text.Should().StartWith("unbekannt");
        rows.Single(r => r.Name == "f0.jpg").CaptureDate!.Source.Should().BeNull();

        var item = new GalleryItem(rows[1]);
        item.CaptureDateText.Should().Be("ca. 1990");
        item.CaptureDateToolTip.Should().Be("Aufgenommen: ca. 1990 (manuell)");
        new GalleryItem(rows.Single(r => r.Name == "f0.jpg")).CaptureDateToolTip.Should().Be("Aufgenommen: noch nicht gelesen (sortiert nach Änderungsdatum)");
    }

    public static IEnumerable<object[]> FilterCombinations()
    {
        foreach (var sort in new[] { GallerySort.CapturedDesc, GallerySort.CapturedAsc })
        {
            foreach (var media in Enum.GetValues<GalleryMediaKind>())
            {
                foreach (var favorites in new[] { false, true })
                {
                    foreach (var rating in new[] { 0, 3 })
                    {
                        yield return [sort, media, favorites, rating];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(FilterCombinations))]
    public async Task Abfrageplan_ohne_Vollscan(GallerySort sort, GalleryMediaKind media, bool favorites, int rating)
    {
        var query = new GalleryQuery(_libraryId, new GalleryFilterState(media, favorites, GalleryLocation.Anywhere, rating), null, Sort: sort);
        foreach (var count in new[] { false, true })
        {
            var plan = await _source.ExplainAsync(query, count, CancellationToken.None);
            var joined = string.Join(" / ", plan);
            plan.Should().NotContain(l => Regex.IsMatch(l, "^SCAN f( |$)"), $"kein Durchlauf aller Dateien (Plan: {joined})");
        }
    }

    [Fact]
    public async Task Ohne_Filter_liefert_der_Ausdrucks_Index_die_Reihenfolge()
    {
        var plan = await _source.ExplainAsync(new GalleryQuery(_libraryId, GalleryFilterState.None, null, Sort: GallerySort.CapturedDesc), false, CancellationToken.None);
        plan.Should().Contain(l => l.Contains("IX_Files_Library_Captured", StringComparison.Ordinal), string.Join(" / ", plan));
        plan.Should().NotContain(l => l.Contains("TEMP B-TREE", StringComparison.Ordinal), "keine Sortierung im Speicher");
    }

    [Fact]
    public void Sortierfeld_Aufnahmedatum_und_Listenspalte()
    {
        GalleryViewModel.FieldOf(GallerySort.CapturedAsc).Should().Be(GallerySortField.Captured);
        GalleryViewModel.SortFor(GallerySortField.Captured, descending: true).Should().Be(GallerySort.CapturedDesc);
        GalleryViewModel.DefaultDescending(GallerySortField.Captured).Should().BeTrue("Datum: neueste zuerst");
        GalleryListColumns.ForSort(GallerySort.CapturedDesc).Should().Be((GalleryListColumnKey.Captured, true));
        GalleryListColumns.NextSort(GalleryListColumnKey.Captured, GallerySort.ModifiedDesc).Should().Be(GallerySort.CapturedAsc);
        GalleryListColumns.Get(GalleryListColumnKey.Captured).Header.Should().Be("Aufgenommen");
    }
}

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Services.Categories;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;

namespace PhotoViewerV3.Wpf.Tests.Categories;

/// <summary>
/// Kategorien-Filter der Galerie-SQL gegen eine echte, migrierte DB (ADR-0027). Fünf Dateien a–e:
/// XX: a = N, b = N, c = NN, d/e ohne Wert · Projekt: a = Nord, c = Süd, sonst ohne Wert.
/// </summary>
public sealed class GalleryPageSourceCategoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> _ids = new();
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;
    private CategoryInfo _xx = null!;
    private CategoryInfo _projekt = null!;
    private CategoryService _service = null!;

    public async Task InitializeAsync()
    {
        var paths = new AppDataPaths(_root);
        Directory.CreateDirectory(_root);
        _factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using (var ctx = _factory.Create())
        {
            var library = new Library { Name = "T", RootPath = @"C:\Fotos", CreatedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            _libraryId = library.Id;
            var folder = new Folder { LibraryId = library.Id, Path = @"C:\Fotos", Name = "Fotos", ScannedAt = DateTime.UtcNow };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();
            foreach (var name in new[] { "a", "b", "c", "d", "e" })
            {
                var file = new MediaFile
                {
                    LibraryId = library.Id,
                    FolderId = folder.Id,
                    Name = $"IMG_{name}.jpg",
                    Extension = ".jpg",
                    Size = 1000,
                    CreatedAtUtc = DateTime.UtcNow,
                    ModifiedAtUtc = DateTime.UtcNow,
                    MediaType = MediaType.Image,
                };
                ctx.Files.Add(file);
                await ctx.SaveChangesAsync();
                _ids[name] = file.Id;
            }
        }

        using (var connection = _factory.OpenSqliteConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files;";
            cmd.ExecuteNonQuery();
        }

        var categories = new CategoryService(_factory, NullLogger<CategoryService>.Instance, new WeakReferenceMessenger());
        _service = categories;
        _xx = await categories.CreateAsync("XX", ["N", "NN"]);
        _projekt = await categories.CreateAsync("Projekt", ["Nord", "Süd"]);
        await categories.SetValueAsync([_ids["a"], _ids["b"]], _xx.Id, N);
        await categories.SetValueAsync([_ids["c"]], _xx.Id, NN);
        await categories.SetValueAsync([_ids["a"]], _projekt.Id, Nord);
        await categories.SetValueAsync([_ids["c"]], _projekt.Id, Sued);

        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

    private int N => _xx.Values[0].Id;

    private int NN => _xx.Values[1].Id;

    private int Nord => _projekt.Values[0].Id;

    private int Sued => _projekt.Values[1].Id;

    public Task DisposeAsync()
    {
        _source.Dispose();
        _service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public async Task Ein_Wert_filtert_auf_genau_diese_Dateien()
        => (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [N], false))).Should().Equal("IMG_a.jpg", "IMG_b.jpg");

    [Fact]
    public async Task Mehrere_Werte_einer_Kategorie_sind_ODER()
        => (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [N, NN], false))).Should().Equal("IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg");

    [Fact]
    public async Task Ohne_Wert_allein_und_zusammen_mit_einem_Wert()
    {
        (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [], true))).Should().Equal("IMG_d.jpg", "IMG_e.jpg");
        (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [NN], true))).Should().Equal("IMG_c.jpg", "IMG_d.jpg", "IMG_e.jpg");
    }

    [Fact]
    public async Task Mehrere_Kategorien_sind_UND()
    {
        (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [N], false), new GalleryCategoryFilter(_projekt.Id, [Nord], false)))
            .Should().Equal("IMG_a.jpg");
        (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [N], false), new GalleryCategoryFilter(_projekt.Id, [], true)))
            .Should().Equal("IMG_b.jpg");
    }

    [Fact]
    public async Task Leerer_Filter_wirkt_nicht_und_Suche_und_Kategorien_Filter_wirken_zusammen()
    {
        (await NamesAsync(new GalleryCategoryFilter(_xx.Id, [], false))).Should().HaveCount(5);
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, "IMG_c", Sort: GallerySort.NameAsc,
            CategoryFilters: [new GalleryCategoryFilter(_projekt.Id, [Sued], false)]);
        (await _source.GetTotalCountAsync(query, CancellationToken.None)).Should().Be(1, "Suche und Kategorien-Filter wirken zusammen");
    }

    // ---- Suche nach Kategorie-Werten (0.2.23) ---------------------------------------------------

    [Theory]
    [InlineData("nord", new[] { "IMG_a.jpg" })]
    [InlineData("NN", new[] { "IMG_c.jpg" })]
    [InlineData("Sü", new[] { "IMG_c.jpg" })]
    [InlineData("n", new[] { "IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg" })]
    public async Task Suche_findet_Dateien_ueber_ihren_Kategorie_Wert(string term, string[] expected)
        => (await SearchAsync(term)).Should().Equal(expected);

    [Fact]
    public async Task Suche_nach_Wert_und_Dateiname_wirken_zusammen_und_mit_Kategorien_Filter()
    {
        (await SearchAsync("IMG_d")).Should().Equal("IMG_d.jpg");
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, "n", Sort: GallerySort.NameAsc,
            CategoryFilters: [new GalleryCategoryFilter(_projekt.Id, [], true)]);
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Select(r => r.Name).Should().Equal(["IMG_b.jpg"], "„n“ trifft a, b, c; nur b hat keinen Projekt-Wert");
    }

    [Fact]
    public async Task Suche_folgt_Umbenennen_und_Loeschen_eines_Werts_ohne_Index_Pflege()
    {
        await _service.RenameValueAsync(Nord, "Westen");
        (await SearchAsync("westen")).Should().Equal("IMG_a.jpg");
        (await SearchAsync("nord")).Should().BeEmpty("der alte Name steht nirgends mehr");

        await _service.DeleteValueAsync(Nord);
        (await SearchAsync("westen")).Should().BeEmpty();

        await _service.SetValueAsync([_ids["e"]], _xx.Id, NN);
        (await SearchAsync("NN")).Should().Equal(["IMG_c.jpg", "IMG_e.jpg"], "neue Zuweisung wird sofort gefunden");
    }

    [Fact]
    public async Task Suche_nach_Werten_laeuft_ueber_Indizes_statt_ueber_alle_Dateien()
    {
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, "nord");
        foreach (var count in new[] { true, false })
        {
            var plan = await _source.ExplainAsync(query, count, CancellationToken.None);
            plan.Should().Contain(l => l.Contains("IX_FileCategoryValues_Value", StringComparison.Ordinal),
                "Kategorie-Treffer über den Wert-Index, nicht über alle Zuordnungen");
            var joined = string.Join(" | ", plan);
            plan.Should().NotContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^SCAN fcv( |$)"), $"kein Durchlauf aller Zuordnungen (Plan: {joined})");
            plan.Should().NotContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^SCAN f( |$)"),
                $"die Treffer treiben die Abfrage, kein Durchlauf aller Dateien (Plan: {joined})");
        }
    }

    // ---- Serverseitige Sortierung nach Kategorie (0.2.23) ---------------------------------------

    [Fact]
    public async Task Sortierung_nach_Kategorie_folgt_der_Werte_Reihenfolge_ohne_Wert_zuletzt()
    {
        // Gleicher Wert → Datum neu → alt, dann höhere Id zuerst (die Testdateien haben dasselbe Änderungsdatum).
        (await SortedAsync(new GalleryCategorySort(_xx.Id, false))).Should().Equal("IMG_b.jpg", "IMG_a.jpg", "IMG_c.jpg", "IMG_e.jpg", "IMG_d.jpg");
        (await SortedAsync(new GalleryCategorySort(_xx.Id, true))).Should().Equal(["IMG_c.jpg", "IMG_b.jpg", "IMG_a.jpg", "IMG_e.jpg", "IMG_d.jpg"],
            "absteigend kehrt die Werte um, gleiche Werte bleiben neu → alt, „ohne Wert“ bleibt am Ende");
        (await SortedAsync(new GalleryCategorySort(_projekt.Id, true))).Should().Equal("IMG_c.jpg", "IMG_a.jpg", "IMG_e.jpg", "IMG_d.jpg", "IMG_b.jpg");
    }

    [Fact]
    public async Task Sortierung_nutzt_die_Reihenfolge_aus_der_Verwaltung()
    {
        await _service.ReorderValuesAsync(_xx.Id, [NN, N]);
        (await SortedAsync(new GalleryCategorySort(_xx.Id, false))).Should().Equal("IMG_c.jpg", "IMG_b.jpg", "IMG_a.jpg", "IMG_e.jpg", "IMG_d.jpg");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sortierung_ueber_Seiten_auch_beim_Lesen_vom_Ende_identisch(bool descending)
    {
        var sort = new GalleryCategorySort(_xx.Id, descending);
        var expected = await SortedAsync(sort);
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, null, CategorySort: sort);
        var paged = new List<string>();
        for (var page = 0; page < 3; page++)
        {
            // Seite 2 liegt näher am Ende → Reverse-Trick (umgekehrt sortieren, clientseitig drehen)
            paged.AddRange((await _source.LoadPageAsync(query, page, 2, expected.Count, CancellationToken.None)).Select(r => r.Name));
        }

        paged.Should().Equal(expected);
    }

    [Fact]
    public async Task Sortierung_wirkt_zusammen_mit_Filter_und_Suche_und_ersetzt_die_Grundsortierung()
    {
        var filtered = new GalleryQuery(_libraryId, GalleryFilter.All, null, Sort: GallerySort.NameDesc,
            CategoryFilters: [new GalleryCategoryFilter(_projekt.Id, [], true)], CategorySort: new GalleryCategorySort(_xx.Id, true));
        (await LoadAllAsync(filtered)).Should().Equal(["IMG_b.jpg", "IMG_e.jpg", "IMG_d.jpg"], "b hat N, d/e ohne Wert; Name Z → A gilt nicht");

        var searched = new GalleryQuery(_libraryId, GalleryFilter.All, "n", CategorySort: new GalleryCategorySort(_projekt.Id, false));
        (await LoadAllAsync(searched)).Should().Equal("IMG_a.jpg", "IMG_c.jpg", "IMG_b.jpg");
    }

    [Fact]
    public async Task Sortierung_liest_segmentweise_ueber_Indizes_statt_alle_Dateien_zu_sortieren()
    {
        // 0.2.30 (ADR-0031): Segment-Zählung über den abdeckenden Kategorie-Index, Seiten je Segment in der Reihenfolge
        // von IX_Files_Library_Modified — kein Schlüssel-Lookup je Datei und keine Sortierung der ganzen Bibliothek mehr.
        var plan = await _source.ExplainAsync(new GalleryQuery(_libraryId, GalleryFilter.All, null, CategorySort: new GalleryCategorySort(_xx.Id, false)), false, CancellationToken.None);
        var joined = string.Join(" | ", plan);
        plan.Should().Contain(l => l.StartsWith("SEARCH scv USING COVERING INDEX IX_FileCategoryValues_Category_Value_File (CategoryId=?)", StringComparison.Ordinal),
            $"Zählung je Wert nur aus dem Index (Plan: {joined})");
        plan.Should().Contain(l => l.Contains("IX_FileCategoryValues_Category_Value_File (CategoryId=? AND ValueId=?)", StringComparison.Ordinal),
            $"Dateien eines Werts aus dem Index (Plan: {joined})");
        plan.Should().Contain(l => l.StartsWith("SEARCH f USING COVERING INDEX IX_Files_Library_Modified", StringComparison.Ordinal),
            $"Reihenfolge im Segment aus dem Datums-Index (Plan: {joined})");
        plan.Where(l => l == "USE TEMP B-TREE FOR ORDER BY").Should().ContainSingle(
            $"voll sortiert wird nur das kleine Segment, das von seinen Zuordnungen her gelesen wird (Plan: {joined})");
        var small = plan.ToList().IndexOf("USE TEMP B-TREE FOR ORDER BY");
        plan[small - 2].Should().Be("SEARCH c USING COVERING INDEX IX_FileCategoryValues_Category_Value_File (CategoryId=? AND ValueId=?)");
        plan[small - 1].Should().Be("SEARCH f USING INTEGER PRIMARY KEY (rowid=?)");
        plan.Should().NotContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^SCAN (f|scv|c)( |$)"), $"kein Durchlauf (Plan: {joined})");
    }

    // ---- Suche nach Kategorie-Namen (0.2.30) ------------------------------------------------------

    [Theory]
    [InlineData("projekt", new[] { "IMG_a.jpg", "IMG_c.jpg" })]
    [InlineData("PROJ", new[] { "IMG_a.jpg", "IMG_c.jpg" })]
    [InlineData("xx", new[] { "IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg" })]
    [InlineData("ekt", new[] { "IMG_a.jpg", "IMG_c.jpg" })]
    public async Task Suche_nach_Kategorie_Namen_findet_alle_Dateien_mit_irgendeinem_Wert(string term, string[] expected)
        => (await SearchAsync(term)).Should().Equal(expected);

    [Fact]
    public async Task Suche_nach_Kategorie_Namen_folgt_Umbenennen_und_wirkt_mit_Filter_und_Sortierung()
    {
        await _service.RenameAsync(_projekt.Id, "Baustelle");
        (await SearchAsync("projekt")).Should().BeEmpty("der alte Name steht nirgends mehr");
        (await SearchAsync("baustelle")).Should().Equal("IMG_a.jpg", "IMG_c.jpg");

        var query = new GalleryQuery(_libraryId, GalleryFilter.All, "baustelle",
            CategoryFilters: [new GalleryCategoryFilter(_xx.Id, [N], false)]);
        (await LoadAllAsync(query)).Should().Equal(["IMG_a.jpg"], "nur a hat einen Baustellen-Wert und XX = N");

        var sorted = new GalleryQuery(_libraryId, GalleryFilter.All, "baustelle", CategorySort: new GalleryCategorySort(_projekt.Id, true));
        (await LoadAllAsync(sorted)).Should().Equal(["IMG_c.jpg", "IMG_a.jpg"], "Süd vor Nord bei absteigender Sortierung");
    }

    [Fact]
    public async Task Suche_nach_Kategorie_Namen_laeuft_ueber_den_Kategorie_Index()
    {
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, "projekt");
        foreach (var count in new[] { true, false })
        {
            var plan = await _source.ExplainAsync(query, count, CancellationToken.None);
            var joined = string.Join(" | ", plan);
            plan.Should().Contain(l => l.StartsWith("SEARCH fcn USING COVERING INDEX IX_FileCategoryValues_Category_Value_File (CategoryId=?)", StringComparison.Ordinal),
                $"Zuordnungen der passenden Kategorien aus dem Index (Plan: {joined})");
            plan.Should().NotContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^SCAN (fcn|fcv|ft|f)( |$)"), $"kein Durchlauf (Plan: {joined})");
        }
    }

    private async Task<List<string>> SearchAsync(string term)
        => await LoadAllAsync(new GalleryQuery(_libraryId, GalleryFilter.All, term, Sort: GallerySort.NameAsc));

    private async Task<List<string>> SortedAsync(GalleryCategorySort sort)
        => await LoadAllAsync(new GalleryQuery(_libraryId, GalleryFilter.All, null, Sort: GallerySort.NameDesc, CategorySort: sort));

    private async Task<List<string>> LoadAllAsync(GalleryQuery query)
    {
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zähl- und Page-SQL nutzen dieselben Bedingungen");
        return rows.Select(r => r.Name).ToList();
    }

    private async Task<List<string>> NamesAsync(params GalleryCategoryFilter[] filters)
    {
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, null, Sort: GallerySort.NameAsc, CategoryFilters: filters);
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zähl- und Page-SQL nutzen dieselben Bedingungen");
        return rows.Select(r => r.Name).ToList();
    }
}

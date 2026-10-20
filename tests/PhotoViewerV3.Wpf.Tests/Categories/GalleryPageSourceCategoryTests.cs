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

    private async Task<List<string>> NamesAsync(params GalleryCategoryFilter[] filters)
    {
        var query = new GalleryQuery(_libraryId, GalleryFilter.All, null, Sort: GallerySort.NameAsc, CategoryFilters: filters);
        var count = await _source.GetTotalCountAsync(query, CancellationToken.None);
        var rows = await _source.LoadPageAsync(query, 0, 100, count, CancellationToken.None);
        rows.Should().HaveCount(count, "Zähl- und Page-SQL nutzen dieselben Bedingungen");
        return rows.Select(r => r.Name).ToList();
    }
}

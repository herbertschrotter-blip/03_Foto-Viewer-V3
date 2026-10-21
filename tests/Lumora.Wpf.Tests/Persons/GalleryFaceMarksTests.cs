using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Database;

namespace Lumora.Wpf.Tests.Persons;

/// <summary>
/// „Alle Fotos mit Gesichtern“ und Kachel-Markierung für unbenannte Gesichter (0.2.39) gegen eine echte, migrierte DB
/// mit Fake-Gesichtern in allen vier Zuständen: a = Anna (bestätigt); b = Anna + Vorschlag Bert; c = Vorschlag Bert;
/// d = zwei unbestätigte Gesichter; e = keine Gesichter; f = nur ignoriert; g = ignoriert + unbestätigt;
/// h = bestätigt ohne Person (Person gelöscht); VID_v = Video.
/// </summary>
public sealed class GalleryFaceMarksTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, int> _ids = new();
    private LibraryContextFactory _factory = null!;
    private GalleryPageSource _source = null!;
    private int _libraryId;

    public async Task InitializeAsync()
    {
        var paths = new TempPaths(_root);
        paths.EnsureDirectoriesExist();
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
            foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "g", "h", "v" })
            {
                var video = name == "v";
                var file = new MediaFile
                {
                    LibraryId = library.Id,
                    FolderId = folder.Id,
                    Name = video ? "VID_v.mp4" : $"IMG_{name}.jpg",
                    Extension = video ? ".mp4" : ".jpg",
                    Size = 1000,
                    CreatedAtUtc = DateTime.UtcNow,
                    ModifiedAtUtc = DateTime.UtcNow,
                    MediaType = video ? MediaType.Video : MediaType.Image,
                };
                ctx.Files.Add(file);
                await ctx.SaveChangesAsync();
                _ids[name] = file.Id;
            }
        }

        using (var connection = _factory.OpenSqliteConnection())
        {
            var anna = FaceTestData.InsertPerson(connection, "Anna");
            var bert = FaceTestData.InsertPerson(connection, "Bert");
            FaceTestData.InsertFace(connection, _ids["a"], FaceState.Confirmed, personId: anna);
            FaceTestData.InsertFace(connection, _ids["b"], FaceState.Confirmed, personId: anna);
            FaceTestData.InsertFace(connection, _ids["b"], FaceState.Suggested, suggestedPersonId: bert, x: 0.6f);
            FaceTestData.InsertFace(connection, _ids["c"], FaceState.Suggested, suggestedPersonId: bert);
            FaceTestData.InsertFace(connection, _ids["d"], FaceState.Unconfirmed);
            FaceTestData.InsertFace(connection, _ids["d"], FaceState.Unconfirmed, x: 0.6f);
            FaceTestData.InsertFace(connection, _ids["f"], FaceState.Ignored);
            FaceTestData.InsertFace(connection, _ids["f"], FaceState.Ignored, x: 0.6f);
            FaceTestData.InsertFace(connection, _ids["g"], FaceState.Ignored);
            FaceTestData.InsertFace(connection, _ids["g"], FaceState.Unconfirmed, x: 0.6f);
            FaceTestData.InsertFace(connection, _ids["h"], FaceState.Confirmed);
        }

        _source = new GalleryPageSource(_factory);
        _source.Open();
    }

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
    public async Task Alle_Fotos_mit_Gesichtern_zaehlt_jeden_Zustand_ausser_ignoriert()
    {
        var expected = new[] { "IMG_a.jpg", "IMG_b.jpg", "IMG_c.jpg", "IMG_d.jpg", "IMG_g.jpg", "IMG_h.jpg" };
        (await NamesAsync(Faces(GalleryPersonMode.AnyFace))).Should().BeEquivalentTo(expected, "f hat nur ignorierte Gesichter, e keine");
        (await NamesAsync(Faces(GalleryPersonMode.AnyFace, suggestions: true))).Should().BeEquivalentTo(expected, "„auch Vorschläge“ ändert nichts");
        (await _source.GetTotalCountAsync(Faces(GalleryPersonMode.AnyFace), CancellationToken.None)).Should().Be(expected.Length);
    }

    [Fact]
    public async Task Alle_Fotos_mit_Gesichtern_wirkt_ohne_gewaehlte_Personen()
    {
        new GalleryPersonFilter([], GalleryPersonMode.AnyFace).IsActive.Should().BeTrue();
        GalleryPersonMode.AnyFace.IgnoresPersonSelection().Should().BeTrue();
        GalleryPersonMode.Any.IgnoresPersonSelection().Should().BeFalse();

        // Gewählte Personen werden ignoriert (sonst fiele c/d heraus).
        (await NamesAsync(Faces(GalleryPersonMode.AnyFace) with { PersonFilter = new GalleryPersonFilter([999], GalleryPersonMode.AnyFace) }))
            .Should().HaveCount(6);
    }

    [Fact]
    public async Task Unbenannte_Gesichter_zaehlen_ignorierte_weiterhin_nicht()
        => (await NamesAsync(Faces(GalleryPersonMode.UnnamedFaces))).Should().BeEquivalentTo(
            ["IMG_b.jpg", "IMG_c.jpg", "IMG_d.jpg", "IMG_g.jpg", "IMG_h.jpg"], "f hat nur ignorierte Gesichter");

    [Fact]
    public async Task Alle_Fotos_mit_Gesichtern_liest_ueber_abdeckenden_Index()
    {
        foreach (var count in new[] { true, false })
        {
            var plan = await _source.ExplainAsync(Faces(GalleryPersonMode.AnyFace), count, CancellationToken.None);
            var joined = string.Join(" | ", plan);
            plan.Should().Contain(l => l.Contains("USING COVERING INDEX IX_Faces_File_State_Person", StringComparison.Ordinal), joined);
            plan.Should().NotContain(l => Regex.IsMatch(l, "^SCAN (fa|fs|fp)( |$)"), $"kein Durchlauf aller Gesichter (Plan: {joined})");
        }
    }

    [Fact]
    public async Task Kachel_Daten_zaehlen_unbenannte_Gesichter_je_Datei_ohne_ignorierte()
    {
        var marks = await _source.GetFaceMarksForFilesAsync(_ids.Values.ToList(), CancellationToken.None);
        GalleryItemPersons Of(string name) => GalleryItemPersons.From(marks.Persons.Where(r => r.FileId == _ids[name]), marks.UnnamedCount(_ids[name]));

        Of("a").HasUnnamed.Should().BeFalse();
        Of("a").HasConfirmed.Should().BeTrue();
        Of("b").UnnamedCount.Should().Be(1, "der Vorschlag ist noch nicht benannt");
        Of("b").UnnamedToolTip.Should().Be("1 Gesicht noch nicht benannt" + Environment.NewLine + "Vorschläge: Bert?");
        Of("c").UnnamedCount.Should().Be(1);
        Of("d").UnnamedCount.Should().Be(2);
        Of("d").UnnamedToolTip.Should().Be("2 Gesichter noch nicht benannt");
        Of("e").Should().BeSameAs(GalleryItemPersons.Empty);
        Of("f").Should().BeSameAs(GalleryItemPersons.Empty, "nur ignorierte Gesichter → kein Symbol");
        Of("g").UnnamedCount.Should().Be(1, "das ignorierte Gesicht zählt nicht");
        Of("h").UnnamedCount.Should().Be(1, "bestätigt ohne Person gilt wie im Filter als unbenannt");
        Of("v").Should().BeSameAs(GalleryItemPersons.Empty);

        // Bisherige Schnittstelle liefert dieselben Personen-Zeilen.
        (await _source.GetPersonsForFilesAsync(_ids.Values.ToList(), CancellationToken.None)).Should().BeEquivalentTo(marks.Persons);
    }

    [Fact]
    public void Automation_Text_der_Kachel_nennt_Personen_und_unbenannte_Gesichter()
    {
        var persons = GalleryItemPersons.From([new GalleryFacePersonRow(1, true, 1, "Anna")], unnamedCount: 2);
        persons.AutomationText.Should().Be("Personen: Anna; 2 Gesichter noch nicht benannt");
        GalleryItemPersons.From([], unnamedCount: 1).AutomationText.Should().Be("1 Gesicht noch nicht benannt");
        GalleryItemPersons.Empty.AutomationText.Should().BeEmpty();
        GalleryItemPersons.Empty.UnnamedToolTip.Should().BeEmpty();
    }

    private GalleryQuery Faces(GalleryPersonMode mode, bool suggestions = false)
        => new GalleryQuery(_libraryId, GalleryFilterState.None, null, Sort: GallerySort.NameAsc) with
        {
            PersonFilter = new GalleryPersonFilter([], mode, suggestions),
        };

    private async Task<IReadOnlyList<string>> NamesAsync(GalleryQuery query)
    {
        var rows = await _source.LoadPageAsync(query, 0, 50, 0, CancellationToken.None);
        return rows.Select(r => r.Name).ToList();
    }

    private sealed class TempPaths(string root) : Lumora.Services.Infrastructure.IAppDataPaths
    {
        public string AppDataRoot => root;
        public string LibraryDatabasePath => Path.Combine(root, "library.db");
        public string ThumbnailsRoot => Path.Combine(root, "thumbs");
        public string LogsRoot => Path.Combine(root, "logs");
        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString());
        public void EnsureDirectoriesExist() => Directory.CreateDirectory(root);
    }
}

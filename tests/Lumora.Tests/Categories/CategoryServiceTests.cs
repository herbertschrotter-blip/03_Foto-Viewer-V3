using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Services.Categories;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Categories;

/// <summary>
/// Kategorien mit Werten (ADR-0027): Stammdaten (eindeutig ohne Groß-/Kleinschreibung, Reihenfolge), ein Wert je
/// Kategorie und Datei (Setzen ersetzt, null entfernt), Prüfung „Wert gehört zur Kategorie“, Zähler, Kaskaden beim
/// Löschen von Wert/Kategorie/Datei, Nachrichten und Übernahme bei „Neu aufbauen“.
/// </summary>
public sealed class CategoryServiceTests
{
    private static async Task<(int LibraryId, int A, int B, int C)> ArrangeAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("x", "a.jpg"));
        fx.WriteLibraryFile(Path.Combine("x", "b.jpg"));
        fx.WriteLibraryFile(Path.Combine("y", "c.mp4"));
        await fx.ScanAsync(library.Id);
        return (library.Id,
            (await fx.FindFileAsync("a.jpg"))!.Id,
            (await fx.FindFileAsync("b.jpg"))!.Id,
            (await fx.FindFileAsync("c.mp4"))!.Id);
    }

    private static async Task<Dictionary<int, int>> ValuesOfCategoryAsync(TempLibraryFixture fx, int categoryId)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.FileCategoryValues.AsNoTracking()
            .Where(x => x.CategoryId == categoryId)
            .ToDictionaryAsync(x => x.FileId, x => x.ValueId);
    }

    [Fact]
    public async Task Kategorie_mit_Werten_anlegen_trimmt_und_haelt_die_Reihenfolge()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateCategoryService();
        var received = new List<CategoriesChangedMessage>();
        fx.Messenger.Register<CategoriesChangedMessage>(this, (_, m) => received.Add(m));

        var xx = await svc.CreateAsync("  XX ", ["N", " NN ", "", "n"]);
        var projekt = await svc.CreateAsync("Projekt");

        xx.Name.Should().Be("XX");
        xx.Values.Select(v => v.Name).Should().Equal(["N", "NN"], "leere und doppelte Werte (ohne Groß-/Kleinschreibung) fallen weg");
        projekt.SortOrder.Should().Be(xx.SortOrder + 1);
        projekt.Values.Should().BeEmpty();
        (await svc.GetAllAsync()).Select(c => c.Name).Should().Equal("XX", "Projekt");
        received.Should().HaveCount(2).And.OnlyContain(m => m.Kind == CategoriesChangeKind.Created);
    }

    [Fact]
    public async Task Doppelte_und_leere_Namen_werden_abgelehnt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N"]);
        var other = await svc.CreateAsync("Andere", ["N"]);

        await FluentActions.Awaiting(() => svc.CreateAsync("xx")).Should().ThrowAsync<InvalidOperationException>().WithMessage("*existiert bereits*");
        await FluentActions.Awaiting(() => svc.CreateAsync("   ")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.RenameAsync(other.Id, "XX")).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => svc.AddValueAsync(xx.Id, "n")).Should().ThrowAsync<InvalidOperationException>().WithMessage("*bereits einen Wert*");

        (await svc.AddValueAsync(other.Id, "NN")).Name.Should().Be("NN", "gleiche Wertnamen in verschiedenen Kategorien sind erlaubt");
    }

    [Fact]
    public async Task Ein_Wert_je_Kategorie_und_Datei_setzen_ersetzen_entfernen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, a, b, c) = await ArrangeAsync(fx);
        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N", "NN"]);
        var projekt = await svc.CreateAsync("Projekt", ["Nord"]);
        int n = xx.Values[0].Id, nn = xx.Values[1].Id, nord = projekt.Values[0].Id;
        fx.Messages.Clear();

        (await svc.SetValueAsync([a, b], xx.Id, n)).Should().Be(2);
        (await svc.SetValueAsync([b, c], xx.Id, nn)).Should().Be(2, "b wird ersetzt, c neu gesetzt");
        (await svc.SetValueAsync([c], xx.Id, nn)).Should().Be(0, "gleicher Wert zählt nicht als Änderung");
        (await svc.SetValueAsync([a], projekt.Id, nord)).Should().Be(1);
        (await svc.SetValueAsync([a, 999_999], xx.Id, null)).Should().Be(1, "unbekannte FileIds werden übergangen");

        (await ValuesOfCategoryAsync(fx, xx.Id)).Should().BeEquivalentTo(new Dictionary<int, int> { [b] = nn, [c] = nn });
        var map = await svc.GetValuesForFilesAsync([a, b, c]);
        map[a].Should().BeEquivalentTo(new Dictionary<int, int> { [projekt.Id] = nord });
        map[b][xx.Id].Should().Be(nn);
        (await svc.GetFileCountsAsync()).Should().BeEquivalentTo(new Dictionary<int, int> { [nn] = 2, [nord] = 1 });

        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().NotBeEmpty()
            .And.OnlyContain(m => m.LibraryId == libraryId);
    }

    [Fact]
    public async Task Wert_einer_fremden_Kategorie_wird_abgelehnt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, a, _, _) = await ArrangeAsync(fx);
        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N"]);
        var projekt = await svc.CreateAsync("Projekt", ["Nord"]);

        await FluentActions.Awaiting(() => svc.SetValueAsync([a], xx.Id, projekt.Values[0].Id))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*gehört nicht*");
        await FluentActions.Awaiting(() => svc.SetValueAsync([a], 4711, null))
            .Should().ThrowAsync<InvalidOperationException>();
        (await svc.GetValuesForFilesAsync([a])).Should().BeEmpty();
    }

    [Fact]
    public async Task Wert_Kategorie_oder_Datei_loeschen_nimmt_die_Zuweisungen_mit()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, a, b, c) = await ArrangeAsync(fx);
        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N", "NN"]);
        var projekt = await svc.CreateAsync("Projekt", ["Nord"]);
        await svc.SetValueAsync([a, b], xx.Id, xx.Values[0].Id);
        await svc.SetValueAsync([c], xx.Id, xx.Values[1].Id);
        await svc.SetValueAsync([a, b, c], projekt.Id, projekt.Values[0].Id);
        var received = new List<CategoriesChangedMessage>();
        fx.Messenger.Register<CategoriesChangedMessage>(this, (_, m) => received.Add(m));

        (await svc.DeleteValueAsync(xx.Values[0].Id)).Should().Be(2);
        (await ValuesOfCategoryAsync(fx, xx.Id)).Keys.Should().Equal(c);
        (await svc.GetAllAsync()).First(k => k.Id == xx.Id).Values.Select(v => v.Name).Should().Equal("NN");

        (await svc.DeleteAsync(projekt.Id)).Should().Be(3);
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.CategoryValues.CountAsync(v => v.CategoryId == projekt.Id)).Should().Be(0);
            // Datei weg (Raw-SQL wie beim Papierkorb) → Zuweisung weg (ON DELETE CASCADE)
            await ctx.Database.ExecuteSqlAsync($"DELETE FROM Files WHERE Id = {c}");
            (await ctx.FileCategoryValues.CountAsync()).Should().Be(0);
        }

        received.Select(m => m.Kind).Should().Equal(CategoriesChangeKind.ValuesChanged, CategoriesChangeKind.Deleted);
    }

    [Fact]
    public async Task Umbenennen_und_Reihenfolge_von_Kategorien_und_Werten()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N", "NN", "NNN"]);
        var projekt = await svc.CreateAsync("Projekt");

        await svc.RenameAsync(xx.Id, "Größe");
        await svc.RenameValueAsync(xx.Values[0].Id, "Klein");
        await svc.ReorderAsync([projekt.Id]);
        await svc.ReorderValuesAsync(xx.Id, [xx.Values[2].Id, xx.Values[0].Id]);

        var all = await svc.GetAllAsync();
        all.Select(c => c.Name).Should().Equal("Projekt", "Größe");
        all[1].Values.Select(v => v.Name).Should().Equal("NNN", "Klein", "NN");
    }

    /// <summary>
    /// Strg+K (0.2.23) liest die Ausgangswerte der ganzen Auswahl aus der DB und setzt danach je geänderter Kategorie
    /// einen Wert — auch bei Auswahlen über die Chunk-Grenze (500) hinweg und mit Dateien, die noch keinen Wert haben.
    /// </summary>
    [Fact]
    public async Task Werte_einer_grossen_Auswahl_lesen_und_je_Kategorie_setzen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, a, _, _) = await ArrangeAsync(fx);
        var ids = new List<int> { a };
        await using (var ctx = fx.CreateContext())
        {
            var folderId = (await ctx.Files.AsNoTracking().FirstAsync(f => f.Id == a)).FolderId;
            var files = Enumerable.Range(0, 1200).Select(i => new Lumora.Domain.Entities.MediaFile
            {
                LibraryId = libraryId,
                FolderId = folderId,
                Name = $"bulk_{i:D4}.jpg",
                Extension = ".jpg",
                Size = 1,
                CreatedAtUtc = DateTime.UtcNow,
                ModifiedAtUtc = DateTime.UtcNow,
                MediaType = MediaType.Image,
            }).ToList();
            ctx.Files.AddRange(files);
            await ctx.SaveChangesAsync();
            ids.AddRange(files.Select(f => f.Id));
        }

        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N", "NN"]);
        var projekt = await svc.CreateAsync("Projekt", ["Nord"]);
        await svc.SetValueAsync([a], xx.Id, xx.Values[1].Id);
        await svc.SetValueAsync(ids.Skip(600).ToList(), projekt.Id, projekt.Values[0].Id);

        var before = await svc.GetValuesForFilesAsync(ids);
        before.Should().HaveCount(602, "a (NN) plus 601 Dateien mit Projekt-Wert; Dateien ohne Wert fehlen");
        before[a].Should().Equal(new Dictionary<int, int> { [xx.Id] = xx.Values[1].Id });
        before[ids[^1]].Should().Equal(new Dictionary<int, int> { [projekt.Id] = projekt.Values[0].Id });

        // Ergebnis des Dialogs: XX = N für alle, Projekt = Kein Wert für alle
        (await svc.SetValueAsync(ids, xx.Id, xx.Values[0].Id)).Should().Be(ids.Count, "a wird ersetzt, alle anderen neu gesetzt");
        (await svc.SetValueAsync(ids, projekt.Id, null)).Should().Be(601);

        var after = await svc.GetValuesForFilesAsync(ids);
        after.Should().HaveCount(ids.Count);
        after.Values.Should().OnlyContain(v => v.Count == 1 && v[xx.Id] == xx.Values[0].Id);
    }

    [Fact]
    public async Task Neu_aufbauen_uebernimmt_Kategorie_Werte_auf_die_neuen_FileIds()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (libraryId, a, b, _) = await ArrangeAsync(fx);
        using var svc = fx.CreateCategoryService();
        var xx = await svc.CreateAsync("XX", ["N", "NN"]);
        await svc.SetValueAsync([a], xx.Id, xx.Values[0].Id);
        await svc.SetValueAsync([b], xx.Id, xx.Values[1].Id);

        var result = await fx.CreateScanService().ScanAsync(libraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        var newA = (await fx.FindFileAsync("a.jpg"))!.Id;
        var newB = (await fx.FindFileAsync("b.jpg"))!.Id;
        newA.Should().NotBe(a, "Neu aufbauen vergibt neue FileIds");
        (await ValuesOfCategoryAsync(fx, xx.Id)).Should().BeEquivalentTo(new Dictionary<int, int>
        {
            [newA] = xx.Values[0].Id,
            [newB] = xx.Values[1].Id,
        });
    }
}

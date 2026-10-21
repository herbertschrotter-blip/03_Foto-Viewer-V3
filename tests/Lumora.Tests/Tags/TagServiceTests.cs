using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Services.Tags;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Tags;

/// <summary>
/// Tag-Stammdaten (eindeutig ohne Groß-/Kleinschreibung, Palette, Reihenfolge), Zuweisungen in Chunks mit
/// FilesChangedMessage(Metadata), Zähler, Batch-Abfrage je Page und das ON DELETE CASCADE der FileTags-Tabelle
/// (Files-Zeile weg → Zuweisung weg, egal ob über EF oder Raw-SQL gelöscht).
/// </summary>
public sealed class TagServiceTests
{
    private static async Task<(Library Library, int AlphaId, int BravoId, int CharlieId)> ArrangeScannedLibraryAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "bravo.jpg"));
        fx.WriteLibraryFile(Path.Combine("b", "charlie.mp4"));
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;
        var bravo = (await fx.FindFileAsync("bravo.jpg"))!;
        var charlie = (await fx.FindFileAsync("charlie.mp4"))!;
        return (library, alpha.Id, bravo.Id, charlie.Id);
    }

    private static async Task<int> CountFileTagsAsync(TempLibraryFixture fx, int? fileId = null)
    {
        await using var ctx = fx.CreateContext();
        return fileId is { } id
            ? await ctx.FileTags.CountAsync(ft => ft.FileId == id)
            : await ctx.FileTags.CountAsync();
    }

    [Fact]
    public async Task Create_vergibt_Palettenfarbe_und_SortOrder_und_trimmt_den_Namen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateTagService();
        var received = new List<TagsChangedMessage>();
        fx.Messenger.Register<TagsChangedMessage>(this, (_, m) => received.Add(m));

        var urlaub = await svc.CreateAsync("  Urlaub ");
        var familie = await svc.CreateAsync("Familie", "#ff0000");

        urlaub.Name.Should().Be("Urlaub");
        urlaub.Color.Should().Be(TagPalette.ForIndex(0));
        urlaub.SortOrder.Should().Be(0);
        familie.Color.Should().Be("#FF0000");
        familie.SortOrder.Should().Be(1);
        (await svc.GetAllAsync()).Select(t => t.Name).Should().Equal("Urlaub", "Familie");
        received.Should().HaveCount(2).And.OnlyContain(m => m.Kind == TagsChangeKind.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_wirft_bei_leerem_Namen(string? name)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateTagService();

        var act = () => svc.CreateAsync(name!);

        await act.Should().ThrowAsync<ArgumentException>();
        (await svc.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_wirft_bei_zu_langem_Namen_und_ungueltiger_Farbe()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateTagService();

        await ((Func<Task>)(() => svc.CreateAsync(new string('x', TagService.MaxNameLength + 1)))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => svc.CreateAsync("Ok", "rot"))).Should().ThrowAsync<ArgumentException>();
        (await svc.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_und_Rename_lehnen_Duplikate_ohne_Gross_Kleinschreibung_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateTagService();
        var urlaub = await svc.CreateAsync("Urlaub");
        await svc.CreateAsync("Familie");

        var create = () => svc.CreateAsync("URLAUB");
        var rename = () => svc.RenameAsync(urlaub.Id, "familie");

        (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("existiert bereits");
        await rename.Should().ThrowAsync<InvalidOperationException>();
        (await svc.GetAllAsync()).Should().HaveCount(2);

        // Umbenennen in sich selbst (andere Schreibweise) ist erlaubt
        await svc.RenameAsync(urlaub.Id, "URLAUB");
        (await svc.GetAllAsync()).First(t => t.Id == urlaub.Id).Name.Should().Be("URLAUB");
    }

    [Fact]
    public async Task Rename_SetColor_und_Reorder_senden_TagsChangedMessage()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateTagService();
        var a = await svc.CreateAsync("A");
        var b = await svc.CreateAsync("B");
        var c = await svc.CreateAsync("C");
        var received = new List<TagsChangedMessage>();
        fx.Messenger.Register<TagsChangedMessage>(this, (_, m) => received.Add(m));

        await svc.RenameAsync(a.Id, "Alpha");
        await svc.SetColorAsync(b.Id, "#123abc");
        await svc.ReorderAsync(new[] { c.Id, a.Id }); // B nicht genannt → dahinter

        var all = await svc.GetAllAsync();
        all.Select(t => t.Name).Should().Equal("C", "Alpha", "B");
        all.Select(t => t.SortOrder).Should().Equal(0, 1, 2);
        all.First(t => t.Id == b.Id).Color.Should().Be("#123ABC");
        received.Select(m => m.Kind).Should().Equal(TagsChangeKind.Renamed, TagsChangeKind.Recolored, TagsChangeKind.Reordered);
    }

    [Fact]
    public async Task Assign_und_Remove_schreiben_Zuweisungen_und_senden_Metadata_Nachricht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (library, alphaId, bravoId, charlieId) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateTagService();
        var urlaub = await svc.CreateAsync("Urlaub");
        var familie = await svc.CreateAsync("Familie");

        var added = await svc.AssignAsync(new[] { alphaId, bravoId, 999_999 }, new[] { urlaub.Id, familie.Id });

        added.Should().Be(4, "zwei Dateien × zwei Tags; die unbekannte Id wird übergangen");
        var message = fx.Messages.OfKind(FilesChangeKind.Metadata).Should().ContainSingle().Subject;
        message.LibraryId.Should().Be(library.Id);
        message.FileIds.Should().BeEquivalentTo(new[] { alphaId, bravoId });

        var byFile = await svc.GetTagIdsForFilesAsync(new[] { alphaId, bravoId, charlieId });
        byFile.Should().ContainKey(alphaId).WhoseValue.Should().Equal(urlaub.Id, familie.Id);
        byFile.Should().ContainKey(bravoId);
        byFile.Should().NotContainKey(charlieId, "ohne Tag fehlt die Datei im Ergebnis");

        // Erneutes Zuweisen ist idempotent
        (await svc.AssignAsync(new[] { alphaId }, new[] { urlaub.Id })).Should().Be(0);

        var removed = await svc.RemoveAsync(new[] { alphaId }, new[] { familie.Id });
        removed.Should().Be(1);
        (await svc.GetTagIdsForFilesAsync(new[] { alphaId }))[alphaId].Should().Equal(urlaub.Id);
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().HaveCount(3);

        var counts = await svc.GetFileCountsAsync();
        counts[urlaub.Id].Should().Be(2);
        counts[familie.Id].Should().Be(1);
    }

    [Fact]
    public async Task Assign_verarbeitet_mehr_als_500_Dateien_in_Chunks()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var now = DateTime.UtcNow;
        var ids = new List<int>();
        await using (var ctx = fx.CreateContext())
        {
            var folder = new Folder { LibraryId = library.Id, Path = fx.LibraryRoot, Name = "library", ScannedAt = now };
            ctx.Folders.Add(folder);
            await ctx.SaveChangesAsync();
            var rows = Enumerable.Range(0, 1200).Select(i => new MediaFile
            {
                LibraryId = library.Id,
                FolderId = folder.Id,
                Name = $"f{i:D4}.jpg",
                Extension = ".jpg",
                Size = i,
                CreatedAtUtc = now,
                ModifiedAtUtc = now,
                MediaType = MediaType.Image,
            }).ToList();
            ctx.Files.AddRange(rows);
            await ctx.SaveChangesAsync();
            ids.AddRange(rows.Select(r => r.Id));
        }

        using var svc = fx.CreateTagService();
        var tag = await svc.CreateAsync("Viele");

        (await svc.AssignAsync(ids, new[] { tag.Id })).Should().Be(1200);
        (await CountFileTagsAsync(fx)).Should().Be(1200);
        (await svc.GetTagIdsForFilesAsync(ids)).Should().HaveCount(1200);
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().ContainSingle().Which.FileIds.Should().HaveCount(1200);

        (await svc.RemoveForFilesAsync(ids.Take(700).ToList())).Should().Be(700);
        (await CountFileTagsAsync(fx)).Should().Be(500);
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().ContainSingle("RemoveForFiles sendet keine Nachricht");
    }

    [Fact]
    public async Task Delete_entfernt_Tag_und_Zuweisungen_und_meldet_betroffene_Dateien()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (library, alphaId, bravoId, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateTagService();
        var urlaub = await svc.CreateAsync("Urlaub");
        var familie = await svc.CreateAsync("Familie");
        await svc.AssignAsync(new[] { alphaId, bravoId }, new[] { urlaub.Id });
        await svc.AssignAsync(new[] { alphaId }, new[] { familie.Id });
        fx.Messages.Clear();
        var received = new List<TagsChangedMessage>();
        fx.Messenger.Register<TagsChangedMessage>(this, (_, m) => received.Add(m));

        var removed = await svc.DeleteAsync(urlaub.Id);

        removed.Should().Be(2);
        (await svc.GetAllAsync()).Select(t => t.Id).Should().Equal(familie.Id);
        (await CountFileTagsAsync(fx)).Should().Be(1);
        received.Should().ContainSingle().Which.Should().Be(new TagsChangedMessage(TagsChangeKind.Deleted, urlaub.Id));
        var message = fx.Messages.OfKind(FilesChangeKind.Metadata).Should().ContainSingle().Subject;
        message.LibraryId.Should().Be(library.Id);
        message.FileIds.Should().BeEquivalentTo(new[] { alphaId, bravoId });

        await ((Func<Task>)(() => svc.DeleteAsync(urlaub.Id))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Loeschen_einer_Datei_entfernt_ihre_Zuweisungen_per_ON_DELETE_CASCADE()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, bravoId, charlieId) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateTagService();
        var tag = await svc.CreateAsync("Urlaub");
        await svc.AssignAsync(new[] { alphaId, bravoId, charlieId }, new[] { tag.Id });

        // Voraussetzung: SQLite erzwingt Fremdschlüssel auf dieser Verbindung (e_sqlite3-Default)
        using (var connection = fx.ContextFactory.OpenSqliteConnection())
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys;";
            Convert.ToInt64(pragma.ExecuteScalar()).Should().Be(1, "ohne PRAGMA foreign_keys=ON greift ON DELETE CASCADE nicht");

            // Raw-SQL-Pfad (LibraryCatalogCleanup: DELETE FROM Files WHERE LibraryId = …)
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM Files WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", alphaId);
            delete.ExecuteNonQuery().Should().Be(1);
        }

        // EF-Pfad (FileOperationService.RemoveCatalogEntryAsync: ctx.Files.Remove)
        await using (var ctx = fx.CreateContext())
        {
            var bravo = await ctx.Files.FirstAsync(f => f.Id == bravoId);
            ctx.Files.Remove(bravo);
            await ctx.SaveChangesAsync();
        }

        (await CountFileTagsAsync(fx, alphaId)).Should().Be(0);
        (await CountFileTagsAsync(fx, bravoId)).Should().Be(0);
        (await CountFileTagsAsync(fx, charlieId)).Should().Be(1);
        (await svc.GetFileCountsAsync())[tag.Id].Should().Be(1);
    }

    [Fact]
    public async Task Papierkorb_ueber_FileOperationService_laesst_keine_Zuweisung_zurueck()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, bravoId, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateTagService();
        var tag = await svc.CreateAsync("Urlaub");
        await svc.AssignAsync(new[] { alphaId, bravoId }, new[] { tag.Id });
        using var ops = fx.CreateFileOperationService();

        var result = await ops.RecycleAsync(new[] { alphaId });

        result.Failed.Should().Be(0);
        (await fx.FindFileByIdAsync(alphaId)).Should().BeNull();
        (await CountFileTagsAsync(fx, alphaId)).Should().Be(0);
        (await CountFileTagsAsync(fx, bravoId)).Should().Be(1);
    }

    [Fact]
    public async Task Leere_Listen_sind_NoOps_ohne_Nachricht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateTagService();
        var tag = await svc.CreateAsync("Urlaub");

        (await svc.AssignAsync(Array.Empty<int>(), new[] { tag.Id })).Should().Be(0);
        (await svc.AssignAsync(new[] { alphaId }, Array.Empty<int>())).Should().Be(0);
        (await svc.RemoveAsync(Array.Empty<int>(), new[] { tag.Id })).Should().Be(0);
        (await svc.RemoveForFilesAsync(Array.Empty<int>())).Should().Be(0);
        (await svc.GetTagIdsForFilesAsync(Array.Empty<int>())).Should().BeEmpty();
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Empfaenger_Ausnahme_bricht_die_Zuweisung_nicht_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var (_, alphaId, _, _) = await ArrangeScannedLibraryAsync(fx);
        using var svc = fx.CreateTagService();
        var tag = await svc.CreateAsync("Urlaub");
        var recipient = new object();
        IMessenger messenger = fx.Messenger;
        messenger.Register<object, FilesChangedMessage>(recipient, static (_, _) => throw new InvalidOperationException("Empfänger kaputt"));

        var act = () => svc.AssignAsync(new[] { alphaId }, new[] { tag.Id });

        await act.Should().NotThrowAsync();
        (await CountFileTagsAsync(fx, alphaId)).Should().Be(1);
    }

    [Fact]
    public void Palette_hat_zwoelf_gueltige_Farben_und_laeuft_reihum()
    {
        TagPalette.Colors.Should().HaveCount(12).And.OnlyContain(c => TagPalette.IsValid(c));
        TagPalette.ForIndex(12).Should().Be(TagPalette.Colors[0]);
        TagPalette.IsValid("#12AB3f").Should().BeTrue();
        TagPalette.IsValid("12AB3F").Should().BeFalse();
        TagPalette.IsValid("#12AB3").Should().BeFalse();
        TagPalette.IsValid("#GGGGGG").Should().BeFalse();
    }
}

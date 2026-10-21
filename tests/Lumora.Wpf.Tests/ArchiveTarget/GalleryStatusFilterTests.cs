using System.IO;
using Lumora.Domain.Enums;
using Lumora.Gallery.Data;

namespace Lumora.Wpf.Tests.ArchiveTarget;

/// <summary>
/// Status-Filter der Galerie (ADR-0020): „Nicht archiviert“ = Dateien unter einer Eingangs-Wurzel, „Archiviert“ =
/// unter der Archiv-Wurzel — SQL über Folders.RootId → LibraryRoots.Role, Count- und Page-Query stimmen überein,
/// auch zusammen mit Suche und Ordnerfilter; das DTO trägt die Rolle für das Badge „Eingang“.
/// </summary>
public sealed class GalleryStatusFilterTests
{
    [Fact]
    public async Task NotArchived_und_Archived_teilen_die_Bibliothek_nach_Rolle_der_Wurzel()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var archiveRoot = await db.AddFolderAsync(db.ArchiveRootId, db.ArchiveDir);
        var year = await db.AddFolderAsync(db.ArchiveRootId, Path.Combine(db.ArchiveDir, "2024"), archiveRoot.Id);
        var inboxRoot = await db.AddFolderAsync(db.InboxRootId, db.InboxDir);
        var camera = await db.AddFolderAsync(db.InboxRootId, Path.Combine(db.InboxDir, "kamera"), inboxRoot.Id);
        await db.AddFileAsync(archiveRoot, "alt.jpg");
        await db.AddFileAsync(year, "urlaub-1.jpg");
        await db.AddFileAsync(year, "urlaub-2.mp4", MediaType.Video);
        await db.AddFileAsync(inboxRoot, "neu.jpg");
        await db.AddFileAsync(camera, "urlaub-3.jpg");

        using var source = new GalleryPageSource(db.Factory);
        source.Open();

        (await CountAsync(source, db, GalleryFilterState.None)).Should().Be(5);
        (await CountAsync(source, db, new GalleryFilterState(Location: GalleryLocation.Inbox))).Should().Be(2);
        (await CountAsync(source, db, new GalleryFilterState(Location: GalleryLocation.Library))).Should().Be(3);

        var inbox = await PageAsync(source, db, new GalleryFilterState(Location: GalleryLocation.Inbox));
        inbox.Select(f => f.Name).Should().BeEquivalentTo("neu.jpg", "urlaub-3.jpg");
        inbox.Should().OnlyContain(f => f.IsInbox && f.RootRole == 0);

        var archived = await PageAsync(source, db, new GalleryFilterState(Location: GalleryLocation.Library));
        archived.Should().HaveCount(3).And.OnlyContain(f => !f.IsInbox && f.RootRole == 1);

        var all = await PageAsync(source, db, GalleryFilterState.None);
        all.Count(f => f.IsInbox).Should().Be(2, "das Badge hängt an der Datei, nicht am Filter");
    }

    [Fact]
    public async Task Status_Filter_wirkt_zusammen_mit_Suche_und_Ordnerfilter()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var archiveRoot = await db.AddFolderAsync(db.ArchiveRootId, db.ArchiveDir);
        var inboxRoot = await db.AddFolderAsync(db.InboxRootId, db.InboxDir);
        var camera = await db.AddFolderAsync(db.InboxRootId, Path.Combine(db.InboxDir, "kamera"), inboxRoot.Id);
        await db.AddFileAsync(archiveRoot, "urlaub-archiv.jpg");
        await db.AddFileAsync(inboxRoot, "urlaub-eingang.jpg");
        await db.AddFileAsync(camera, "urlaub-kamera.jpg");
        await db.AddFileAsync(camera, "sonstiges.jpg");

        using var source = new GalleryPageSource(db.Factory);
        source.Open();

        var search = new GalleryQuery(db.LibraryId, new GalleryFilterState(Location: GalleryLocation.Inbox), "urlaub");
        (await source.GetTotalCountAsync(search, CancellationToken.None)).Should().Be(2);
        (await source.LoadPageAsync(search, 0, 100, 2, CancellationToken.None)).Select(f => f.Name)
            .Should().BeEquivalentTo("urlaub-eingang.jpg", "urlaub-kamera.jpg");

        var archivedSearch = search with { Filter = new GalleryFilterState(Location: GalleryLocation.Library) };
        (await source.GetTotalCountAsync(archivedSearch, CancellationToken.None)).Should().Be(1);

        var folder = new GalleryQuery(db.LibraryId, new GalleryFilterState(Location: GalleryLocation.Inbox), null, FolderId: camera.Id);
        (await source.GetTotalCountAsync(folder, CancellationToken.None)).Should().Be(2);
        (await source.GetTotalCountAsync(folder with { Filter = new GalleryFilterState(Location: GalleryLocation.Library) }, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Altzeilen_ohne_RootId_zaehlen_weder_als_Eingang_noch_als_Archiv()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var orphan = await db.AddFolderAsync(db.ArchiveRootId, Path.Combine(db.RootDir, "verwaist"));
        await using (var ctx = db.Factory.Create())
        {
            var row = await ctx.Folders.FindAsync(orphan.Id);
            row!.RootId = null;
            await ctx.SaveChangesAsync();
        }

        await db.AddFileAsync(orphan, "x.jpg");

        using var source = new GalleryPageSource(db.Factory);
        source.Open();

        (await CountAsync(source, db, GalleryFilterState.None)).Should().Be(1);
        (await CountAsync(source, db, new GalleryFilterState(Location: GalleryLocation.Inbox))).Should().Be(0);
        (await CountAsync(source, db, new GalleryFilterState(Location: GalleryLocation.Library))).Should().Be(0);
        (await PageAsync(source, db, GalleryFilterState.None)).Single().RootRole.Should().BeNull();
    }

    private static Task<int> CountAsync(GalleryPageSource source, ArchiveTestDb db, GalleryFilterState filter)
        => source.GetTotalCountAsync(new GalleryQuery(db.LibraryId, filter, null), CancellationToken.None);

    private static async Task<IReadOnlyList<GalleryFileDto>> PageAsync(GalleryPageSource source, ArchiveTestDb db, GalleryFilterState filter)
    {
        var query = new GalleryQuery(db.LibraryId, filter, null);
        var total = await source.GetTotalCountAsync(query, CancellationToken.None);
        return await source.LoadPageAsync(query, 0, 100, total, CancellationToken.None);
    }
}

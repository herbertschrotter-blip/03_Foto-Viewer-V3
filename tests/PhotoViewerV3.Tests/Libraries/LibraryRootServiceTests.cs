using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Libraries;

/// <summary>Wurzelverwaltung des LibraryService (ADR-0020): anlegen, Rollen, Überlappungen, entfernen nur aus dem Katalog.</summary>
public sealed class LibraryRootServiceTests
{
    [Fact]
    public async Task Create_legt_Archiv_Wurzel_an_und_LibraryInfo_fuehrt_sie()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var service = fx.CreateLibraryService();

        var info = await service.CreateAsync("Test", fx.LibraryRoot);

        info.Roots.Should().ContainSingle(r => r.IsArchive && r.Path == fx.LibraryRoot);
        info.ArchiveRoot!.Name.Should().Be("library");
        (await service.GetAsync(info.Id))!.Roots.Should().HaveCount(1);
    }

    [Fact]
    public async Task AddRoot_Eingang_und_Regeln_fuer_Duplikate_Verschachtelung_und_zweites_Archiv()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var service = fx.CreateLibraryService();
        var lib = await service.CreateAsync("Test", fx.LibraryRoot);

        var inbox = await service.AddRootAsync(lib.Id, fx.InboxDir, LibraryRootRole.Inbox);
        inbox.IsInbox.Should().BeTrue();
        inbox.Name.Should().Be("inbox");

        var roots = await service.GetRootsAsync(lib.Id);
        roots.Select(r => r.Role).Should().Equal(new[] { LibraryRootRole.Archive, LibraryRootRole.Inbox }, "Archiv zuerst");

        await FluentActions.Awaiting(() => service.AddRootAsync(lib.Id, fx.InboxDir, LibraryRootRole.Inbox))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*gehört bereits*");

        var nested = Path.Combine(fx.LibraryRoot, "sub");
        Directory.CreateDirectory(nested);
        await FluentActions.Awaiting(() => service.AddRootAsync(lib.Id, nested, LibraryRootRole.Inbox))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*innerhalb der Wurzel*");

        await FluentActions.Awaiting(() => service.AddRootAsync(lib.Id, fx.RootDir, LibraryRootRole.Inbox))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*enthält die Wurzel*");

        await FluentActions.Awaiting(() => service.AddRootAsync(lib.Id, fx.OutsideDir, LibraryRootRole.Archive))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*bereits eine Archiv-Wurzel*");

        await FluentActions.Awaiting(() => service.AddRootAsync(lib.Id, Path.Combine(fx.RootDir, "gibt-es-nicht"), LibraryRootRole.Inbox))
            .Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task SetRootRole_tauscht_Archiv_und_haelt_RootPath_synchron()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var service = fx.CreateLibraryService();
        var lib = await service.CreateAsync("Test", fx.LibraryRoot);
        var inbox = await service.AddRootAsync(lib.Id, fx.InboxDir, LibraryRootRole.Inbox);
        var archiveId = lib.ArchiveRoot!.Id;

        await FluentActions.Awaiting(() => service.SetRootRoleAsync(archiveId, LibraryRootRole.Inbox))
            .Should().ThrowAsync<InvalidOperationException>("die einzige Archiv-Wurzel kann nicht zum Eingang werden");

        await service.SetRootRoleAsync(inbox.Id, LibraryRootRole.Archive);

        var info = (await service.GetAsync(lib.Id))!;
        info.ArchiveRoot!.Id.Should().Be(inbox.Id);
        info.RootPath.Should().Be(fx.InboxDir);
        info.Roots.Single(r => r.Id == archiveId).IsInbox.Should().BeTrue();
        await using var ctx = fx.CreateContext();
        (await ctx.Libraries.SingleAsync()).RootPath.Should().Be(fx.InboxDir);
    }

    [Fact]
    public async Task RenameRoot_setzt_Namen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var service = fx.CreateLibraryService();
        var lib = await service.CreateAsync("Test", fx.LibraryRoot);

        await service.RenameRootAsync(lib.ArchiveRoot!.Id, "  Hauptarchiv ");

        (await service.GetRootsAsync(lib.Id)).Single().Name.Should().Be("Hauptarchiv");
    }

    [Fact]
    public async Task RemoveRoot_entfernt_Katalogzeilen_der_Wurzel_nicht_aber_die_Dateien()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var service = fx.CreateLibraryService();
        var lib = await service.CreateAsync("Test", fx.LibraryRoot);
        var inbox = await service.AddRootAsync(lib.Id, fx.InboxDir, LibraryRootRole.Inbox);
        fx.WriteLibraryFile("a\\archiv.jpg");
        var inboxFile = fx.WriteInboxFile("neu\\eingang.jpg");
        (await fx.ScanAsync(lib.Id)).FilesInserted.Should().Be(2);
        var inboxFileId = (await fx.FindFileAsync("eingang.jpg"))!.Id;
        var thumbDir = fx.Paths.GetLibraryThumbnailsPath(lib.Id);
        Directory.CreateDirectory(thumbDir);
        var thumb = Path.Combine(thumbDir, $"{inboxFileId}_320_still.jpg");
        File.WriteAllBytes(thumb, new byte[] { 1 });
        fx.Messages.Clear();

        await FluentActions.Awaiting(() => service.RemoveRootAsync(lib.ArchiveRoot!.Id))
            .Should().ThrowAsync<InvalidOperationException>("Archiv-Wurzel bleibt");

        await service.RemoveRootAsync(inbox.Id);

        File.Exists(inboxFile).Should().BeTrue("nur Katalog");
        File.Exists(thumb).Should().BeFalse("Thumbnail-Cache der entfernten Dateien weg");
        (await fx.FindFileAsync("eingang.jpg")).Should().BeNull();
        (await fx.FindFileAsync("archiv.jpg")).Should().NotBeNull();
        (await fx.GetRootsAsync(lib.Id)).Should().ContainSingle(r => r.Role == LibraryRootRole.Archive);
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.Folders.CountAsync(f => f.RootId == inbox.Id)).Should().Be(0);
        }

        fx.FtsRowIds("eingang").Should().BeEmpty();
        fx.FtsRowIds("archiv").Should().HaveCount(1);
        fx.Messages.OfKind(FilesChangeKind.Removed).Should().ContainSingle().Which.FileIds.Should().Equal(inboxFileId);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Archive;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;
using Lumora.Tests.Fixtures;
using static Lumora.Tests.Archive.ArchiveTestHelper;

namespace Lumora.Tests.Archive;

/// <summary>Archive im Katalog: Scanner nimmt sie auf, Thumbnails lassen sie aus, Entpacken bucht Papierkorb + Scan.</summary>
public sealed class ArchiveCatalogTests
{
    [Fact]
    public async Task Scanner_nimmt_Archive_als_MediaType_Archive_auf()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        fx.WriteLibraryFile(Path.Combine("a", "one.jpg"));
        WriteZip(Path.Combine(fx.LibraryRoot, "a", "set.ZIP"), ("x.jpg", Jpeg(10)));
        WriteZip(Path.Combine(fx.LibraryRoot, "b", "pack.7z"), ("y.jpg", Jpeg(10))); // Endung zählt, Inhalt egal

        var enumerator = new FolderEnumerator(fx.Formats, NullLogger<FolderEnumerator>.Instance);
        var found = enumerator.Enumerate(fx.LibraryRoot).ToList();
        found.Select(f => f.Name).Should().BeEquivalentTo(new[] { "one.jpg", "set.ZIP", "pack.7z" });
        found.Single(f => f.Name == "set.ZIP").MediaType.Should().Be(MediaType.Archive);
        found.Single(f => f.Name == "set.ZIP").Extension.Should().Be(".zip");

        var library = await fx.CreateLibraryAsync();
        var scan = await fx.ScanAsync(library.Id);
        scan.FilesInserted.Should().Be(3);

        var archive = await fx.FindFileAsync("set.ZIP");
        archive.Should().NotBeNull();
        archive!.MediaType.Should().Be(MediaType.Archive);
        fx.FtsRowIds("set").Should().Contain(archive.Id);
    }

    [Fact]
    public async Task ThumbnailService_reiht_Archive_nicht_ein()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var image = new FakeImageRenderer();
        var video = new FakeVideoRenderer();
        using var svc = new ThumbnailService(fx.ContextFactory, fx.Paths, image, video, NullLogger<ThumbnailService>.Instance);
        var ready = 0;
        svc.ThumbnailReady += _ => Interlocked.Increment(ref ready);
        var zip = WriteZip(Path.Combine(fx.LibraryRoot, "set.zip"), ("x.jpg", Jpeg(10)));

        await svc.RequestThumbnailsAsync(new[] { new ThumbnailRequest(1, 7, zip, MediaType.Archive, DateTime.UtcNow) });
        await Task.Delay(200);

        svc.PendingCount.Should().Be(0);
        image.Calls.Should().Be(0);
        video.Calls.Should().Be(0);
        ready.Should().Be(0);
        (await fx.GetThumbnailRowsAsync(7)).Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_bucht_Archiv_in_den_Papierkorb_und_scannt_die_Bibliothek_inkrementell()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        fx.WriteLibraryFile(Path.Combine("Unsortiert", "vorhanden.jpg"));
        var zip = WriteZip(Path.Combine(fx.LibraryRoot, "Unsortiert", "neu.zip"),
            ("a.jpg", Jpeg(100)), ("sub/b.png", Jpeg(100)), ("liesmich.txt", Text("x")), ("inner.zip", ZipBytes(("c.jpg", Jpeg(100)))));
        var library = await fx.CreateLibraryAsync();
        await fx.ScanAsync(library.Id);
        var archiveRow = await fx.FindFileAsync("neu.zip");
        archiveRow.Should().NotBeNull();
        var recycleBin = new FakeRecycleBin();
        var fileOps = fx.CreateFileOperationService(recycleBin);
        var svc = CreateService(CreateScanService(fx), fileOps);

        var result = await svc.ExtractAsync(zip, new ArchiveExtractOptions
        {
            LibraryId = library.Id,
            ArchiveFileId = archiveRow!.Id,
            RecycleArchiveAfterExtract = true,
        });

        result.Errors.Should().BeEmpty();
        result.FilesExtracted.Should().Be(4, "a.jpg, sub/b.png, inner.zip, c.jpg");
        result.NestedArchivesExtracted.Should().Be(1);
        result.ArchiveRecycled.Should().BeTrue();
        recycleBin.Recycled.Should().ContainSingle().Which.Should().Be(zip);
        File.Exists(zip).Should().BeFalse();
        result.Scan.Should().NotBeNull();
        result.Scan!.Mode.Should().Be(ScanMode.Incremental);
        result.Scan.Added.Should().Be(3, "a.jpg, sub\\b.png, inner\\c.jpg — liesmich.txt ist keine Mediendatei");

        await using var ctx = fx.CreateContext();
        var names = await ctx.Files.AsNoTracking().Where(f => f.LibraryId == library.Id).Select(f => f.Name).ToListAsync();
        names.Should().BeEquivalentTo(new[] { "vorhanden.jpg", "a.jpg", "b.png", "c.jpg" });
        (await fx.FindFileByIdAsync(archiveRow.Id)).Should().BeNull("der Katalog-Eintrag des Archivs ist mit dem Papierkorb weg");
        fx.Messages.OfKind(FilesChangeKind.Removed).Should().ContainSingle(m => m.FileIds.Contains(archiveRow.Id));
    }

    [Fact]
    public async Task ExtractAsync_behaelt_das_Archiv_bei_Fehlern_und_ohne_Option()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var zip = WriteZip(Path.Combine(fx.LibraryRoot, "set.zip"), ("ok.jpg", Jpeg(10)), ("../raus.jpg", Jpeg(10)));
        var library = await fx.CreateLibraryAsync();
        await fx.ScanAsync(library.Id);
        var row = await fx.FindFileAsync("set.zip");
        var recycleBin = new FakeRecycleBin();
        var svc = CreateService(CreateScanService(fx), fx.CreateFileOperationService(recycleBin));

        var withErrors = await svc.ExtractAsync(zip, new ArchiveExtractOptions { LibraryId = library.Id, ArchiveFileId = row!.Id, RecycleArchiveAfterExtract = true });
        withErrors.ArchiveRecycled.Should().BeFalse();
        withErrors.Errors.Should().Contain(e => e.Message.Contains("bleibt erhalten"));
        recycleBin.Recycled.Should().BeEmpty();
        File.Exists(zip).Should().BeTrue();
        withErrors.Scan.Should().NotBeNull("der Scan läuft trotzdem, damit die entpackten Dateien erscheinen");

        var keep = await svc.ExtractAsync(zip, new ArchiveExtractOptions { LibraryId = library.Id, ArchiveFileId = row.Id, ScanAfterExtract = false });
        keep.ArchiveRecycled.Should().BeFalse();
        keep.Scan.Should().BeNull();
        File.Exists(zip).Should().BeTrue();
    }
}

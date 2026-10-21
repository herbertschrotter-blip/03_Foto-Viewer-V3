using Microsoft.EntityFrameworkCore;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Scanning;

/// <summary>Scanner über mehrere Wurzeln (ADR-0020): Fresh und Incremental, RootId je Ordner, Offline-Guard je Wurzel.</summary>
public sealed class MultiRootScanTests
{
    [Fact]
    public async Task Fresh_Scan_erfasst_beide_Wurzeln_mit_eigener_Ordnerkette_und_RootId()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var inbox = await fx.AddInboxRootAsync(lib.Id);
        fx.WriteLibraryFile("2024\\a.jpg");
        fx.WriteLibraryFile("2024\\b.jpg");
        fx.WriteInboxFile("kamera\\c.jpg");

        var result = await fx.ScanAsync(lib.Id);

        result.FilesInserted.Should().Be(3);
        result.RootsScanned.Should().Be(2);
        result.RootPath.Should().Be(fx.LibraryRoot, "Archiv-Wurzel");
        await using var ctx = fx.CreateContext();
        var folders = await ctx.Folders.AsNoTracking().ToListAsync();
        var archiveRootId = (await fx.GetRootsAsync(lib.Id)).Single(r => r.Role == Domain.Enums.LibraryRootRole.Archive).Id;
        folders.Where(f => f.Path.StartsWith(fx.LibraryRoot)).Should().OnlyContain(f => f.RootId == archiveRootId);
        folders.Where(f => f.Path.StartsWith(fx.InboxDir)).Should().OnlyContain(f => f.RootId == inbox.Id);
        folders.Should().Contain(f => f.Path == fx.InboxDir && f.ParentId == null, "jede Wurzel hat ihren eigenen Top-Level-Ordner");
        folders.Should().Contain(f => f.Path == fx.LibraryRoot && f.ParentId == null);
        (await ctx.Files.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Incremental_Scan_diffs_je_Wurzel()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        await fx.AddInboxRootAsync(lib.Id);
        var archiveFile = fx.WriteLibraryFile("a.jpg");
        for (var i = 1; i <= 5; i++)
        {
            // genug Bestand, damit eine fehlende Datei unter dem Mass-Missing-Schwellwert (20 %) der Wurzel bleibt
            fx.WriteLibraryFile($"bestand-{i}.jpg");
        }

        fx.WriteInboxFile("b.jpg");
        (await fx.ScanAsync(lib.Id)).FilesInserted.Should().Be(7);
        var idsBefore = (await LoadIdsAsync(fx)).ToList();

        File.Delete(archiveFile);
        fx.WriteInboxFile("neu\\c.jpg");

        var result = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        result.RootsScanned.Should().Be(2);
        result.Added.Should().Be(1);
        result.MarkedMissing.Should().Be(1);
        result.FoldersAdded.Should().Be(1);
        var files = await LoadFilesAsync(fx);
        files["a.jpg"].Missing.Should().BeTrue();
        files["b.jpg"].Missing.Should().BeFalse();
        files["b.jpg"].Id.Should().Be(idsBefore.Single(i => i == files["b.jpg"].Id), "Ids bleiben stabil");
        files["c.jpg"].Id.Should().BeGreaterThan(idsBefore.Max());
    }

    [Fact]
    public async Task Incremental_ueberspringt_offline_Wurzel_und_meldet_LibraryOffline_ohne_Missing_Markierung()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var offlineDir = Path.Combine(fx.RootDir, "usb");
        Directory.CreateDirectory(offlineDir);
        await fx.AddInboxRootAsync(lib.Id, offlineDir);
        fx.WriteLibraryFile("a.jpg");
        TempLibraryFixture.WriteFile(Path.Combine(offlineDir, "u.jpg"));
        (await fx.ScanAsync(lib.Id)).FilesInserted.Should().Be(2);

        Directory.Delete(offlineDir, recursive: true); // Laufwerk weg
        fx.WriteLibraryFile("neu.jpg");

        var result = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.Incremental, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.LibraryOffline);
        result.RootPath.Should().Be(offlineDir, "die nicht erreichbare Wurzel");
        result.RootsScanned.Should().Be(1, "nur die Archiv-Wurzel wurde abgeglichen");
        result.Added.Should().Be(1, "die erreichbare Wurzel wird trotzdem aktualisiert");
        var files = await LoadFilesAsync(fx);
        files["u.jpg"].Missing.Should().BeFalse("Dateien der Offline-Wurzel werden nie als fehlend markiert");
        files.Should().ContainKey("neu.jpg");
    }

    [Fact]
    public async Task Fresh_Scan_startet_nicht_wenn_eine_Wurzel_offline_ist()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var offlineDir = Path.Combine(fx.RootDir, "usb");
        Directory.CreateDirectory(offlineDir);
        await fx.AddInboxRootAsync(lib.Id, offlineDir);
        Directory.Delete(offlineDir);
        fx.WriteLibraryFile("a.jpg");

        var result = await fx.ScanAsync(lib.Id);

        result.Outcome.Should().Be(ScanOutcome.LibraryOffline);
        result.RootPath.Should().Be(offlineDir);
        await using var ctx = fx.CreateContext();
        (await ctx.Files.CountAsync()).Should().Be(0, "kein Katalogschreiben bei Offline-Wurzel");
    }

    private static async Task<IEnumerable<int>> LoadIdsAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Files.AsNoTracking().Select(f => f.Id).ToListAsync();
    }

    private static async Task<Dictionary<string, Domain.Entities.MediaFile>> LoadFilesAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Files.AsNoTracking().ToDictionaryAsync(f => f.Name);
    }
}

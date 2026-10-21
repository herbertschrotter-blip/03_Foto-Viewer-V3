using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Albums;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Albums;

/// <summary>
/// Bibliotheks-Trennung der Alben (ADR-0054 nach ADR-0048): Alben gehören der Gruppe, zeigen und zählen nur den Bereich;
/// Verknüpfen führt gleichnamige eigene Alben zusammen (intelligente bekommen einen Zusatz), Lösen kopiert alle Alben,
/// Ordner-Verknüpfungen nur innerhalb einer Bibliothek, eine leere Gruppe nimmt ihre Alben mit.
/// </summary>
public sealed class AlbumScopingTests
{
    [Fact]
    public async Task Alben_getrennt_verknuepft_zusammengefuehrt_und_beim_Loesen_kopiert()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var libraries = w.Fx.CreateLibraryService();
        using var linking = new LibraryLinkService(w.Fx.ContextFactory, w.Catalog, NullLogger<LibraryLinkService>.Instance, w.Fx.Messenger);
        var a = w.LibraryId;
        (await w.Scope.GetForLibraryAsync(a)).GroupId.Should().NotBe(0);
        var b = (await libraries.CreateAsync("Zweite", w.Fx.OutsideDir)).Id;
        TempLibraryFixture.WriteFile(Path.Combine(w.Fx.OutsideDir, "Reise", "zweite1.jpg"));
        TempLibraryFixture.WriteFile(Path.Combine(w.Fx.OutsideDir, "Reise", "zweite2.jpg"));
        await w.Fx.CreateScanService().ScanAsync(b, ScanMode.Incremental, null, CancellationToken.None);
        var b1 = await w.FileIdAsync("zweite1.jpg");
        var b2 = await w.FileIdAsync("zweite2.jpg");
        var u1 = await w.FileIdAsync("urlaub1.jpg");

        // A: eigenes Album „Urlaub“ und intelligentes „Gute“
        await libraries.SetActiveAsync(a);
        var urlaubA = await w.Albums.CreateAsync("Urlaub");
        (await w.Albums.AddFilesAsync(urlaubA.Id, [u1, b1])).Should().Be(1, "ein Foto aus B landet nie in einem Album von A");
        await w.Albums.SaveSmartAsync("Gute", new SmartAlbumFilter { MinRating = 4 });

        // Ordner-Verknüpfung nur innerhalb einer Bibliothek
        var urlaubFolder = await w.FolderIdAsync("Urlaub");
        (await w.Links.LinkAsync(urlaubFolder, [b1])).OtherLibrary.Should().Be(1);

        // B sieht nichts aus A
        await libraries.SetActiveAsync(b);
        (await w.Albums.GetAllAsync()).Should().BeEmpty("Alben aus A sind in B unsichtbar");
        (await w.Albums.GetAsync(urlaubA.Id)).Should().BeNull();
        (await w.Albums.GetFileIdsAsync(urlaubA.Id)).Should().BeEmpty();
        var urlaubB = await w.Albums.CreateAsync("Urlaub");
        await w.Albums.AddFilesAsync(urlaubB.Id, [b1, b2]);
        var guteB = await w.Albums.CreateAsync("Gute"); // eigenes Album mit dem Namen eines intelligenten in A

        // Verknüpfen: A nimmt B auf
        await linking.LinkAsync(a, b);
        await libraries.SetActiveAsync(a);
        var all = await w.Albums.GetAllAsync();
        all.Select(x => x.Name).Should().BeEquivalentTo(["Urlaub", "Gute", "Gute (Zweite)"]);
        var merged = all.Single(x => x.Name == "Urlaub");
        merged.Id.Should().Be(urlaubA.Id, "die Zielgruppe behält ihr Album");
        merged.FileCount.Should().Be(1, "einzeln geöffnet zählt A nur seine eigenen Fotos");
        w.Scalar("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a", ("$a", urlaubA.Id)).Should().Be(3, "Fotos beider Seiten im zusammengeführten Album");
        var group = (await w.Scope.GetCurrentAsync()).GroupId;
        await w.Scope.ShowAllLinkedAsync(group);
        (await w.Albums.GetAsync(urlaubA.Id))!.FileCount.Should().Be(3, "„Alle verknüpften“ zeigt die ganze Gruppe");
        (await w.Albums.AddFilesAsync(urlaubA.Id, [b2])).Should().Be(0, "schon drin");

        // Lösen: B bekommt Kopien aller Alben, ihre Fotos wandern auf die Kopien
        await linking.UnlinkAsync(b);
        await libraries.SetActiveAsync(b);
        var inB = await w.Albums.GetAllAsync();
        inB.Select(x => x.Name).Should().BeEquivalentTo(["Urlaub", "Gute", "Gute (Zweite)"]);
        var urlaubCopy = inB.Single(x => x.Name == "Urlaub");
        urlaubCopy.Id.Should().NotBe(urlaubA.Id);
        (await w.Albums.GetFileIdsAsync(urlaubCopy.Id)).Should().BeEquivalentTo([b1, b2]);
        inB.Single(x => x.Name == "Gute").IsSmart.Should().BeTrue("intelligente Alben werden mit ihrem Filter kopiert");
        await libraries.SetActiveAsync(a);
        (await w.Albums.GetFileIdsAsync(urlaubA.Id)).Should().Equal(u1);
        w.Scalar("SELECT COUNT(*) FROM AlbumItems ai JOIN Files f ON f.Id = ai.FileId WHERE ai.AlbumId = $a AND f.LibraryId = $b",
            ("$a", urlaubA.Id), ("$b", b)).Should().Be(0, "nach dem Lösen hängt nichts aus B am Album von A");

        // B entfernen: ihre Gruppe ist leer → ihre Alben gehen mit, A bleibt unberührt
        var groupB = (await w.Scope.GetForLibraryAsync(b)).GroupId;
        await libraries.DeleteAsync(b);
        w.Scalar("SELECT COUNT(*) FROM Albums WHERE GroupId = $g", ("$g", groupB)).Should().Be(0);
        (await w.Albums.GetAllAsync()).Should().HaveCount(3);
    }
}

using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Albums;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Tests.Albums;

/// <summary>
/// Alben und Ordner-Verknüpfungen (ADR-0054): Verknüpfen/Entfernen löscht nie ein Foto, Verschieben und Umbenennen
/// erhalten Verknüpfungen, Papierkorb/Rückgängig/„Neu aufbauen“ tragen sie, intelligente Alben speichern einen Filter,
/// Export kopiert echte Dateien.
/// </summary>
public sealed class AlbumServiceTests
{
    [Fact]
    public async Task Album_anlegen_Fotos_verknuepfen_und_entfernen_loescht_nie_ein_Foto()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var u1 = await w.FileIdAsync("urlaub1.jpg");
        var u2 = await w.FileIdAsync("urlaub2.jpg");
        var f1 = await w.FileIdAsync("familie1.jpg");

        var album = await w.Albums.CreateAsync("  Sommer 2024 ");
        album.Name.Should().Be("Sommer 2024");
        album.Kind.Should().Be(AlbumKind.Manual);
        album.FileCount.Should().Be(0);
        await FluentActions.Invoking(() => w.Albums.CreateAsync("sommer 2024")).Should().ThrowAsync<InvalidOperationException>("Namen sind je Gruppe ohne Groß-/Kleinschreibung eindeutig");

        (await w.Albums.AddFilesAsync(album.Id, [u1, u2, f1, u1, 999_999])).Should().Be(3, "doppelte und unbekannte Ids zählen nicht");
        (await w.Albums.AddFilesAsync(album.Id, [u1])).Should().Be(0, "schon verknüpft");
        var info = await w.Albums.GetAsync(album.Id);
        info!.FileCount.Should().Be(3);
        info.Cover.Should().NotBeNull("ohne gewähltes Titelbild nimmt das Album das neueste Foto");
        (await w.Albums.GetFileIdsAsync(album.Id)).Should().BeEquivalentTo([u1, u2, f1]);
        (await w.Albums.GetAlbumIdsForFilesAsync([u1, await w.FileIdAsync("urlaub3.jpg")])).Should().ContainKey(u1).And.HaveCount(1);

        await w.Albums.SetCoverAsync(album.Id, f1);
        (await w.Albums.GetAsync(album.Id))!.Cover!.FileId.Should().Be(f1);

        (await w.Albums.RemoveFilesAsync(album.Id, [u2])).Should().Be(1);
        (await w.Fx.FindFileByIdAsync(u2)).Should().NotBeNull("Entfernen aus dem Album löscht nie das Foto");
        File.Exists(Path.Combine(w.Fx.LibraryRoot, "Urlaub", "urlaub2.jpg")).Should().BeTrue();

        await w.Albums.RenameAsync(album.Id, "Sommer");
        (await w.Albums.FindByNameAsync("SOMMER"))!.Id.Should().Be(album.Id);

        await w.Albums.DeleteAsync(album.Id);
        (await w.Albums.GetAllAsync()).Should().BeEmpty();
        w.Scalar("SELECT COUNT(*) FROM AlbumItems").Should().Be(0);
        w.Scalar("SELECT COUNT(*) FROM Files").Should().Be(5, "Album löschen löscht keine Fotos");
    }

    [Fact]
    public async Task Ordner_verknuepfen_zeigt_Foto_zusaetzlich_und_Entfernen_laesst_das_Foto_liegen()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var urlaub = await w.FolderIdAsync("Urlaub");
        var familie = await w.FolderIdAsync("Familie");
        var f1 = await w.FileIdAsync("familie1.jpg");
        var u1 = await w.FileIdAsync("urlaub1.jpg");

        var result = await w.Links.LinkAsync(urlaub, [f1, u1, 424242]);
        result.Should().Be(new FolderLinkResult(Linked: 1, AlreadyThere: 1, OtherLibrary: 0, Unknown: 1), "urlaub1 liegt dort schon physisch");
        (await w.Links.LinkAsync(urlaub, [f1])).AlreadyThere.Should().Be(1, "zweimal verknüpfen ändert nichts");
        (await w.Links.GetLinkedFoldersAsync(f1)).Select(x => x.FolderId).Should().Equal(urlaub);
        w.Scalar("SELECT FolderId FROM Files WHERE Id = $f", ("$f", f1)).Should().Be(familie, "die Datei bleibt, wo sie liegt");

        (await w.Links.UnlinkAsync(urlaub, [f1])).Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM FolderLinks").Should().Be(0);
        File.Exists(Path.Combine(w.Fx.LibraryRoot, "Familie", "familie1.jpg")).Should().BeTrue("Verknüpfung entfernen löscht nie das Foto");

        // Mit Unterordnern: Verknüpfung in einen Unterordner wird über den Elternordner entfernt
        w.Fx.WriteLibraryFile(Path.Combine("Urlaub", "Strand", "strand1.jpg"));
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.Incremental, null, CancellationToken.None);
        var strand = await w.FolderIdAsync(Path.Combine("Urlaub", "Strand"));
        (await w.Links.LinkAsync(strand, [f1])).Linked.Should().Be(1);
        (await w.Links.UnlinkAsync(urlaub, [f1], includeSubfolders: false)).Should().Be(0);
        (await w.Links.UnlinkAsync(urlaub, [f1], includeSubfolders: true)).Should().Be(1);
    }

    [Fact]
    public async Task Verschieben_und_Umbenennen_erhalten_alle_Verknuepfungen()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var f1 = await w.FileIdAsync("familie1.jpg");
        var urlaub = await w.FolderIdAsync("Urlaub");
        var album = await w.Albums.CreateAsync("Beste");
        await w.Albums.AddFilesAsync(album.Id, [f1]);
        await w.Links.LinkAsync(urlaub, [f1]);

        var move = await w.FileOps.MoveAsync([f1], Path.Combine(w.Fx.LibraryRoot, "Neu"));
        move.Succeeded.Should().Be(1);
        var rename = await w.FileOps.RenameAsync(f1, "familie-umbenannt.jpg");
        rename.Succeeded.Should().Be(1);
        var folderRename = await w.FileOps.RenameFolderAsync(urlaub, "Urlaub 2024");
        folderRename.Success.Should().BeTrue();

        w.Scalar("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a AND FileId = $f", ("$a", album.Id), ("$f", f1)).Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM FolderLinks WHERE FolderId = $d AND FileId = $f", ("$d", urlaub), ("$f", f1)).Should().Be(1);
        (await w.Albums.GetFileIdsAsync(album.Id)).Should().Equal(f1);
    }

    [Fact]
    public async Task Papierkorb_entfernt_Verknuepfungen_und_Rueckkehr_der_Datei_stellt_sie_wieder_her()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        w.RecycleBin.DeleteFiles = false; // Datei „liegt im Papierkorb“: wir verschieben sie selbst
        var f1 = await w.FileIdAsync("familie1.jpg");
        var path = Path.Combine(w.Fx.LibraryRoot, "Familie", "familie1.jpg");
        var urlaub = await w.FolderIdAsync("Urlaub");
        var album = await w.Albums.CreateAsync("Beste");
        await w.Albums.AddFilesAsync(album.Id, [f1]);
        await w.Links.LinkAsync(urlaub, [f1]);

        var parked = Path.Combine(w.Fx.OutsideDir, "papierkorb.jpg");
        w.RecycleBin.OnRecycle = p => File.Move(p, parked);
        (await w.FileOps.RecycleAsync([f1])).Succeeded.Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM AlbumItems").Should().Be(0, "Papierkorb entfernt die Verknüpfungen mit der Files-Zeile");
        w.Scalar("SELECT COUNT(*) FROM FolderLinks").Should().Be(0);
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo").Should().Be(2, "zur Papierkorb-Operation gesichert");
        (await w.Albums.GetAsync(album.Id))!.FileCount.Should().Be(0);

        // Windows „Wiederherstellen“: Datei wieder am alten Pfad → Scan nimmt sie neu auf → Verknüpfungen kommen zurück
        File.Move(parked, path);
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.Incremental, null, CancellationToken.None);
        var back = await w.FileIdAsync("familie1.jpg");
        await AlbumTestWorld.WaitUntilAsync(() => w.Scalar("SELECT COUNT(*) FROM AlbumItems WHERE FileId = $f", ("$f", back)) == 1, "Album-Verknüpfung zurück");
        w.Scalar("SELECT COUNT(*) FROM FolderLinks WHERE FileId = $f AND FolderId = $d", ("$f", back), ("$d", urlaub)).Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo").Should().Be(0, "die Sicherung ist verbraucht");
    }

    [Fact]
    public async Task Neuer_Import_gleichen_Namens_erbt_keine_Verknuepfungen_und_Neu_aufbauen_behaelt_die_Sicherung()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        w.RecycleBin.DeleteFiles = false;
        var f1 = await w.FileIdAsync("familie1.jpg");
        var path = Path.Combine(w.Fx.LibraryRoot, "Familie", "familie1.jpg");
        var album = await w.Albums.CreateAsync("Beste");
        await w.Albums.AddFilesAsync(album.Id, [f1]);
        await w.Links.LinkAsync(await w.FolderIdAsync("Urlaub"), [f1]);
        var parked = Path.Combine(w.Fx.OutsideDir, "papierkorb.jpg");
        w.RecycleBin.OnRecycle = p => File.Move(p, parked);
        (await w.FileOps.RecycleAsync([f1])).Succeeded.Should().Be(1);

        // Ein anderes Foto landet am selben Pfad (neuer Import): andere Größe → keine Verknüpfung
        TempLibraryFixtureWrite(path, 9999);
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.Incremental, null, CancellationToken.None);
        await Task.Delay(500);
        w.Scalar("SELECT COUNT(*) FROM AlbumItems").Should().Be(0, "ein neuer Import gleichen Namens erbt nichts");
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo").Should().Be(2, "die Sicherung bleibt für die echte Datei");

        // „Neu aufbauen“ vergibt neue Ordner-Ids — die gesicherte Ordner-Verknüpfung zieht mit
        File.Delete(path);
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);
        var newUrlaub = await w.FolderIdAsync("Urlaub");
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo WHERE Kind = 1 AND TargetId = $d", ("$d", newUrlaub)).Should().Be(1);

        // Die echte Datei kehrt zurück (Windows „Wiederherstellen“: gleiche Größe, gleiche Änderungszeit)
        File.Move(parked, path);
        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.Incremental, null, CancellationToken.None);
        var back = await w.FileIdAsync("familie1.jpg");
        await AlbumTestWorld.WaitUntilAsync(() => w.Scalar("SELECT COUNT(*) FROM FolderLinks WHERE FileId = $f AND FolderId = $d", ("$f", back), ("$d", newUrlaub)) == 1, "Ordner-Verknüpfung zurück");
        w.Scalar("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a AND FileId = $f", ("$a", album.Id), ("$f", back)).Should().Be(1);

        // Album löschen verwirft auch seine Sicherungen
        await w.Albums.DeleteAsync(album.Id);
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo WHERE Kind = 0").Should().Be(0);

        static void TempLibraryFixtureWrite(string file, int size) => Fixtures.TempLibraryFixture.WriteFile(file, size);
    }

    [Fact]
    public async Task Aus_der_Bibliothek_verschieben_und_Rueckgaengig_stellt_Verknuepfungen_wieder_her()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var f2 = await w.FileIdAsync("familie2.jpg");
        var urlaub = await w.FolderIdAsync("Urlaub");
        var album = await w.Albums.CreateAsync("Beste");
        await w.Albums.AddFilesAsync(album.Id, [f2]);
        await w.Links.LinkAsync(urlaub, [f2]);

        (await w.FileOps.MoveAsync([f2], w.Fx.OutsideDir)).Succeeded.Should().Be(1);
        (await w.Fx.FindFileByIdAsync(f2)).Should().BeNull("außerhalb der Bibliothek kein Katalogeintrag");
        w.Scalar("SELECT COUNT(*) FROM AlbumItems").Should().Be(0);

        var undo = await w.FileOps.UndoLastAsync();
        undo!.Succeeded.Should().Be(1);
        var back = await w.FileIdAsync("familie2.jpg");
        w.Scalar("SELECT COUNT(*) FROM AlbumItems WHERE AlbumId = $a AND FileId = $f", ("$a", album.Id), ("$f", back)).Should().Be(1, "Rückgängig stellt die Album-Verknüpfung her");
        w.Scalar("SELECT COUNT(*) FROM FolderLinks WHERE FolderId = $d AND FileId = $f", ("$d", urlaub), ("$f", back)).Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM FileLinkUndo").Should().Be(0);
    }

    [Fact]
    public async Task Neu_aufbauen_traegt_Alben_Ordner_Verknuepfungen_und_Titelbild_auf_die_neuen_Ids()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var f1 = await w.FileIdAsync("familie1.jpg");
        var u3 = await w.FileIdAsync("urlaub3.jpg");
        var album = await w.Albums.CreateAsync("Beste");
        await w.Albums.AddFilesAsync(album.Id, [f1, u3]);
        await w.Albums.SetCoverAsync(album.Id, u3);
        await w.Links.LinkAsync(await w.FolderIdAsync("Urlaub"), [f1]);

        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var newF1 = await w.FileIdAsync("familie1.jpg");
        var newU3 = await w.FileIdAsync("urlaub3.jpg");
        var newUrlaub = await w.FolderIdAsync("Urlaub");
        (await w.Albums.GetFileIdsAsync(album.Id)).Should().BeEquivalentTo([newF1, newU3]);
        (await w.Albums.GetAsync(album.Id))!.Cover!.FileId.Should().Be(newU3);
        w.Scalar("SELECT COUNT(*) FROM FolderLinks WHERE FolderId = $d AND FileId = $f", ("$d", newUrlaub), ("$f", newF1)).Should().Be(1);
        w.Scalar("SELECT COUNT(*) FROM AlbumItems WHERE FileId < 0").Should().Be(0);
    }

    [Fact]
    public async Task Intelligentes_Album_speichert_den_Filter_und_wertet_ihn_ueber_den_Auswerter_aus()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var u1 = await w.FileIdAsync("urlaub1.jpg");
        var u2 = await w.FileIdAsync("urlaub2.jpg");
        w.Evaluator.Ids = [u2, u1];
        var filter = new SmartAlbumFilter
        {
            Media = "Images",
            MinRating = 3,
            Tags = ["Urlaub", "Strand"],
            TagMode = "All",
            Categories = [new SmartAlbumCategory("Projekt", ["Nord"], IncludeNone: true)],
            FolderPath = Path.Combine(w.Fx.LibraryRoot, "Urlaub"),
            IncludeSubfolders = true,
        };

        await FluentActions.Invoking(() => w.Albums.SaveSmartAsync("Leer", new SmartAlbumFilter())).Should().ThrowAsync<ArgumentException>("ein leerer Filter zeigte alles");
        var smart = await w.Albums.SaveSmartAsync("Gute Urlaubsbilder", filter);
        smart.Kind.Should().Be(AlbumKind.Smart);
        smart.FileCount.Should().Be(2);
        smart.Cover!.FileId.Should().Be(u2);
        (await w.Albums.GetFileIdsAsync(smart.Id)).Should().Equal(u2, u1);
        w.Evaluator.LastFilter!.ToJson().Should().Be(filter.ToJson(), "der gespeicherte Filter kommt unverändert zurück");
        smart.Filter!.Describe().Should().Contain(["Bilder", "ab 3 Sternen", "Tag „Urlaub“ und „Strand“", "Projekt: „Nord“ oder ohne Wert"]);

        await FluentActions.Invoking(() => w.Albums.SaveSmartAsync("gute urlaubsbilder", filter with { MinRating = 4 }))
            .Should().ThrowAsync<InvalidOperationException>("ohne „ersetzen“ kein Überschreiben");
        var replaced = await w.Albums.SaveSmartAsync("Gute Urlaubsbilder", filter with { MinRating = 4 }, replaceExisting: true);
        replaced.Id.Should().Be(smart.Id);
        replaced.Filter!.MinRating.Should().Be(4);
        await FluentActions.Invoking(() => w.Albums.AddFilesAsync(smart.Id, [u1])).Should().ThrowAsync<InvalidOperationException>();

        await w.Albums.CreateAsync("Eigenes");
        await FluentActions.Invoking(() => w.Albums.SaveSmartAsync("Eigenes", filter, replaceExisting: true))
            .Should().ThrowAsync<InvalidOperationException>("ein eigenes Album wird nie überschrieben");

        SmartAlbumFilter.FromJson(filter.ToJson()).Should().BeEquivalentTo(filter);
        SmartAlbumFilter.FromJson("kein json").Should().BeNull();
    }

    [Fact]
    public async Task Export_kopiert_echte_Dateien_umbenennt_bei_Gleichnamigen_und_ist_in_versteckter_Bibliothek_gesperrt()
    {
        using var w = await AlbumTestWorld.CreateAsync();
        var album = await w.Albums.CreateAsync("Export");
        var ids = new[] { await w.FileIdAsync("urlaub1.jpg"), await w.FileIdAsync("urlaub2.jpg"), await w.FileIdAsync("familie1.jpg") };
        await w.Albums.AddFilesAsync(album.Id, ids);
        var target = Path.Combine(w.Fx.RootDir, "export");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "urlaub1.jpg"), "schon da");
        File.Delete(Path.Combine(w.Fx.LibraryRoot, "Familie", "familie1.jpg")); // Quelle fehlt

        var reports = new List<AlbumExportProgress>();
        var result = await w.Albums.ExportAsync(album.Id, target, new Progress<AlbumExportProgress>(reports.Add));
        result.Copied.Should().Be(2);
        result.Renamed.Should().Be(1);
        result.Missing.Should().Be(1);
        result.Canceled.Should().BeFalse();
        File.ReadAllText(Path.Combine(target, "urlaub1.jpg")).Should().Be("schon da", "nichts wird überschrieben");
        new FileInfo(Path.Combine(target, "urlaub1 (2).jpg")).Length.Should().Be(new FileInfo(Path.Combine(w.Fx.LibraryRoot, "Urlaub", "urlaub1.jpg")).Length);
        File.Exists(Path.Combine(w.Fx.LibraryRoot, "Urlaub", "urlaub2.jpg")).Should().BeTrue("Export kopiert, verschiebt nicht");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        (await w.Albums.ExportAsync(album.Id, Path.Combine(w.Fx.RootDir, "export2"), null, cts.Token)).Canceled.Should().BeTrue();

        w.Catalog.SetHidden(true);
        await FluentActions.Invoking(() => w.Albums.ExportAsync(album.Id, target)).Should().ThrowAsync<InvalidOperationException>();
    }
}

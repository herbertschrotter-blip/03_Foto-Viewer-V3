using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Operations;

/// <summary>
/// Regressionstests aus dem Review-Härtungslauf (Welle 2): Abbruch-/Statuskonsistenz, Undo nach
/// Rescan bzw. Id-Wiederverwendung, gemischte Ordnerbäume, Pfad-Sonderfälle, Konflikte und
/// gleichzeitige Schreiber.
/// </summary>
public sealed class FileOperationHardeningTests
{
    private sealed record Scanned(Library Library, MediaFile Alpha, MediaFile Bravo);

    private static async Task<Scanned> ArrangeAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("a", "bravo.jpg"), size: 4096);
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;
        var bravo = (await fx.FindFileAsync("bravo.jpg"))!;
        return new Scanned(library, alpha, bravo);
    }

    private static string SourcePath(TempLibraryFixture fx, string name) => Path.Combine(fx.LibraryRoot, "a", name);

    private static void RebuildFts(TempLibraryFixture fx)
    {
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO FilesFts(FilesFts) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
    }

    private static MediaFile CloneRow(MediaFile source, int id, int folderId, string name) => new()
    {
        Id = id,
        LibraryId = source.LibraryId,
        FolderId = folderId,
        Name = name,
        Extension = Path.GetExtension(name).ToLowerInvariant(),
        Size = source.Size,
        CreatedAtUtc = source.CreatedAtUtc,
        ModifiedAtUtc = source.ModifiedAtUtc,
        MediaType = source.MediaType,
    };

    // ---------------------------------------------------------------- Abbruch / Status

    [Fact]
    public async Task Abbruch_waehrend_des_Dateisystem_Schritts_bucht_erledigtes_Item_als_Applied_und_meldet_es()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var cts = new CancellationTokenSource();
        var bin = new FakeRecycleBin { OnRecycle = _ => cts.Cancel() }; // Abbruch kommt, während alpha schon im Papierkorb landet
        using var svc = fx.CreateFileOperationService(bin);

        var act = () => svc.RecycleAsync(new[] { s.Alpha.Id, s.Bravo.Id }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        bin.Recycled.Should().Equal(SourcePath(fx, "alpha.jpg"));
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeFalse();
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeTrue();

        // Katalog spiegelt das Dateisystem: alpha weg, bravo unangetastet.
        (await fx.FindFileByIdAsync(s.Alpha.Id)).Should().BeNull();
        (await fx.FindFileByIdAsync(s.Bravo.Id)).Should().NotBeNull();
        fx.FtsRowIds("alpha").Should().BeEmpty();
        fx.FtsRowIds("bravo").Should().Equal(s.Bravo.Id);

        var batch = (await svc.GetRecentBatchesAsync()).Should().ContainSingle().Subject;
        batch.Status.Should().Be(BatchStatus.Applied);
        batch.CompletedAtUtc.Should().NotBeNull();
        var ops = await fx.GetOperationsAsync(batch.Id);
        ops.Single(o => o.FileId == s.Alpha.Id).Status.Should().Be(FileOperationStatus.Applied);
        var open = ops.Single(o => o.FileId == s.Bravo.Id);
        open.Status.Should().Be(FileOperationStatus.Failed);
        open.Error.Should().Contain("Abgebrochen");

        // Die UI erfährt vom bereits entfernten alpha — trotz Abbruch.
        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Removed);
        message.FileIds.Should().Equal(s.Alpha.Id);
    }

    [Fact]
    public async Task Item_steht_waehrend_des_Dateisystem_Schritts_auf_Applying()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        FileOperationStatus? observed = null;
        var bin = new FakeRecycleBin
        {
            OnRecycle = path =>
            {
                using var ctx = fx.CreateContext();
                observed = ctx.FileOperations.AsNoTracking().Single(o => o.SourcePath == path).Status;
            },
        };
        using var svc = fx.CreateFileOperationService(bin);

        var result = await svc.RecycleAsync(new[] { s.Alpha.Id });

        observed.Should().Be(FileOperationStatus.Applying, "der Crash-Marker muss vor dem Dateisystem-Schritt persistiert sein");
        result.Succeeded.Should().Be(1);
        (await fx.GetOperationsAsync(result.BatchId)).Single().Status.Should().Be(FileOperationStatus.Applied);
    }

    [Fact]
    public async Task OperationCanceledException_aus_dem_Papierkorb_bricht_den_Batch_nicht_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        var calls = 0;
        var bin = new FakeRecycleBin();
        bin.OnRecycle = _ =>
        {
            // Erste Datei: Shell meldet Benutzer-Abbruch (VB/Shell werfen OperationCanceledException).
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new OperationCanceledException("Vorgang von der Shell abgebrochen");
            }
        };
        using var svc = fx.CreateFileOperationService(bin);

        var result = await svc.RecycleAsync(new[] { s.Alpha.Id, s.Bravo.Id });

        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Items.Single(i => i.FileId == s.Alpha.Id).Error.Should().Contain("Papierkorb");
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeFalse();
        (await fx.FindFileByIdAsync(s.Alpha.Id)).Should().NotBeNull();
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Applied);
    }

    // ---------------------------------------------------------------- Undo nach Rescan / Id-Wiederverwendung

    [Fact]
    public async Task Undo_eines_Moves_nach_aussen_faesst_eine_fremde_Datei_mit_wiederverwendeter_Id_nicht_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var moved = await svc.MoveAsync(new[] { s.Bravo.Id }, fx.OutsideDir);
        moved.Succeeded.Should().Be(1);

        // Simulierter Rescan: eine neue Datei bekommt die frei gewordene Id (Scanner vergibt Ids explizit ab 1).
        fx.WriteLibraryFile(Path.Combine("a", "zulu.jpg"), size: 777);
        await using (var ctx = fx.CreateContext())
        {
            ctx.Files.Add(CloneRow(s.Bravo, s.Bravo.Id, s.Bravo.FolderId, "zulu.jpg"));
            await ctx.SaveChangesAsync();
        }

        RebuildFts(fx);
        fx.Messages.Clear();

        var undo = await svc.UndoBatchAsync(moved.BatchId);

        undo!.Succeeded.Should().Be(1);
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeTrue();
        File.Exists(SourcePath(fx, "zulu.jpg")).Should().BeTrue();

        var intruder = (await fx.FindFileByIdAsync(s.Bravo.Id))!;
        intruder.Name.Should().Be("zulu.jpg", "die fremde Zeile mit der alten Id darf nicht umgebogen werden");
        intruder.FolderId.Should().Be(s.Bravo.FolderId);

        var restored = (await fx.FindFileAsync("bravo.jpg"))!;
        restored.Id.Should().NotBe(s.Bravo.Id);
        restored.FolderId.Should().Be(s.Bravo.FolderId);
        fx.FtsRowIds("bravo").Should().Equal(restored.Id);
        fx.FtsRowIds("zulu").Should().Equal(s.Bravo.Id);
        fx.Messages.OfKind(FilesChangeKind.Added).Should().ContainSingle().Which.FileIds.Should().Equal(restored.Id);
    }

    [Fact]
    public async Task Undo_eines_Moves_innerhalb_findet_die_Datei_nach_Rescan_ueber_den_Pfad_statt_ueber_die_alte_Id()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var target = Path.Combine(fx.LibraryRoot, "b");
        var moved = await svc.MoveAsync(new[] { s.Alpha.Id }, target);
        moved.Succeeded.Should().Be(1);
        var folderB = (await fx.FindFolderAsync(target))!;

        // Simulierter ClearAndRescan: alle Files-Zeilen neu — alpha bekommt Id 50, bravo erbt alphas alte Id.
        const int alphaNewId = 50;
        await using (var ctx = fx.CreateContext())
        {
            var rows = await ctx.Files.Where(f => f.Id == s.Alpha.Id || f.Id == s.Bravo.Id).ToListAsync();
            ctx.Files.RemoveRange(rows);
            await ctx.SaveChangesAsync();
            ctx.Files.Add(CloneRow(s.Alpha, alphaNewId, folderB.Id, "alpha.jpg"));
            ctx.Files.Add(CloneRow(s.Bravo, s.Alpha.Id, s.Bravo.FolderId, "bravo.jpg"));
            await ctx.SaveChangesAsync();
        }

        RebuildFts(fx);
        fx.Messages.Clear();

        var undo = await svc.UndoBatchAsync(moved.BatchId);

        undo!.Succeeded.Should().Be(1);
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "alpha.jpg")).Should().BeFalse();

        var alpha = (await fx.FindFileByIdAsync(alphaNewId))!;
        alpha.Name.Should().Be("alpha.jpg");
        alpha.FolderId.Should().Be(s.Alpha.FolderId, "die Zeile am Zielpfad gehört zurück in den Ursprungsordner");

        var bravo = (await fx.FindFileByIdAsync(s.Alpha.Id))!;
        bravo.Name.Should().Be("bravo.jpg", "die Zeile mit der protokollierten FileId gehört inzwischen einer anderen Datei");
        bravo.FolderId.Should().Be(s.Bravo.FolderId);

        fx.FtsRowIds("alpha").Should().Equal(alphaNewId);
        fx.FtsRowIds("bravo").Should().Equal(s.Alpha.Id);
        fx.Messages.OfKind(FilesChangeKind.Moved).Should().ContainSingle().Which.FileIds.Should().Equal(alphaNewId);
        (await fx.GetBatchAsync(moved.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    // ---------------------------------------------------------------- Ordnerbaum

    [Fact]
    public async Task Move_in_neuen_Zwischenordner_haengt_verwaiste_Scanner_Ordner_an_die_nachgetragene_Kette()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        // Altkatalog nachstellen: Der V0.1-Scanner legte nur Ordner MIT Dateien an (Root und „2024“ fehlten,
        // die Blätter hatten ParentId NULL). Der heutige Scanner schreibt die volle Kette, daher wird der
        // Alt-Zustand hier explizit hergestellt — die Reparaturlogik muss solche Kataloge weiterhin heilen.
        fx.WriteLibraryFile(Path.Combine("2024", "01", "a.jpg"));
        fx.WriteLibraryFile(Path.Combine("2024", "02_alt", "b.jpg"));
        await fx.ScanAsync(library.Id);
        var path01 = Path.Combine(fx.LibraryRoot, "2024", "01");
        var path02 = Path.Combine(fx.LibraryRoot, "2024", "02_alt");
        var path2024 = Path.Combine(fx.LibraryRoot, "2024");
        await using (var setup = fx.CreateContext())
        {
            var rows = await setup.Folders.Where(f => f.LibraryId == library.Id).ToListAsync();
            foreach (var r in rows.Where(r => r.Path.Equals(path01, StringComparison.OrdinalIgnoreCase)
                                           || r.Path.Equals(path02, StringComparison.OrdinalIgnoreCase)))
            {
                r.ParentId = null;
            }

            setup.Folders.RemoveRange(rows.Where(r => r.Path.Equals(fx.LibraryRoot, StringComparison.OrdinalIgnoreCase)
                                                   || r.Path.Equals(path2024, StringComparison.OrdinalIgnoreCase)));
            await setup.SaveChangesAsync();
        }

        var leaf01 = (await fx.FindFolderAsync(path01))!;
        var leaf02 = (await fx.FindFolderAsync(path02))!;
        leaf01.ParentId.Should().BeNull("Vorbedingung: Altkatalog ohne Elternordner");
        leaf02.ParentId.Should().BeNull();
        (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "2024"))).Should().BeNull();
        var a = (await fx.FindFileAsync("a.jpg"))!;
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { a.Id }, Path.Combine(fx.LibraryRoot, "2024", "03"));

        result.Succeeded.Should().Be(1);
        var root = (await fx.FindFolderAsync(fx.LibraryRoot))!;
        var y2024 = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "2024")))!;
        var f03 = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "2024", "03")))!;
        root.ParentId.Should().BeNull();
        y2024.ParentId.Should().Be(root.Id);
        f03.ParentId.Should().Be(y2024.Id);
        (await fx.FindFolderAsync(leaf01.Path))!.ParentId.Should().Be(y2024.Id, "verwaiste direkte Kinder werden an den nachgetragenen Elternordner gehängt");
        (await fx.FindFolderAsync(leaf02.Path))!.ParentId.Should().Be(y2024.Id);
        (await fx.FindFileByIdAsync(a.Id))!.FolderId.Should().Be(f03.Id);

        await using var ctx = fx.CreateContext();
        (await ctx.Folders.CountAsync(f => f.LibraryId == library.Id)).Should().Be(5);
    }

    [Fact]
    public async Task Move_Ziel_mit_anderer_Schreibweise_und_Trennzeichen_am_Ende_nutzt_bestehende_Ordnerzeile()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("b", "bravo.jpg"));
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;
        var bravo = (await fx.FindFileAsync("bravo.jpg"))!;
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { alpha.Id }, Path.Combine(fx.LibraryRoot, "B") + Path.DirectorySeparatorChar);

        result.Succeeded.Should().Be(1);
        result.Items[0].DestPath.Should().Be(Path.Combine(fx.LibraryRoot, "B", "alpha.jpg"));
        File.Exists(Path.Combine(fx.LibraryRoot, "b", "alpha.jpg")).Should().BeTrue();
        (await fx.FindFileByIdAsync(alpha.Id))!.FolderId.Should().Be(bravo.FolderId);
        await using var ctx = fx.CreateContext();
        (await ctx.Folders.CountAsync(f => f.Path.ToLower() == Path.Combine(fx.LibraryRoot, "b").ToLower())).Should().Be(1);
    }

    // ---------------------------------------------------------------- Pfade / Namen

    [Fact]
    public async Task Nicht_ASCII_Namen_ueberleben_Move_Rename_FTS_und_Undo()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("Фото", "Ärger ✨.jpg"));
        await fx.ScanAsync(library.Id);
        var file = (await fx.FindFileAsync("Ärger ✨.jpg"))!;
        var originalFolderId = file.FolderId;
        using var svc = fx.CreateFileOperationService();
        var target = Path.Combine(fx.LibraryRoot, "Ziel 🎉", "unter");

        var moved = await svc.MoveAsync(new[] { file.Id }, target);
        moved.Succeeded.Should().Be(1, moved.Items[0].Error);
        File.Exists(Path.Combine(target, "Ärger ✨.jpg")).Should().BeTrue();
        (await fx.FindFileByIdAsync(file.Id))!.FolderId.Should().Be((await fx.FindFolderAsync(target))!.Id);
        fx.FtsRowIds("Ärger").Should().Equal(file.Id);

        var renamed = await svc.RenameAsync(file.Id, "Straße ✨ neu.JPG");
        renamed.Succeeded.Should().Be(1, renamed.Items[0].Error);
        File.Exists(Path.Combine(target, "Straße ✨ neu.JPG")).Should().BeTrue();
        (await fx.FindFileByIdAsync(file.Id))!.Name.Should().Be("Straße ✨ neu.JPG");
        fx.FtsRowIds("Straße").Should().Equal(file.Id);
        fx.FtsRowIds("Ärger").Should().BeEmpty();

        (await svc.UndoLastAsync())!.Succeeded.Should().Be(1);
        (await fx.FindFileByIdAsync(file.Id))!.Name.Should().Be("Ärger ✨.jpg");
        fx.FtsRowIds("Ärger").Should().Equal(file.Id);

        (await svc.UndoLastAsync())!.Succeeded.Should().Be(1);
        File.Exists(Path.Combine(fx.LibraryRoot, "Фото", "Ärger ✨.jpg")).Should().BeTrue();
        (await fx.FindFileByIdAsync(file.Id))!.FolderId.Should().Be(originalFolderId);
    }

    [Theory]
    [InlineData("CON.jpg")]
    [InlineData("con")]
    [InlineData("COM1.png")]
    [InlineData("lpt9.jpg")]
    [InlineData("NUL.mp4")]
    public async Task Rename_auf_reservierten_Geraetenamen_wirft(string newName)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();

        var act = () => svc.RenameAsync(s.Alpha.Id, newName);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*reservierter*");
        (await svc.GetRecentBatchesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Rename_auf_Namen_die_nur_wie_Geraetenamen_aussehen_ist_erlaubt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();

        (await svc.RenameAsync(s.Alpha.Id, "console.jpg")).Succeeded.Should().Be(1);
        (await svc.RenameAsync(s.Bravo.Id, "COM10.jpg")).Succeeded.Should().Be(1);
    }

    [Fact]
    public async Task Sehr_langer_Zielpfad_wird_verschoben_oder_sauber_als_Fehler_gemeldet()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var deep = Path.Combine(fx.LibraryRoot, new string('x', 200), new string('y', 120));
        deep.Length.Should().BeGreaterThan(260);

        var result = await svc.MoveAsync(new[] { s.Alpha.Id }, deep);

        // .NET setzt intern das \\?\-Präfix: auf aktuellen Windows-Versionen klappt der Move; sonst
        // darf höchstens ein Failed-Item mit Fehlertext entstehen — nie eine Ausnahme.
        if (result.Succeeded == 1)
        {
            File.Exists(Path.Combine(deep, "alpha.jpg")).Should().BeTrue();
            (await fx.FindFileByIdAsync(s.Alpha.Id))!.FolderId.Should().Be((await fx.FindFolderAsync(deep))!.Id);
            (await svc.UndoLastAsync())!.Succeeded.Should().Be(1);
            File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        }
        else
        {
            result.Items[0].Error.Should().NotBeNullOrEmpty();
            File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
            (await fx.FindFileByIdAsync(s.Alpha.Id))!.FolderId.Should().Be(s.Alpha.FolderId);
        }
    }

    // ---------------------------------------------------------------- Konflikte

    [Fact]
    public async Task Gesperrte_Quelldatei_ergibt_Failed_Item_ohne_Ausnahme_bei_Move_und_Rename()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        using var handle = new FileStream(SourcePath(fx, "alpha.jpg"), FileMode.Open, FileAccess.Read, FileShare.None);

        var move = await svc.MoveAsync(new[] { s.Alpha.Id, s.Bravo.Id }, Path.Combine(fx.LibraryRoot, "b"));
        var rename = await svc.RenameAsync(s.Alpha.Id, "alpha2.jpg");

        move.Failed.Should().Be(1);
        move.Succeeded.Should().Be(1);
        move.Items.Single(i => i.FileId == s.Alpha.Id).Error.Should().Contain("Verschieben fehlgeschlagen");
        rename.Failed.Should().Be(1);
        rename.Items[0].Error.Should().Contain("Umbenennen fehlgeschlagen");
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        var alpha = (await fx.FindFileByIdAsync(s.Alpha.Id))!;
        alpha.FolderId.Should().Be(s.Alpha.FolderId);
        alpha.Name.Should().Be("alpha.jpg");
        (await fx.GetOperationsAsync(move.BatchId)).Single(o => o.FileId == s.Alpha.Id).Status.Should().Be(FileOperationStatus.Failed);
    }

    // ---------------------------------------------------------------- Gleichzeitige Schreiber

    [Fact]
    public async Task Gleichzeitige_Move_Metadaten_und_Thumbnail_Schreiber_laufen_ohne_Ausnahmen_durch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        const int count = 60;
        for (var i = 0; i < count; i++)
        {
            fx.WriteLibraryFile(Path.Combine("a", $"f{i:D3}.jpg"), size: 1024 + i);
        }

        await fx.ScanAsync(library.Id);
        List<MediaFile> files;
        await using (var ctx = fx.CreateContext())
        {
            files = await ctx.Files.AsNoTracking().OrderBy(f => f.Id).ToListAsync();
        }

        files.Should().HaveCount(count);
        var ids = files.Select(f => f.Id).ToList();
        var toMove = ids.Take(30).ToList();
        var toFavorite = ids.Skip(15).Take(30).ToList();
        var toRate = ids.Skip(40).ToList();
        var requests = files
            .Select(f => new ThumbnailRequest(library.Id, f.Id, Path.Combine(fx.LibraryRoot, "a", f.Name), MediaType.Image, f.ModifiedAtUtc))
            .ToList();

        using var ops = fx.CreateFileOperationService();
        using var catalog = fx.CreateCatalogUpdateService();
        using var thumbs = new ThumbnailService(fx.ContextFactory, fx.Paths, new FakeImageRenderer(), new FakeVideoRenderer(), NullLogger<ThumbnailService>.Instance);

        await Task.WhenAll(
            Task.Run(() => ops.MoveAsync(toMove, Path.Combine(fx.LibraryRoot, "b"))),
            Task.Run(() => catalog.SetFavoriteAsync(toFavorite, true)),
            Task.Run(() => catalog.SetRatingAsync(toRate, 3)),
            Task.Run(() => thumbs.RequestThumbnailsAsync(requests)));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (thumbs.PendingCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        thumbs.PendingCount.Should().Be(0);

        await using var verify = fx.CreateContext();
        var folderB = await verify.Folders.SingleAsync(f => f.Path == Path.Combine(fx.LibraryRoot, "b"));
        (await verify.Files.CountAsync(f => toMove.Contains(f.Id) && f.FolderId == folderB.Id)).Should().Be(toMove.Count);
        (await verify.Files.CountAsync(f => toFavorite.Contains(f.Id) && f.IsFavorite)).Should().Be(toFavorite.Count);
        (await verify.Files.CountAsync(f => toRate.Contains(f.Id) && f.Rating == 3)).Should().Be(toRate.Count);
        (await verify.Files.CountAsync(f => f.IsFavorite)).Should().Be(toFavorite.Count);
        (await verify.Thumbnails.CountAsync()).Should().Be(count, "jede Datei hat eine Thumbnail-Zeile (Generated oder source_missing nach dem Move)");
        (await verify.Thumbnails.CountAsync(t => t.Status == ThumbnailStatus.Pending)).Should().Be(0);
        foreach (var id in toMove)
        {
            File.Exists(Path.Combine(fx.LibraryRoot, "b", files.Single(f => f.Id == id).Name)).Should().BeTrue();
        }

        fx.Messages.OfKind(FilesChangeKind.Moved).Should().ContainSingle().Which.FileIds.Should().BeEquivalentTo(toMove);
        fx.Messages.OfKind(FilesChangeKind.Metadata).Should().HaveCount(2);
    }
}

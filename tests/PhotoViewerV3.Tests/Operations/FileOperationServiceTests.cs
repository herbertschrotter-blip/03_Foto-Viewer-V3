using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Operations;

public sealed class FileOperationServiceTests
{
    private sealed record Scanned(Library Library, MediaFile Alpha, MediaFile Bravo);

    /// <summary>Library mit a\alpha.jpg und a\bravo.jpg, frisch gescannt.</summary>
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

    // ---------------------------------------------------------------- Move

    [Fact]
    public async Task Move_innerhalb_der_Library_verschiebt_Datei_und_aktualisiert_Katalog()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var target = Path.Combine(fx.LibraryRoot, "b", "c");

        var result = await svc.MoveAsync(new[] { s.Alpha.Id }, target);

        result.Type.Should().Be(OperationType.Move);
        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(0);
        var item = result.Items.Should().ContainSingle().Subject;
        item.Success.Should().BeTrue();
        item.SourcePath.Should().Be(SourcePath(fx, "alpha.jpg"));
        item.DestPath.Should().Be(Path.Combine(target, "alpha.jpg"));

        File.Exists(Path.Combine(target, "alpha.jpg")).Should().BeTrue();
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeFalse();

        // Folders-Kette: Root → b → c
        var root = await fx.FindFolderAsync(fx.LibraryRoot);
        var b = await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "b"));
        var c = await fx.FindFolderAsync(target);
        root.Should().NotBeNull();
        b.Should().NotBeNull();
        c.Should().NotBeNull();
        root!.ParentId.Should().BeNull();
        b!.ParentId.Should().Be(root.Id);
        b.Name.Should().Be("b");
        c!.ParentId.Should().Be(b.Id);
        c.Name.Should().Be("c");

        var moved = (await fx.FindFileByIdAsync(s.Alpha.Id))!;
        moved.FolderId.Should().Be(c.Id);
        moved.FolderId.Should().NotBe(s.Alpha.FolderId);
        moved.Name.Should().Be("alpha.jpg");
        fx.FtsRowIds("alpha").Should().Contain(s.Alpha.Id);

        var batch = (await fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.Move);
        batch.Status.Should().Be(BatchStatus.Applied);
        batch.CompletedAtUtc.Should().NotBeNull();
        batch.Description.Should().Contain("1 Datei");
        var op = (await fx.GetOperationsAsync(result.BatchId)).Should().ContainSingle().Subject;
        op.Status.Should().Be(FileOperationStatus.Applied);
        op.Action.Should().Be(OperationType.Move);
        op.FileId.Should().Be(s.Alpha.Id);
        op.ExecutedAtUtc.Should().NotBeNull();

        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Moved);
        message.LibraryId.Should().Be(s.Library.Id);
        message.FileIds.Should().Equal(s.Alpha.Id);
    }

    [Fact]
    public async Task Move_in_bereits_gescannten_Ordner_nutzt_bestehende_Folders_Zeile()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine("a", "alpha.jpg"));
        fx.WriteLibraryFile(Path.Combine("z", "zulu.jpg"));
        await fx.ScanAsync(library.Id);
        var alpha = (await fx.FindFileAsync("alpha.jpg"))!;
        var zulu = (await fx.FindFileAsync("zulu.jpg"))!;
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { alpha.Id }, Path.Combine(fx.LibraryRoot, "z"));

        result.Succeeded.Should().Be(1);
        (await fx.FindFileByIdAsync(alpha.Id))!.FolderId.Should().Be(zulu.FolderId);
        await using var ctx = fx.CreateContext();
        (await ctx.Folders.CountAsync(f => f.Path == Path.Combine(fx.LibraryRoot, "z"))).Should().Be(1);
    }

    [Fact]
    public async Task Move_ausserhalb_der_Library_entfernt_Katalogeintrag_Thumbnails_und_FTS()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        var thumbPath = await SeedThumbnailAsync(fx, s.Library.Id, s.Bravo.Id);
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { s.Bravo.Id }, fx.OutsideDir);

        result.Succeeded.Should().Be(1);
        File.Exists(Path.Combine(fx.OutsideDir, "bravo.jpg")).Should().BeTrue();
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeFalse();
        (await fx.FindFileByIdAsync(s.Bravo.Id)).Should().BeNull();
        (await fx.FindFileByIdAsync(s.Alpha.Id)).Should().NotBeNull();
        fx.FtsRowIds("bravo").Should().BeEmpty();
        fx.FtsRowIds("alpha").Should().Contain(s.Alpha.Id);
        (await fx.GetThumbnailRowsAsync(s.Bravo.Id)).Should().BeEmpty();
        File.Exists(thumbPath).Should().BeFalse();

        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Removed);
        message.FileIds.Should().Equal(s.Bravo.Id);
    }

    [Fact]
    public async Task Move_bei_Zielkonflikt_markiert_Item_als_Failed_und_laesst_Datei_liegen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        var target = Path.Combine(fx.LibraryRoot, "b");
        TempLibraryFixture.WriteFile(Path.Combine(target, "alpha.jpg"), 10);
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { s.Alpha.Id }, target);

        result.Succeeded.Should().Be(0);
        result.Failed.Should().Be(1);
        var item = result.Items.Should().ContainSingle().Subject;
        item.Success.Should().BeFalse();
        item.Error.Should().Contain("existiert bereits");
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        new FileInfo(Path.Combine(target, "alpha.jpg")).Length.Should().Be(10);
        (await fx.FindFileByIdAsync(s.Alpha.Id))!.FolderId.Should().Be(s.Alpha.FolderId);

        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Failed);
        var op = (await fx.GetOperationsAsync(result.BatchId)).Should().ContainSingle().Subject;
        op.Status.Should().Be(FileOperationStatus.Failed);
        op.Error.Should().Contain("existiert bereits");
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Move_mit_Teilerfolg_liefert_Batch_Applied_und_Items_gemischt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        var target = Path.Combine(fx.LibraryRoot, "b");
        TempLibraryFixture.WriteFile(Path.Combine(target, "alpha.jpg"), 10);
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { s.Alpha.Id, s.Bravo.Id, 424242 }, target);

        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(2);
        result.Items.Should().HaveCount(3);
        result.Items.Single(i => i.FileId == s.Alpha.Id).Success.Should().BeFalse();
        result.Items.Single(i => i.FileId == s.Bravo.Id).Success.Should().BeTrue();
        result.Items.Single(i => i.FileId == 424242).Error.Should().Contain("nicht im Katalog");
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Applied);
        (await fx.GetOperationsAsync(result.BatchId)).Should().HaveCount(2); // unbekannte Id wird nicht protokolliert
        fx.Messages.OfKind(FilesChangeKind.Moved).Should().ContainSingle().Which.FileIds.Should().Equal(s.Bravo.Id);
    }

    [Fact]
    public async Task Move_in_denselben_Ordner_ist_ein_Failed_Item()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();

        var result = await svc.MoveAsync(new[] { s.Alpha.Id }, Path.Combine(fx.LibraryRoot, "a"));

        result.Failed.Should().Be(1);
        result.Items[0].Error.Should().Contain("bereits im Zielordner");
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task Leere_Id_Liste_wirft_ArgumentException()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateFileOperationService();

        var move = () => svc.MoveAsync(Array.Empty<int>(), fx.OutsideDir);
        var recycle = () => svc.RecycleAsync(Array.Empty<int>());

        await move.Should().ThrowAsync<ArgumentException>();
        await recycle.Should().ThrowAsync<ArgumentException>();
    }

    // ---------------------------------------------------------------- Rename

    [Fact]
    public async Task Rename_aktualisiert_Name_Extension_MediaType_und_FTS()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();

        var result = await svc.RenameAsync(s.Alpha.Id, "sunset.JPEG");

        result.Type.Should().Be(OperationType.Rename);
        result.Succeeded.Should().Be(1);
        File.Exists(SourcePath(fx, "sunset.JPEG")).Should().BeTrue();
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeFalse();

        var renamed = (await fx.FindFileByIdAsync(s.Alpha.Id))!;
        renamed.Name.Should().Be("sunset.JPEG");
        renamed.Extension.Should().Be(".jpeg");
        renamed.MediaType.Should().Be(MediaType.Image);
        renamed.FolderId.Should().Be(s.Alpha.FolderId);
        fx.FtsRowIds("alpha").Should().BeEmpty();
        fx.FtsRowIds("sunset").Should().Equal(s.Alpha.Id);

        var batch = (await fx.GetBatchAsync(result.BatchId))!;
        batch.Status.Should().Be(BatchStatus.Applied);
        batch.Description.Should().Contain("alpha.jpg").And.Contain("sunset.JPEG");
        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Moved);
        message.FileIds.Should().Equal(s.Alpha.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a<b.jpg")]
    [InlineData("x/y.jpg")]
    [InlineData("x\\y.jpg")]
    [InlineData("..")]
    [InlineData("name.")]
    public async Task Rename_mit_ungueltigem_Namen_wirft_ohne_Batch(string newName)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();

        var act = () => svc.RenameAsync(s.Alpha.Id, newName);

        await act.Should().ThrowAsync<ArgumentException>();
        (await svc.GetRecentBatchesAsync()).Should().BeEmpty();
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task Rename_auf_bestehenden_Namen_schlaegt_fehl()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();

        var result = await svc.RenameAsync(s.Alpha.Id, "bravo.jpg");

        result.Failed.Should().Be(1);
        result.Items[0].Error.Should().Contain("existiert bereits");
        (await fx.FindFileByIdAsync(s.Alpha.Id))!.Name.Should().Be("alpha.jpg");
        fx.FtsRowIds("alpha").Should().Equal(s.Alpha.Id);
    }

    // ---------------------------------------------------------------- Undo

    [Fact]
    public async Task Undo_macht_Move_innerhalb_der_Library_rueckgaengig()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var target = Path.Combine(fx.LibraryRoot, "b");
        var moved = await svc.MoveAsync(new[] { s.Alpha.Id, s.Bravo.Id }, target);
        fx.Messages.Clear();

        var undo = await svc.UndoLastAsync();

        undo.Should().NotBeNull();
        undo!.BatchId.Should().Be(moved.BatchId);
        undo.Succeeded.Should().Be(2);
        undo.Failed.Should().Be(0);
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeTrue();
        Directory.EnumerateFiles(target).Should().BeEmpty();

        (await fx.FindFileByIdAsync(s.Alpha.Id))!.FolderId.Should().Be(s.Alpha.FolderId);
        (await fx.FindFileByIdAsync(s.Bravo.Id))!.FolderId.Should().Be(s.Bravo.FolderId);

        (await fx.GetBatchAsync(moved.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        var ops = await fx.GetOperationsAsync(moved.BatchId);
        ops.Should().OnlyContain(o => o.Status == FileOperationStatus.Undone && o.UndoneAtUtc != null);

        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Moved);
        message.FileIds.Should().BeEquivalentTo(new[] { s.Alpha.Id, s.Bravo.Id });

        (await svc.UndoLastAsync()).Should().BeNull("der Batch ist bereits rückgängig gemacht");
    }

    [Fact]
    public async Task Undo_macht_Move_nach_aussen_rueckgaengig_und_legt_Files_Zeile_neu_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var moved = await svc.MoveAsync(new[] { s.Bravo.Id }, fx.OutsideDir);
        fx.Messages.Clear();

        var undo = await svc.UndoBatchAsync(moved.BatchId);

        undo.Should().NotBeNull();
        undo!.Succeeded.Should().Be(1);
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(fx.OutsideDir, "bravo.jpg")).Should().BeFalse();

        var restored = (await fx.FindFileAsync("bravo.jpg"))!;
        restored.Id.Should().NotBe(s.Bravo.Id, "die alte Zeile wurde beim Move nach außen entfernt");
        restored.FolderId.Should().Be(s.Bravo.FolderId);
        restored.LibraryId.Should().Be(s.Library.Id);
        restored.Size.Should().Be(4096);
        restored.MediaType.Should().Be(MediaType.Image);
        restored.Extension.Should().Be(".jpg");
        fx.FtsRowIds("bravo").Should().Equal(restored.Id);
        undo.Items[0].FileId.Should().Be(restored.Id);

        (await fx.GetBatchAsync(moved.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Added);
        message.LibraryId.Should().Be(s.Library.Id);
        message.FileIds.Should().Equal(restored.Id);
    }

    [Fact]
    public async Task Undo_macht_Rename_rueckgaengig_inklusive_FTS()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var renamed = await svc.RenameAsync(s.Alpha.Id, "sunset.png");
        fx.Messages.Clear();

        var undo = await svc.UndoLastAsync();

        undo.Should().NotBeNull();
        undo!.BatchId.Should().Be(renamed.BatchId);
        undo.Succeeded.Should().Be(1);
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        File.Exists(SourcePath(fx, "sunset.png")).Should().BeFalse();

        var row = (await fx.FindFileByIdAsync(s.Alpha.Id))!;
        row.Name.Should().Be("alpha.jpg");
        row.Extension.Should().Be(".jpg");
        fx.FtsRowIds("sunset").Should().BeEmpty();
        fx.FtsRowIds("alpha").Should().Equal(s.Alpha.Id);
        (await fx.GetBatchAsync(renamed.BatchId))!.Status.Should().Be(BatchStatus.Undone);
        fx.Messages.OfKind(FilesChangeKind.Moved).Should().ContainSingle().Which.FileIds.Should().Equal(s.Alpha.Id);
    }

    [Fact]
    public async Task Undo_scheitert_wenn_Datei_am_Zielort_fehlt_und_Batch_bleibt_Applied()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var target = Path.Combine(fx.LibraryRoot, "b");
        var moved = await svc.MoveAsync(new[] { s.Alpha.Id }, target);
        File.Delete(Path.Combine(target, "alpha.jpg"));

        var undo = await svc.UndoLastAsync();

        undo.Should().NotBeNull();
        undo!.Succeeded.Should().Be(0);
        undo.Failed.Should().Be(1);
        undo.Items[0].Error.Should().Contain("nicht mehr vorhanden");
        (await fx.GetBatchAsync(moved.BatchId))!.Status.Should().Be(BatchStatus.Applied);
        var op = (await fx.GetOperationsAsync(moved.BatchId)).Should().ContainSingle().Subject;
        op.Status.Should().Be(FileOperationStatus.Applied);
        op.Error.Should().Contain("nicht mehr vorhanden");
    }

    [Fact]
    public async Task UndoLast_ohne_Batches_liefert_null_und_UndoBatch_mit_unbekannter_Id_auch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateFileOperationService();

        (await svc.UndoLastAsync()).Should().BeNull();
        (await svc.UndoBatchAsync(12345)).Should().BeNull();
    }

    // ---------------------------------------------------------------- Recycle

    [Fact]
    public async Task Recycle_ruft_Papierkorb_und_bereinigt_Katalog_Thumbnails_und_FTS()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        var thumbPath = await SeedThumbnailAsync(fx, s.Library.Id, s.Alpha.Id);
        var bin = new FakeRecycleBin();
        using var svc = fx.CreateFileOperationService(bin);

        var result = await svc.RecycleAsync(new[] { s.Alpha.Id });

        result.Type.Should().Be(OperationType.Recycle);
        result.Items[0].Error.Should().BeNull();
        result.Succeeded.Should().Be(1);
        result.Items[0].DestPath.Should().BeNull();
        bin.Recycled.Should().Equal(SourcePath(fx, "alpha.jpg"));
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeFalse();
        File.Exists(SourcePath(fx, "bravo.jpg")).Should().BeTrue();

        (await fx.FindFileByIdAsync(s.Alpha.Id)).Should().BeNull();
        (await fx.FindFileByIdAsync(s.Bravo.Id)).Should().NotBeNull();
        (await fx.GetThumbnailRowsAsync(s.Alpha.Id)).Should().BeEmpty();
        File.Exists(thumbPath).Should().BeFalse();
        fx.FtsRowIds("alpha").Should().BeEmpty();
        fx.FtsRowIds("bravo").Should().Equal(s.Bravo.Id);

        var batch = (await fx.GetBatchAsync(result.BatchId))!;
        batch.Status.Should().Be(BatchStatus.Applied);
        batch.Description.Should().Be("1 Datei in den Papierkorb");
        var op = (await fx.GetOperationsAsync(result.BatchId)).Should().ContainSingle().Subject;
        op.Action.Should().Be(OperationType.Recycle);
        op.Status.Should().Be(FileOperationStatus.Applied);
        op.DestPath.Should().BeNull();

        var message = fx.Messages.FilesChanged.Should().ContainSingle().Subject;
        message.Kind.Should().Be(FilesChangeKind.Removed);
        message.FileIds.Should().Equal(s.Alpha.Id);
    }

    [Fact]
    public async Task Recycle_bei_fehlender_Quelldatei_bereinigt_Katalog_mit_Hinweis()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        File.Delete(SourcePath(fx, "alpha.jpg"));
        var bin = new FakeRecycleBin();
        using var svc = fx.CreateFileOperationService(bin);

        var result = await svc.RecycleAsync(new[] { s.Alpha.Id });

        result.Succeeded.Should().Be(1);
        result.Items[0].Success.Should().BeTrue();
        result.Items[0].Error.Should().Contain("nicht mehr vorhanden");
        bin.Recycled.Should().BeEmpty();
        (await fx.FindFileByIdAsync(s.Alpha.Id)).Should().BeNull();
        fx.FtsRowIds("alpha").Should().BeEmpty();
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Applied);
    }

    [Fact]
    public async Task Recycle_Fehler_des_Papierkorbs_laesst_Katalog_unveraendert()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        var bin = new FakeRecycleBin { ThrowOnRecycle = new IOException("gesperrt") };
        using var svc = fx.CreateFileOperationService(bin);

        var result = await svc.RecycleAsync(new[] { s.Alpha.Id });

        result.Succeeded.Should().Be(0);
        result.Failed.Should().Be(1);
        result.Items[0].Error.Should().Contain("gesperrt");
        File.Exists(SourcePath(fx, "alpha.jpg")).Should().BeTrue();
        (await fx.FindFileByIdAsync(s.Alpha.Id)).Should().NotBeNull();
        fx.FtsRowIds("alpha").Should().Equal(s.Alpha.Id);
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Failed);
        fx.Messages.FilesChanged.Should().BeEmpty();
    }

    [Fact]
    public async Task Recycle_Batch_ist_nicht_rueckgaengig_machbar()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var recycled = await svc.RecycleAsync(new[] { s.Alpha.Id });

        var info = (await svc.GetRecentBatchesAsync()).Should().ContainSingle().Subject;
        info.CanUndo.Should().BeFalse();
        (await svc.UndoLastAsync()).Should().BeNull();

        var undo = await svc.UndoBatchAsync(recycled.BatchId);

        undo.Should().NotBeNull();
        undo!.Succeeded.Should().Be(0);
        undo.Failed.Should().Be(1);
        undo.Items[0].Error.Should().Contain("nicht rückgängig machbar");
        (await fx.GetBatchAsync(recycled.BatchId))!.Status.Should().Be(BatchStatus.Applied);
    }

    // ---------------------------------------------------------------- Verlauf

    [Fact]
    public async Task GetRecentBatches_liefert_juengste_zuerst_mit_ItemCount_und_CanUndo()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var s = await ArrangeAsync(fx);
        using var svc = fx.CreateFileOperationService();
        var moved = await svc.MoveAsync(new[] { s.Alpha.Id, s.Bravo.Id }, Path.Combine(fx.LibraryRoot, "b"));
        var renamed = await svc.RenameAsync(s.Alpha.Id, "alpha2.jpg");
        var recycled = await svc.RecycleAsync(new[] { s.Bravo.Id });

        var batches = await svc.GetRecentBatchesAsync();

        batches.Select(b => b.Id).Should().Equal(recycled.BatchId, renamed.BatchId, moved.BatchId);
        batches[0].Type.Should().Be(OperationType.Recycle);
        batches[0].ItemCount.Should().Be(1);
        batches[0].CanUndo.Should().BeFalse();
        batches[1].Type.Should().Be(OperationType.Rename);
        batches[1].CanUndo.Should().BeTrue();
        batches[2].Type.Should().Be(OperationType.Move);
        batches[2].ItemCount.Should().Be(2);
        batches[2].CanUndo.Should().BeTrue();
        batches.Should().OnlyContain(b => b.Status == BatchStatus.Applied && b.CompletedAtUtc != null);

        (await svc.GetRecentBatchesAsync(take: 2)).Should().HaveCount(2);

        // UndoLast nimmt den jüngsten undo-fähigen Batch (Rename), danach bleibt Move übrig.
        (await svc.UndoLastAsync())!.BatchId.Should().Be(renamed.BatchId);
        var after = await svc.GetRecentBatchesAsync();
        after.Single(b => b.Id == renamed.BatchId).CanUndo.Should().BeFalse();
        after.Single(b => b.Id == renamed.BatchId).Status.Should().Be(BatchStatus.Undone);
        after.Single(b => b.Id == moved.BatchId).CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task Unbekannte_FileIds_ergeben_Ergebnis_ohne_Batch()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var svc = fx.CreateFileOperationService();

        var result = await svc.RecycleAsync(new[] { 77, 78 });

        result.BatchId.Should().Be(0);
        result.Succeeded.Should().Be(0);
        result.Failed.Should().Be(2);
        result.Items.Should().OnlyContain(i => !i.Success && i.Error!.Contains("nicht im Katalog"));
        (await svc.GetRecentBatchesAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Helfer

    /// <summary>Legt Thumbnails-Zeile + Cache-Datei im FileId-Muster an; liefert den Cache-Pfad.</summary>
    private static async Task<string> SeedThumbnailAsync(TempLibraryFixture fx, int libraryId, int fileId)
    {
        var path = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(libraryId), $"{fileId}_320_still.jpg");
        TempLibraryFixture.WriteFile(path, 64);
        await using var ctx = fx.CreateContext();
        ctx.Thumbnails.Add(new Thumbnail
        {
            FileId = fileId,
            Size = 320,
            Kind = ThumbnailKind.Still,
            Format = "jpg",
            CachePath = path,
            SourceModifiedAtUtc = DateTime.UtcNow,
            GeneratedAtUtc = DateTime.UtcNow,
            ByteSize = 64,
            Status = ThumbnailStatus.Generated,
            AttemptCount = 1,
        });
        await ctx.SaveChangesAsync();
        return path;
    }
}

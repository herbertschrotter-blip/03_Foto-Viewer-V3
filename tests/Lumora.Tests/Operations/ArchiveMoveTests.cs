using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Enums;
using Lumora.Services.Messaging;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Operations;

/// <summary>
/// FileOperationService mit mehreren Wurzeln (ADR-0020): Move Eingang → Archiv samt Ordnerkette unter der richtigen
/// Wurzel, Undo zurück in den Eingang, Ordner anlegen und umbenennen (protokolliert, Undo des Umbenennens).
/// </summary>
public sealed class ArchiveMoveTests
{
    [Fact]
    public async Task Move_vom_Eingang_ins_Archiv_haengt_Ordnerkette_an_die_Archiv_Wurzel_und_Undo_bringt_zurueck()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var inbox = await fx.AddInboxRootAsync(lib.Id);
        var archiveRootId = (await fx.GetRootsAsync(lib.Id)).Single(r => r.Role == LibraryRootRole.Archive).Id;
        var source = fx.WriteInboxFile("kamera\\IMG_1.jpg");
        await fx.ScanAsync(lib.Id);
        var file = (await fx.FindFileAsync("IMG_1.jpg"))!;
        var sourceFolderId = file.FolderId;
        var target = Path.Combine(fx.LibraryRoot, "2024", "2024-07");
        var service = fx.CreateFileOperationService();

        var result = await service.MoveAsync(new[] { file.Id }, target);

        result.Succeeded.Should().Be(1);
        File.Exists(Path.Combine(target, "IMG_1.jpg")).Should().BeTrue();
        File.Exists(source).Should().BeFalse();
        var moved = (await fx.FindFileByIdAsync(file.Id))!;
        var targetFolder = (await fx.FindFolderByIdAsync(moved.FolderId))!;
        targetFolder.Path.Should().Be(target);
        targetFolder.RootId.Should().Be(archiveRootId, "Zielordner gehört zur Archiv-Wurzel");
        (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "2024")))!.RootId.Should().Be(archiveRootId, "Zwischenordner der Kette ebenfalls");
        fx.Messages.OfKind(FilesChangeKind.Moved).Should().ContainSingle();

        var undo = await service.UndoLastAsync();

        undo!.Succeeded.Should().Be(1);
        File.Exists(source).Should().BeTrue();
        var back = (await fx.FindFileByIdAsync(file.Id))!;
        back.FolderId.Should().Be(sourceFolderId);
        (await fx.FindFolderByIdAsync(sourceFolderId))!.RootId.Should().Be(inbox.Id);
    }

    [Fact]
    public async Task Move_aus_dem_Archiv_in_den_Eingang_bleibt_im_Katalog_mit_RootId_des_Eingangs()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var inbox = await fx.AddInboxRootAsync(lib.Id);
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var file = (await fx.FindFileAsync("a.jpg"))!;
        var target = Path.Combine(fx.InboxDir, "zurueck");

        var result = await fx.CreateFileOperationService().MoveAsync(new[] { file.Id }, target);

        result.Succeeded.Should().Be(1);
        var moved = (await fx.FindFileByIdAsync(file.Id))!;
        (await fx.FindFolderByIdAsync(moved.FolderId))!.RootId.Should().Be(inbox.Id);
        (await fx.FindFolderAsync(fx.InboxDir))!.ParentId.Should().BeNull("Eingangs-Wurzel ist Top-Level");
    }

    [Fact]
    public async Task Move_ausserhalb_aller_Wurzeln_entfernt_den_Katalogeintrag()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        await fx.AddInboxRootAsync(lib.Id);
        fx.WriteInboxFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var file = (await fx.FindFileAsync("a.jpg"))!;

        var result = await fx.CreateFileOperationService().MoveAsync(new[] { file.Id }, fx.OutsideDir);

        result.Succeeded.Should().Be(1);
        (await fx.FindFileByIdAsync(file.Id)).Should().BeNull();
        fx.Messages.OfKind(FilesChangeKind.Removed).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateFolder_legt_Ordner_und_Katalogzeile_unter_der_Wurzel_an_und_protokolliert_MkDir()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        var archiveRootId = (await fx.GetRootsAsync(lib.Id)).Single().Id;
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(lib.Id);
        var service = fx.CreateFileOperationService();

        var result = await service.CreateFolderAsync(lib.Id, fx.LibraryRoot, "2025");

        result.Success.Should().BeTrue();
        result.FolderId.Should().NotBeNull();
        Directory.Exists(result.Path).Should().BeTrue();
        var row = (await fx.FindFolderByIdAsync(result.FolderId!.Value))!;
        row.Path.Should().Be(Path.Combine(fx.LibraryRoot, "2025"));
        row.RootId.Should().Be(archiveRootId);
        row.ParentId.Should().Be((await fx.FindFolderAsync(fx.LibraryRoot))!.Id);
        var batch = (await fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.MkDir);
        batch.Status.Should().Be(BatchStatus.Applied);
        (await service.GetRecentBatchesAsync(1)).Single().CanUndo.Should().BeFalse("Ordner anlegen ist nicht rückgängig machbar");

        await FluentActions.Awaiting(() => service.CreateFolderAsync(lib.Id, fx.OutsideDir, "x"))
            .Should().ThrowAsync<InvalidOperationException>("außerhalb aller Wurzeln");
        await FluentActions.Awaiting(() => service.CreateFolderAsync(lib.Id, fx.LibraryRoot, "a/b"))
            .Should().ThrowAsync<ArgumentException>("ungültige Zeichen");
    }

    [Fact]
    public async Task RenameFolder_mit_Dateien_benennt_Ordner_um_zieht_Pfade_nach_und_ist_rueckgaengig_machbar()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("alt\\a.jpg");
        fx.WriteLibraryFile("alt\\sub\\b.jpg");
        await fx.ScanAsync(lib.Id);
        var folder = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "alt")))!;
        var subId = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "alt", "sub")))!.Id;
        var aId = (await fx.FindFileAsync("a.jpg"))!.Id;
        var service = fx.CreateFileOperationService();
        fx.Messages.Clear();

        var result = await service.RenameFolderAsync(folder.Id, "neu");

        result.Success.Should().BeTrue();
        result.Path.Should().Be(Path.Combine(fx.LibraryRoot, "neu"));
        Directory.Exists(result.Path).Should().BeTrue();
        Directory.Exists(folder.Path).Should().BeFalse();
        File.Exists(Path.Combine(result.Path, "sub", "b.jpg")).Should().BeTrue();
        var renamed = (await fx.FindFolderByIdAsync(folder.Id))!;
        renamed.Path.Should().Be(result.Path);
        renamed.Name.Should().Be("neu");
        (await fx.FindFolderByIdAsync(subId))!.Path.Should().Be(Path.Combine(result.Path, "sub"), "Unterordner-Pfade nachgezogen");
        (await fx.FindFileByIdAsync(aId))!.FolderId.Should().Be(folder.Id, "Dateien behalten Ordner-Id");
        var batch = (await fx.GetBatchAsync(result.BatchId))!;
        batch.OperationType.Should().Be(OperationType.RenameFolder);
        var ops = await fx.GetOperationsAsync(result.BatchId);
        ops.Should().HaveCount(3, "Ordner-Operation + 2 Dateien");
        ops.Should().OnlyContain(o => o.Status == FileOperationStatus.Applied);
        fx.Messages.OfKind(FilesChangeKind.Moved).Should().ContainSingle().Which.FileIds.Should().HaveCount(2);

        var undo = await service.UndoLastAsync();

        undo!.Succeeded.Should().Be(3);
        Directory.Exists(folder.Path).Should().BeTrue();
        Directory.Exists(result.Path).Should().BeFalse();
        (await fx.FindFolderByIdAsync(folder.Id))!.Path.Should().Be(folder.Path);
        (await fx.FindFolderByIdAsync(subId))!.Path.Should().Be(Path.Combine(folder.Path, "sub"));
        (await fx.GetBatchAsync(result.BatchId))!.Status.Should().Be(BatchStatus.Undone);
    }

    [Fact]
    public async Task RenameFolder_Regeln_Wurzel_Konflikt_und_identischer_Name()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a\\x.jpg");
        fx.WriteLibraryFile("b\\y.jpg");
        await fx.ScanAsync(lib.Id);
        var root = (await fx.FindFolderAsync(fx.LibraryRoot))!;
        var a = (await fx.FindFolderAsync(Path.Combine(fx.LibraryRoot, "a")))!;
        var service = fx.CreateFileOperationService();

        await FluentActions.Awaiting(() => service.RenameFolderAsync(root.Id, "anders"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Wurzelordner*");
        await FluentActions.Awaiting(() => service.RenameFolderAsync(a.Id, "b"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*existiert bereits*");
        await FluentActions.Awaiting(() => service.RenameFolderAsync(a.Id, "a"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*identisch*");
        await using var ctx = fx.CreateContext();
        (await ctx.OperationBatches.CountAsync()).Should().Be(0, "Validierung vor dem Protokoll");
    }
}

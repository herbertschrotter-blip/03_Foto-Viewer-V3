using Microsoft.EntityFrameworkCore;
using Lumora.Services.Watch;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Watch;

/// <summary>
/// Eigene Datei-Operationen der App (0.2.17): Verschieben, Papierkorb, Umbenennen, Ordner anlegen/umbenennen und
/// Rückgängig lösen keinen automatischen Scan aus — der Katalog ist dafür schon gebucht. Externe Änderungen daneben und
/// zurückgenommene Pfade (Katalog nicht gebucht) werden weiter erkannt.
/// </summary>
public sealed partial class LibraryWatchServiceTests
{
    /// <summary>Legt die Dateien an, scannt sie (ohne Überwachung) und startet danach die Überwachung.</summary>
    private static async Task<Dictionary<string, int>> SeedAndStartAsync(Harness h, params string[] relativePaths)
    {
        foreach (var path in relativePaths)
        {
            h.WriteFile(path);
        }

        (await h.Fixture.ScanAsync(h.LibraryId)).FilesInserted.Should().Be(relativePaths.Length);
        await using var ctx = h.Fixture.CreateContext();
        var ids = await ctx.Files.AsNoTracking().ToDictionaryAsync(f => f.Name, f => f.Id);

        await h.Service.StartAsync();
        await h.WaitForStateAsync(WatchState.Idle, "Überwachung bereit");
        return ids;
    }

    [Fact]
    public async Task Own_move_into_new_folders_triggers_no_automatic_scan_but_a_later_external_file_does()
    {
        await using var h = await Harness.CreateAsync(start: false);
        var ids = await SeedAndStartAsync(h, "eingang\\IMG_0001.jpg", "eingang\\IMG_0002.jpg");
        var ops = h.CreateFileOperations();

        var move = await ops.MoveAsync(ids.Values.ToList(), Path.Combine(h.Fixture.LibraryRoot, "2024", "2024-07"));

        move.Succeeded.Should().Be(2);
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0, "die eigenen Ereignisse (Löschen, Anlegen, neue Ordner) sind schon im Katalog gebucht");
        h.Service.Status.State.Should().Be(WatchState.Idle);

        h.WriteFile("extern\\IMG_9999.jpg");
        await h.WaitForScanStartAsync("eine fremde Datei nach dem eigenen Verschieben");
        h.Scans.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Own_recycle_rename_folder_operations_move_out_and_undo_trigger_no_automatic_scan()
    {
        await using var h = await Harness.CreateAsync(start: false);
        var ids = await SeedAndStartAsync(h, "a\\IMG_0001.jpg", "a\\IMG_0002.jpg", "b\\IMG_0003.jpg", "IMG_0004.jpg", "IMG_0005.jpg");
        var ops = h.CreateFileOperations();
        var root = h.Fixture.LibraryRoot;

        (await ops.RecycleAsync([ids["IMG_0004.jpg"]])).Succeeded.Should().Be(1);
        (await ops.RenameAsync(ids["IMG_0003.jpg"], "IMG_0003_neu.jpg")).Succeeded.Should().Be(1);
        (await ops.MoveAsync([ids["IMG_0005.jpg"]], h.Fixture.OutsideDir)).Succeeded.Should().Be(1, "aus der Bibliothek heraus");
        (await ops.CreateFolderAsync(h.LibraryId, root, "Neu")).Success.Should().BeTrue();
        var folderA = (await h.Fixture.FindFolderAsync(Path.Combine(root, "a")))!;
        (await ops.RenameFolderAsync(folderA.Id, "a2")).Success.Should().BeTrue();
        (await ops.UndoLastAsync())!.Succeeded.Should().BeGreaterThan(0);
        Directory.Exists(Path.Combine(root, "a")).Should().BeTrue("Umbenennen rückgängig gemacht");

        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0, "alle Änderungen stammen aus eigenen, im Katalog gebuchten Batches");
        h.Service.Status.State.Should().Be(WatchState.Idle);
    }

    [Fact]
    public async Task External_file_written_during_an_own_batch_is_still_detected()
    {
        await using var h = await Harness.CreateAsync(start: false);
        var ids = await SeedAndStartAsync(h, "IMG_0001.jpg", "IMG_0002.jpg");
        var recycleBin = new FakeRecycleBin { OnRecycle = _ => h.WriteFile("kamera\\IMG_0100.jpg") };
        var ops = h.Fixture.CreateFileOperationService(recycleBin, h.Gate, h.OwnChanges);

        (await ops.RecycleAsync([ids["IMG_0001.jpg"]])).Succeeded.Should().Be(1);

        await h.WaitForScanStartAsync("eine fremde Datei, geschrieben während des eigenen Batches");
        await h.WaitForStateAsync(WatchState.Idle, "Scan fertig");
        h.Scans.Calls.Should().Be(1, "nur die fremde Datei zählt, und sie genau einmal");
    }

    [Fact]
    public async Task Revoked_own_path_counts_as_an_event_but_only_under_a_watched_root()
    {
        await using var h = await Harness.CreateAsync(start: false);
        await SeedAndStartAsync(h, "IMG_0001.jpg");
        var path = Path.Combine(h.Fixture.LibraryRoot, "x", "IMG_0002.jpg");
        using var scope = h.OwnChanges.Begin();
        scope.Add(Path.Combine(h.Fixture.LibraryRoot, "x"));
        scope.Add(path);

        h.WriteFile(Path.Combine("x", "IMG_0002.jpg")); // eigener Schreibvorgang, dessen Katalog-Buchung gleich scheitert
        scope.Revoke(Path.Combine(h.Fixture.OutsideDir, "IMG_0003.jpg")); // außerhalb jeder Wurzel: zählt nicht
        await Task.Delay(NoEventWindow);
        h.Scans.Calls.Should().Be(0);

        scope.Revoke(path);

        await h.WaitForScanStartAsync("der zurückgenommene Pfad holt das verworfene Ereignis nach");
    }
}

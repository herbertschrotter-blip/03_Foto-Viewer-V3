using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.ArchiveTarget;
using Lumora.Services.Scanning;

namespace Lumora.Wpf.Tests.ArchiveTarget;

/// <summary>
/// „Ins Archiv…“ unter der Scan-Sperre (0.2.17): <see cref="ArchiveMoveService"/> verschiebt über den
/// FileOperationService, dessen Batch die Bibliothek belegt — er wartet auf einen laufenden Scan und gibt die
/// Bibliothek danach frei, sodass ein anschließender Scan („Aktualisieren“, Ordnerüberwachung) ohne Deadlock startet.
/// </summary>
public sealed class ArchiveMoveServiceGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class FixedPicker(string target) : IArchiveTargetPicker
    {
        public Task<string?> PickAsync(ArchiveTargetRequest request) => Task.FromResult<string?>(target);
    }

    [Fact]
    public async Task ArchiveAsync_waits_for_a_running_scan_and_frees_the_library_for_the_next_scan()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var inboxRoot = await db.AddFolderAsync(db.InboxRootId, db.InboxDir);
        var first = await db.AddFileAsync(inboxRoot, "IMG_0001.jpg");
        var second = await db.AddFileAsync(inboxRoot, "IMG_0002.jpg");
        ArchiveTestDb.WriteFile(Path.Combine(db.InboxDir, "IMG_0001.jpg"));
        ArchiveTestDb.WriteFile(Path.Combine(db.InboxDir, "IMG_0002.jpg"));
        var gate = new ScanGate();
        var target = Path.Combine(db.ArchiveDir, "2024", "2024-07");
        var service = new ArchiveMoveService(db.Factory, db.CreateFileOperationService(gate), new FixedPicker(target), NullLogger<ArchiveMoveService>.Instance);

        var scan = await gate.AcquireAsync(db.LibraryId, CancellationToken.None); // „Aktualisieren“ läuft
        var archive = Task.Run(() => service.ArchiveAsync([first.Id, second.Id]));
        (await Task.WhenAny(archive, Task.Delay(800))).Should().NotBeSameAs(archive, "der Batch wartet auf den Scan");
        File.Exists(Path.Combine(db.InboxDir, "IMG_0001.jpg")).Should().BeTrue();

        scan.Dispose();
        var result = await archive.WaitAsync(Timeout);

        result!.Succeeded.Should().Be(2);
        File.Exists(Path.Combine(target, "IMG_0002.jpg")).Should().BeTrue();
        using (await gate.AcquireAsync(db.LibraryId, CancellationToken.None).WaitAsync(Timeout))
        {
            gate.IsBusy(db.LibraryId).Should().BeTrue("der nächste Scan bekommt die Bibliothek sofort");
        }
    }
}

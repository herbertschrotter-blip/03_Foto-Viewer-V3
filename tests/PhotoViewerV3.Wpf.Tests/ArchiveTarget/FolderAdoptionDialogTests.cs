using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Folders.Adoption;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Wpf.Tests.Fixtures;

namespace PhotoViewerV3.Wpf.Tests.ArchiveTarget;

/// <summary>
/// Dialog „Ordner in die Bibliothek übernehmen…“ (ADR-0034): ViewModel ohne Fenster (Vorschau alt → neu, Zusammenziehen
/// an/aus, Optionen, Ziel außerhalb gesperrt) und das echte Fenster (lädt Baum und Vorschau, liefert den Plan).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class FolderAdoptionDialogTests
{
    private static (FolderAdoptionContext Context, AdoptionSourceSnapshot Snapshot, FolderAdoptionService Service) Arrange(ArchiveTestDb db)
    {
        ArchiveTestDb.WriteFile(Path.Combine(db.InboxDir, "leer", "inhalt", "a.jpg"));
        ArchiveTestDb.WriteFile(Path.Combine(db.InboxDir, "B", "b.jpg"));
        File.WriteAllText(Path.Combine(db.InboxDir, "B", "b.xmp"), "x");
        var context = new FolderAdoptionContext(
            db.LibraryId, "Test", db.InboxDir, db.InboxRootId, db.InboxDir, "eingang", db.ArchiveRootId, db.ArchiveDir, "archiv", null);
        var fileOps = db.CreateFileOperationService();
        var service = new FolderAdoptionService(
            db.Factory,
            fileOps,
            new LibraryService(db.Factory, db.Paths, NullLogger<LibraryService>.Instance, new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger()),
            NullLogger<FolderAdoptionService>.Instance);
        return (context, FolderAdoptionSourceReader.Read(db.InboxDir), service);
    }

    private static async Task WaitForPreviewAsync(FolderAdoptionViewModel vm, int rows)
    {
        for (var i = 0; i < 100 && (vm.IsComputing || vm.PreviewRows.Count != rows); i++)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Vorschau_zeigt_Ergebnisbaum_und_reagiert_auf_Zusammenziehen_und_Ziel()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var (context, snapshot, service) = Arrange(db);
        var vm = new FolderAdoptionViewModel(context, snapshot, db.Factory, db.CreateFileOperationService(), service);

        await vm.LoadAsync();
        await WaitForPreviewAsync(vm, 3);

        vm.Target.TargetPath.Should().Be(db.ArchiveDir, "ohne gemerktes Ziel ist der Bibliotheksordner gewählt");
        vm.PreviewRows.Select(r => r.RelativeTarget).Should().Equal("eingang", Path.Combine("eingang", "B"), Path.Combine("eingang", "inhalt"));
        vm.PreviewRows.Single(r => r.Name == "inhalt").Notes.Should().Contain("Zwischenordner");
        vm.PreviewRows.Single(r => r.Name == "B").FileCount.Should().Be(2, "Nicht-Mediendateien wandern mit");
        vm.PreviewSummary.Should().Contain("3 Dateien");
        vm.IsWholeInbox.Should().BeTrue();
        vm.CanConfirm.Should().BeTrue();
        var choice = vm.BuildChoice();
        choice!.Options.Should().Be(FolderAdoptionOptions.Default);
        choice.Plan.FileCount.Should().Be(3);

        vm.CollapseEmptyChains = false;
        await WaitForPreviewAsync(vm, 4);
        vm.PreviewRows.Select(r => r.Name).Should().Contain("leer");

        vm.Target.TargetPath = db.InboxDir;
        await WaitForPreviewAsync(vm, 0);
        vm.CanConfirm.Should().BeFalse("Ziel außerhalb des Bibliotheksordners");
        vm.BuildChoice().Should().BeNull();
        vm.PreviewError.Should().NotBeNull();
    }

    [Fact]
    public async Task Fenster_laedt_Baum_und_Vorschau_und_liefert_den_Plan()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var (context, snapshot, service) = Arrange(db);
        var fileOps = db.CreateFileOperationService();

        var choice = TestUiThread.Run(() =>
        {
            var viewModel = new FolderAdoptionViewModel(context, snapshot, db.Factory, fileOps, service);
            var window = new FolderAdoptionDialog(viewModel) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            var timer = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                if (viewModel.IsComputing || !viewModel.CanConfirm)
                {
                    return;
                }

                timer.Stop();
                window.Finish(confirmed: true);
            };
            window.Loaded += (_, _) => timer.Start();
            FolderAdoptionChoice? result = null;
            window.Closed += (_, _) => result = viewModel.BuildChoice();
            _ = window.ShowDialog();
            return result;
        }, TimeSpan.FromSeconds(20));

        choice.Should().NotBeNull();
        choice!.Plan.TargetFolder.Should().Be(db.ArchiveDir);
        choice.Plan.Folders.Select(f => f.RelativeTargetPath).Should().Contain(Path.Combine("eingang", "inhalt"));
    }
}

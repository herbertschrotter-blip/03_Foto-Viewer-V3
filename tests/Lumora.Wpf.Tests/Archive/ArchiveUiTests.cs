using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Gallery.Archives;
using Lumora.Gallery.Data;
using Lumora.Gallery.Services;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Services.Archive;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.Archive;

/// <summary>Archiv-Oberfläche (ADR-0022): Kachel-Daten, Inhaltszeilen, Entpacken-Dialog (ViewModel und echtes Fenster).</summary>
[Collection(UiTestCollection.Name)]
public sealed class ArchiveUiTests
{
    private static GalleryFileDto ArchiveDto(int id = 7, string name = "urlaub.ZIP")
        => new(id, 1, name, ".zip", 123_456, 1_700_000_000, (int)MediaType.Archive, false, null, false, @"C:\Fotos");

    [Fact]
    public void Kachel_erkennt_Archive_und_zeigt_Badge_mit_Endung()
    {
        var dto = ArchiveDto();
        dto.IsArchive.Should().BeTrue();
        dto.IsVideo.Should().BeFalse();

        var item = new GalleryItem(dto);
        item.IsArchive.Should().BeTrue();
        item.MediaType.Should().Be(MediaType.Archive);
        item.ArchiveBadge.Should().Be("ZIP");
        item.Thumbnail.Should().BeNull();
        item.CoreMatches(dto).Should().BeTrue();

        new GalleryItem(new GalleryFileDto(1, 1, "a.jpg", ".jpg", 1, 1, 0, false, null, false, @"C:\")).IsArchive.Should().BeFalse();
    }

    [Fact]
    public void Inhaltszeile_formatiert_Groesse_Datum_und_Symbol()
    {
        var modified = new DateTime(2024, 6, 1, 10, 30, 0, DateTimeKind.Utc);
        var row = new ArchiveEntryRow(new ArchiveEntryInfo(@"sub\clip.mp4", "clip.mp4", 5 * 1024 * 1024, 4 * 1024 * 1024, modified, false, true, false));
        row.Path.Should().Be(@"sub\clip.mp4");
        row.SizeText.Should().Be("5,0 MB");
        row.CompressedText.Should().Be("4,0 MB");
        row.ModifiedText.Should().Be(modified.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
        row.Symbol.Should().Be(global::Wpf.Ui.Controls.SymbolRegular.Video24);

        var nested = new ArchiveEntryRow(new ArchiveEntryInfo("inner.zip", "inner.zip", 10, 0, null, false, false, true));
        nested.Symbol.Should().Be(global::Wpf.Ui.Controls.SymbolRegular.Archive24);
        nested.CompressedText.Should().Be("–");
        nested.ModifiedText.Should().Be("–");
    }

    [Fact]
    public async Task Dialog_ViewModel_entpackt_alle_Archive_und_fasst_das_Ergebnis_zusammen()
    {
        var fake = new FakeArchiveService { FilesPerArchive = 3 };
        var targets = new[] { new ArchiveExtractTarget(1, @"C:\Fotos\a.zip", "a.zip"), new ArchiveExtractTarget(2, @"C:\Fotos\b.rar", "b.rar") };
        var vm = new ExtractArchiveDialogViewModel(new ArchiveExtractDialogRequest(targets, 5, @"C:\Fotos"), fake, new FakeFolderPicker(), NullLogger.Instance);
        vm.Title.Should().Be("2 Archive entpacken");
        vm.RecycleAfterExtract = true;
        vm.OverwriteMode = ArchiveOverwriteMode.Rename;

        await vm.StartCommand.ExecuteAsync(null);

        vm.IsFinished.Should().BeTrue();
        vm.IsRunning.Should().BeFalse();
        vm.Results.Should().HaveCount(2);
        fake.Calls.Should().HaveCount(2);
        fake.Calls[0].Options.TargetFolder.Should().BeNull("Standardziel = neben dem Archiv");
        fake.Calls[0].Options.ArchiveFileId.Should().Be(1);
        fake.Calls[0].Options.LibraryId.Should().Be(5);
        fake.Calls[0].Options.RecycleArchiveAfterExtract.Should().BeTrue();
        fake.Calls[0].Options.OverwriteMode.Should().Be(ArchiveOverwriteMode.Rename);
        fake.Calls[0].Options.ScanAfterExtract.Should().BeFalse("der Scan läuft erst nach dem letzten Archiv");
        fake.Calls[1].Options.ScanAfterExtract.Should().BeTrue();
        vm.ResultText.Should().StartWith("Fertig: 6 Datei(en) entpackt");
        vm.ResultIsWarning.Should().BeFalse();
        vm.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public async Task Dialog_ViewModel_eigener_Ordner_je_Archiv_Unterordner_und_Fehler_als_Liste()
    {
        var fake = new FakeArchiveService { FilesPerArchive = 1, Throw = new ArchiveEncryptedException(@"C:\Fotos\b.rar") };
        var targets = new[] { new ArchiveExtractTarget(1, @"C:\Fotos\a.zip", "a.zip"), new ArchiveExtractTarget(2, @"C:\Fotos\b.rar", "b.rar") };
        var vm = new ExtractArchiveDialogViewModel(new ArchiveExtractDialogRequest(targets, 5, @"C:\Fotos"), fake, new FakeFolderPicker { Folder = @"D:\Ziel" }, NullLogger.Instance);

        vm.UseCustomTarget = true;
        vm.CustomTargetFolder = "";
        await vm.StartCommand.ExecuteAsync(null);
        vm.ResultText.Should().Contain("Zielordner");
        vm.IsFinished.Should().BeFalse("ohne Ordner startet nichts");

        vm.BrowseFolderCommand.Execute(null);
        vm.CustomTargetFolder.Should().Be(@"D:\Ziel");
        vm.UseDefaultTarget.Should().BeFalse();
        await vm.StartCommand.ExecuteAsync(null);

        fake.Calls[0].Options.TargetFolder.Should().Be(@"D:\Ziel\a");
        vm.Results.Should().HaveCount(1, "das zweite Archiv ist passwortgeschützt");
        vm.ErrorLines.Should().ContainSingle(l => l.StartsWith("b.rar:") && l.Contains("passwortgeschützt"));
        vm.ResultIsWarning.Should().BeTrue();
        vm.ResultText.Should().Contain("1 Fehler");
    }

    [Fact]
    public void Dialog_Fenster_oeffnet_entpackt_und_schliesst()
    {
        var fake = new FakeArchiveService { FilesPerArchive = 2 };
        var targets = new[] { new ArchiveExtractTarget(1, @"C:\Fotos\a.zip", "a.zip") };
        // Gemessen wird nur die Zeit im Dialog — der Test-UI-Thread ist geteilt, Wartezeit davor zählt nicht.
        var (vm, elapsed) = TestUiThread.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var viewModel = new ExtractArchiveDialogViewModel(new ArchiveExtractDialogRequest(targets, 5, @"C:\Fotos"), fake, new FakeFolderPicker(), NullLogger.Instance);
            var window = new ExtractArchiveDialog(viewModel);
            // Testfenster außerhalb des sichtbaren Bereichs — sonst erscheint der Dialog auf dem Desktop des Benutzers.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -5000;
            window.Top = -5000;
            window.ShowActivated = false;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, async () =>
            {
                await viewModel.StartCommand.ExecuteAsync(null);
                viewModel.CloseCommand.Execute(null);
            });
            _ = window.ShowDialog();
            return (viewModel, sw.Elapsed);
        }, TimeSpan.FromSeconds(60));

        vm.Results.Should().ContainSingle().Which.FilesExtracted.Should().Be(2);
        vm.IsFinished.Should().BeTrue();
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    private sealed class FakeArchiveService : IArchiveService
    {
        public int FilesPerArchive { get; set; } = 1;
        public Exception? Throw { get; set; }
        public List<(string Path, ArchiveExtractOptions Options)> Calls { get; } = new();

        public bool IsArchive(string? path) => path?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true || path?.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) == true;

        public string GetDefaultTargetFolder(string archivePath)
            => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(archivePath)!, System.IO.Path.GetFileNameWithoutExtension(archivePath));

        public Task<ArchiveListing> ListAsync(string archivePath, CancellationToken ct = default)
            => Task.FromResult(new ArchiveListing(archivePath, Array.Empty<ArchiveEntryInfo>()));

        public async Task<ArchiveExtractResult> ExtractAsync(string archivePath, ArchiveExtractOptions options, IProgress<ArchiveExtractProgress>? progress = null, CancellationToken ct = default)
        {
            if (Throw is not null && Throw is ArchiveEncryptedException enc && enc.ArchivePath == archivePath)
            {
                throw Throw;
            }

            Calls.Add((archivePath, options));
            progress?.Report(new ArchiveExtractProgress(0, System.IO.Path.GetFileName(archivePath), 0, FilesPerArchive, 0, 100, null));
            await Task.Delay(10, ct);
            progress?.Report(new ArchiveExtractProgress(0, System.IO.Path.GetFileName(archivePath), FilesPerArchive, FilesPerArchive, 100, 100, "x.jpg"));
            return new ArchiveExtractResult(archivePath, options.TargetFolder ?? GetDefaultTargetFolder(archivePath), FilesPerArchive, 0, 0, 1, 100, Array.Empty<ArchiveExtractError>(), false, options.RecycleArchiveAfterExtract, null);
        }
    }

    private sealed class FakeFolderPicker : IFolderPickerService
    {
        public string? Folder { get; set; }

        public string? PickFolder(string title, string? initialDirectory) => Folder;
    }
}

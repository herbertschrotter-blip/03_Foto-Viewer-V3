using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumora.Gallery.ArchiveTarget;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.ArchiveTarget;

/// <summary>
/// Echtes Fenster „Ins Archiv…“ (Muster AppDialogWindow): öffnet, lädt den Baum, übernimmt den Datumsvorschlag und
/// schließt mit dem Zielordner — hängt das Layout, läuft der Test in den Timeout. Mit der Umgebungsvariable
/// <c>LUMORA_TEST_SHOTS</c> (Ordner) wird zusätzlich ein Bild des Dialogs abgelegt.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ArchiveTargetDialogTests
{
    [Fact]
    public async Task Dialog_laedt_Baum_und_liefert_den_uebernommenen_Vorschlag()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var root = await db.AddFolderAsync(db.ArchiveRootId, db.ArchiveDir);
        await db.AddFolderAsync(db.ArchiveRootId, Path.Combine(db.ArchiveDir, "2024"), root.Id);
        Directory.CreateDirectory(Path.Combine(db.ArchiveDir, "Neu von der Platte"));
        var request = new ArchiveTargetRequest(db.LibraryId, db.ArchiveRootId, db.ArchiveDir, "archiv", new[] { 1 }, null, Path.Combine("2026", "2026-01"));
        var fileOps = db.CreateFileOperationService();
        var shots = Environment.GetEnvironmentVariable("LUMORA_TEST_SHOTS");

        var (target, childNames) = TestUiThread.Run(() =>
        {
            var viewModel = new ArchiveTargetViewModel(request, db.Factory, fileOps);
            var window = new ArchiveTargetDialog(viewModel) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            string? names = null;
            var timer = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                if (viewModel.IsBusy || viewModel.Roots.Count == 0)
                {
                    return;
                }

                timer.Stop();
                names = string.Join("|", viewModel.Roots[0].Children.Select(c => c.Name));
                viewModel.UseSuggestionCommand.Execute(null);
                if (!string.IsNullOrEmpty(shots))
                {
                    window.UpdateLayout();
                    SaveShot(window, Path.Combine(shots, "archiv-dialog.png"));
                }

                window.Finish(confirmed: true);
            };
            window.Loaded += (_, _) => timer.Start();
            _ = window.ShowDialog();
            return (window.ViewModel.TargetPath, names);
        }, TimeSpan.FromSeconds(20));

        childNames.Should().Be("2024|Neu von der Platte");
        target.Should().Be(Path.Combine(db.ArchiveDir, "2026", "2026-01"));
    }

    private static void SaveShot(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

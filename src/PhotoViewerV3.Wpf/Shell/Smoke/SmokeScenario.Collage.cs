using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Gallery.Views;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Settings;
using Wpf.Ui.Appearance;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Collage-Ansicht“ (0.2.46, ADR-0044): umschalten (Einstellung gespeichert, Regler bleibt), Reihen
/// randvoll ±1 px und letzte Reihe nicht gestreckt, Zellen im Seitenverhältnis der Bildmaße (EXIF-6-Foto hochkant),
/// Vorschaubilder auf die Reihenhöhe × Skalierung dekodiert, Doppelklick auf eine Zelle (echter Handler der GalleryView)
/// öffnet genau diese Datei, Screenshots hell und dunkel, zurück zu Kacheln.
/// </summary>
internal sealed partial class SmokeScenario
{
    /// <summary>Erste sichtbare <see cref="GalleryView"/> unter <paramref name="root"/> (verborgene Seiten-Instanzen überspringen).</summary>
    private static GalleryView? FindVisibleGalleryView(DependencyObject? root)
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is GalleryView view && view.IsVisible)
            {
                return view;
            }

            if (FindVisibleGalleryView(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private async Task CollageAsync()
    {
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount,
            ShortWait,
            "Galerie mit allen Dateien").ConfigureAwait(true);
        if (_gallery.ViewMode != GalleryViewMode.Grid)
        {
            _gallery.ViewMode = GalleryViewMode.Grid;
        }

        // (1) Umschalten: Segment-Eintrag, Sichtbarkeit, gespeicherte Einstellung, Regler sichtbar
        var option = _gallery.ViewModeOptions.Select((o, i) => (o, i)).Single(x => x.o.Mode == GalleryViewMode.Collage);
        Check(option.i == 1 && option.o.Label == "Collage", $"Ansicht-Segment: {string.Join(" | ", _gallery.ViewModeOptions.Select(o => o.Label))}");
        _gallery.ViewMode = GalleryViewMode.Collage;
        Check(_gallery.ShowCollage && _gallery.ShowGalleryList && !_gallery.ShowGrid && !_gallery.ShowList && _gallery.IsTileSizeAdjustable,
            "Collage aktiv (Galerie-Liste sichtbar, Regler sichtbar)");

        // Die SICHTBARE GalleryView (Galerie- und Ordner-Seite haben je eine Instanz; 0.2.50)
        GalleryView? galleryView = null;
        GalleryListBox? list = null;
        JustifiedTilePanel? panel = null;
        try
        {
            await WaitUntilAsync(
                () => (galleryView = FindVisibleGalleryView(Application.Current.MainWindow)) is not null
                      && (list = FindVisualChild<GalleryListBox>(galleryView)) is not null
                      && (panel = FindVisualChild<JustifiedTilePanel>(list)) is { } p
                      && p.RowCount > 0 && p.ViewportHeight > 0 && p.Layout.ItemCount == _gallery.CurrentPage.Count,
                ShortWait,
                "Collage-Panel umgebrochen").ConfigureAwait(true);
        }
        catch (SmokeCheckException ex)
        {
            throw new SmokeCheckException(
                $"{ex.Message} (GalleryView sichtbar: {galleryView is not null}, ListBox: {list is not null}, Panel: {panel is not null}, " +
                $"Reihen {panel?.RowCount}, Viewport {panel?.ViewportHeight:N0}, Layout-Items {panel?.Layout.ItemCount}, Seite {_gallery.CurrentPage.Count}, " +
                $"ListBox sichtbar {list?.IsVisible}, Panel-Größe {panel?.ActualWidth:N0}×{panel?.ActualHeight:N0})");
        }
        Check(FindVisualChild<VirtualizingTilePanel>(list!) is null, "Kachel-Panel ersetzt");
        await Task.Delay(400).ConfigureAwait(true); // die Einstellung wird im Hintergrund geschrieben
        var storedMode = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = $k", ("$k", GalleryViewSettingsStore.ViewModeKey)).ConfigureAwait(true);
        Check(storedMode == nameof(GalleryViewMode.Collage), $"Setting {GalleryViewSettingsStore.ViewModeKey} = {storedMode}");

        var layout = panel!.Layout;
        Check(layout.TargetRowHeight == _gallery.TileSize, $"Zielhöhe = Regler {_gallery.TileSize} DIP; {layout.RowCount} Reihen, {panel.RealizedCount} Container, Umbruch {panel.LastLayoutMilliseconds:N2} ms");

        // (2) Reihen randvoll (±1 px) im Fenster, letzte nicht gestreckt
        list!.UpdateLayout();
        var filled = 0;
        for (var row = 0; row < layout.RowCount; row++)
        {
            var first = list.ItemContainerGenerator.ContainerFromIndex(layout.RowFirst(row)) as ListBoxItem;
            var last = list.ItemContainerGenerator.ContainerFromIndex(layout.RowLast(row)) as ListBoxItem;
            if (first is null || last is null)
            {
                continue;
            }

            var left = first.TranslatePoint(new Point(0, 0), panel).X;
            var right = last.TranslatePoint(new Point(last.ActualWidth, 0), panel).X;
            if (layout.IsFilledRow(row))
            {
                Check(Math.Abs(left) <= 1 && Math.Abs(right - panel.ActualWidth) <= 1, $"Reihe {row + 1}: {left:N1} … {right:N1} von {panel.ActualWidth:N1} (randvoll)");
                filled++;
            }
            else
            {
                Check(Math.Abs(left) <= 1 && right <= panel.ActualWidth + 1 && Math.Abs(layout.RowHeight(row) - (layout.TargetRowHeight + layout.Spacing)) < 0.5,
                    $"letzte Reihe {row + 1}: linksbündig bis {right:N1}, Höhe {layout.RowHeight(row):N0} = Ziel (nicht gestreckt)");
            }
        }

        Check(filled > 0, $"{filled} realisierte Reihen randvoll");

        // (3) Seitenverhältnis aus den Bildmaßen; EXIF-6-Foto hochkant
        var withSize = _gallery.CurrentPage.Select((item, index) => (item, index)).Where(x => x.item.MediaWidth is > 0).ToList();
        var wrong = withSize
            .Select(x => (x.item, cell: layout.ItemRect(x.index), expected: JustifiedRowLayout.ClampAspect((double)x.item.MediaWidth!.Value / x.item.MediaHeight!.Value)))
            .Where(x => Math.Abs(((x.cell.Width - layout.Spacing) / (x.cell.Height - layout.Spacing)) - x.expected) > 0.05 * x.expected)
            .Select(x => $"{x.item.Name}: Zelle {x.cell.Width:N0} × {x.cell.Height:N0}")
            .ToList();
        Check(withSize.Count >= 20 && wrong.Count == 0, $"{withSize.Count} Zellen im Seitenverhältnis ihrer Bildmaße{(wrong.Count > 0 ? ": " + string.Join("; ", wrong.Take(5)) : string.Empty)}");
        var exif6 = _gallery.CurrentPage.Select((item, index) => (item, index)).FirstOrDefault(x => x.item.Name == Exif6FileName);
        if (exif6.item is not null)
        {
            var cell = layout.ItemRect(exif6.index);
            Check(cell.Height > cell.Width, $"EXIF-6-Foto hochkant: Zelle {cell.Width:N0} × {cell.Height:N0}");
        }

        // (4) Vorschaubilder: Dekodierfläche = Reihenhöhe × Skalierung, sichtbares Hochkant-Bild neu dekodiert
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow!).DpiScaleX;
        var box = ThumbnailDecodeBox.FromCollageRow(_gallery.TileSize, dpi);
        Check(_gallery.ThumbnailDecodeBox == box, $"Dekodierfläche Collage {box.Width} × {box.Height} px = Reihe {_gallery.TileSize} × {dpi:P0}");
        if (exif6.item is not null)
        {
            await WaitUntilAsync(
                () => exif6.item.Thumbnail is BitmapSource b && b.PixelHeight >= Math.Min(box.Height, 800) - 1,
                ShortWait,
                $"EXIF-6-Bild für die Reihenhöhe dekodiert ({Describe(exif6.item)})").ConfigureAwait(true);
            Check(true, $"EXIF-6-Bild {Describe(exif6.item)} bedeckt die Reihenhöhe {box.Height} px");
        }

        await SnapshotAsync("26-galerie-collage").ConfigureAwait(true);

        // (5) Doppelklick (echter Handler der GalleryView) auf eine sichtbare Zelle → Lightbox zeigt genau diese Datei
        var slots = VisibleCollageSlots(panel);
        var mismatches = slots
            .Where(s => !ReferenceEquals(s.Container?.DataContext, _gallery.CurrentPage[s.Index]) || DisplayedName(s.Container) != _gallery.CurrentPage[s.Index].Name)
            .Select(s => $"Platz {s.Index + 1}")
            .ToList();
        Check(slots.Count > 0 && mismatches.Count == 0, $"{slots.Count} sichtbare Zellen tragen ihr Item{(mismatches.Count > 0 ? ": " + string.Join(", ", mismatches) : string.Empty)}");
        var (slotIndex, target) = slots.Where(s => !_gallery.CurrentPage[s.Index].IsArchive).ElementAt(slots.Count / 3);
        var targetName = DisplayedName(target);
        target!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = target,
        });
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox nach Doppelklick auf die Zelle").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var vm = window.ViewModel;
        Check(vm.CurrentItem?.FileId == _gallery.CurrentPage[slotIndex].Id && vm.CurrentItem?.Name == targetName,
            $"Doppelklick auf Zelle {slotIndex + 1} („{targetName}“) → Lightbox zeigt „{vm.Title}“ ({vm.CounterText})");
        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (6) Dunkel
        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("27-galerie-collage-dunkel").ConfigureAwait(true);
        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);

        // (7) Zurück zu Kacheln
        _gallery.ClearSelectionCommand.Execute(null);
        _gallery.ViewMode = GalleryViewMode.Grid;
        await WaitUntilAsync(
            () => FindVisualChild<VirtualizingTilePanel>(list) is { RealizedCount: > 0 } && FindVisualChild<JustifiedTilePanel>(list) is null,
            ShortWait,
            "zurück zum Kachel-Panel").ConfigureAwait(true);
        Check(_gallery.ShowGrid && !_gallery.ShowCollage, "zurück zur Kachelansicht");
    }

    /// <summary>Container per Treffertest in der Mitte jeder (zu mindestens 40 px) sichtbaren Zelle.</summary>
    private static List<(int Index, ListBoxItem? Container)> VisibleCollageSlots(JustifiedTilePanel panel)
    {
        var result = new List<(int, ListBoxItem?)>();
        var layout = panel.Layout;
        var offset = panel.VerticalOffset;
        for (var row = layout.RowAt(offset); row >= 0 && row <= layout.RowAt(offset + panel.ViewportHeight - 1); row++)
        {
            for (var index = layout.RowFirst(row); index <= layout.RowLast(row); index++)
            {
                var cell = layout.ItemRect(index);
                var top = Math.Max(cell.Top - offset, 0);
                var bottom = Math.Min(cell.Bottom - offset, panel.ViewportHeight);
                if (bottom - top < 40)
                {
                    continue;
                }

                DependencyObject? hit = panel.InputHitTest(new Point(cell.Left + (cell.Width / 2), (top + bottom) / 2)) as DependencyObject;
                while (hit is not null and not ListBoxItem)
                {
                    hit = VisualTreeHelper.GetParent(hit);
                }

                result.Add((index, hit as ListBoxItem));
            }
        }

        return result;
    }
}

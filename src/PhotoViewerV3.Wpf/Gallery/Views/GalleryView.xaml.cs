using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.ViewModels;

namespace PhotoViewerV3.Gallery.Views;

/// <summary>
/// Galerie-View. Code-Behind enthält nur UI-Glue (Owner-Window, Enter/Doppelklick auf den
/// fokussierten Container); alle Fachlogik liegt im <see cref="GalleryViewModel"/>.
/// Die View existiert zweimal (Galerie-Seite und rechte Hälfte der Ordner-Seite) auf demselben
/// Singleton-ViewModel; den Lightbox-Owner liefert deshalb immer die zuletzt geladene Instanz.
/// </summary>
public partial class GalleryView : UserControl
{
    private readonly GalleryViewModel _viewModel;
    private readonly ILogger<GalleryView> _logger;
    private Stopwatch? _gridStopwatch;

    public GalleryView(GalleryViewModel viewModel, ILogger<GalleryView> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.OwnerWindowResolver = ResolveOwnerWindow;
        _viewModel.CurrentPage.CollectionChanged += OnPageChanged;
        try
        {
            await _viewModel.EnsureInitializedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Galerie konnte nicht initialisiert werden");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.CurrentPage.CollectionChanged -= OnPageChanged;
        // Nur den eigenen Resolver abhängen — die andere Instanz kann inzwischen geladen worden sein.
        if (_viewModel.OwnerWindowResolver == (Func<Window?>)ResolveOwnerWindow)
        {
            _viewModel.OwnerWindowResolver = null;
        }
    }

    /// <summary>
    /// Diagnose: Zeit von der ersten Änderung an <see cref="GalleryViewModel.CurrentPage"/> (Seitenwechsel, Reload) bis
    /// zum Layout danach (Loaded-Priorität liegt unter Render) plus Zahl der realisierten Kachel-Container.
    /// </summary>
    private void OnPageChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_gridStopwatch is not null)
        {
            return; // Burst (ApplyRows fügt Item für Item ein) wird gebündelt
        }

        _gridStopwatch = Stopwatch.StartNew();
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                var sw = _gridStopwatch;
                _gridStopwatch = null;
                if (sw is null || !IsLoaded)
                {
                    return;
                }

                var panel = FindVisualChild<VirtualizingTilePanel>(GalleryList);
                _logger.LogInformation(
                    "Kachel-Grid sichtbar: {Items} Kacheln, {Containers} Container realisiert ({Columns} Spalten), {Ms:N0} ms nach Seitenänderung",
                    _viewModel.CurrentPage.Count, panel?.RealizedCount ?? -1, panel?.Columns ?? 0, sw.Elapsed.TotalMilliseconds);
            },
            DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private Window? ResolveOwnerWindow() => Window.GetWindow(this);

    /// <summary>Doppelklick auf eine Kachel öffnet die Lightbox (nicht bei Klick auf Herz/Sterne).</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { DataContext: GalleryItem item })
        {
            return;
        }

        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender))
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenInLightboxCommand.Execute(item);
    }

    /// <summary>Enter öffnet die Lightbox für den fokussierten (sonst den ersten ausgewählten) Eintrag.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // Maßgeblich ist der Auswahlzustand der Items (bei Virtualisierung kann die ListBox-Liste kurz nachhinken).
        var item = (Keyboard.FocusedElement as ListBoxItem)?.DataContext as GalleryItem
                   ?? _viewModel.CurrentPage.FirstOrDefault(i => i.IsSelected);
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenInLightboxCommand.Execute(item);
    }

    /// <summary>Das Kachel-Panel meldet den sichtbaren Index-Bereich — das ViewModel fordert Vorschaubilder dafür zuerst an.</summary>
    private void OnVisibleRangeChanged(object sender, VisibleRangeChangedEventArgs e)
        => _viewModel.ReportVisibleRange(e.FirstIndex, e.LastIndex);

    private static bool IsInsideButton(DependencyObject? source, DependencyObject container)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, container))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}

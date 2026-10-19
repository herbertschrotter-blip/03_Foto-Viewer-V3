using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
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
        // Nur den eigenen Resolver abhängen — die andere Instanz kann inzwischen geladen worden sein.
        if (_viewModel.OwnerWindowResolver == (Func<Window?>)ResolveOwnerWindow)
        {
            _viewModel.OwnerWindowResolver = null;
        }
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

        var item = (Keyboard.FocusedElement as ListBoxItem)?.DataContext as GalleryItem
                   ?? GalleryList.SelectedItem as GalleryItem;
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenInLightboxCommand.Execute(item);
    }

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

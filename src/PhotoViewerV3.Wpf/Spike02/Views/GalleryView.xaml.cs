using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PhotoViewerV3.Spike02.ViewModels;

namespace PhotoViewerV3.Spike02.Views;

/// <summary>
/// Galerie-View. Code-Behind enthält nur UI-Glue (Owner-Window, Enter/Doppelklick auf den
/// fokussierten Container); alle Fachlogik liegt im <see cref="GalleryViewModel"/>.
/// </summary>
public partial class GalleryView : UserControl
{
    private readonly GalleryViewModel _viewModel;

    public GalleryView(GalleryViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.OwnerWindowResolver = () => Window.GetWindow(this);
        Loaded += async (_, _) => await viewModel.EnsureInitializedAsync().ConfigureAwait(true);
    }

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

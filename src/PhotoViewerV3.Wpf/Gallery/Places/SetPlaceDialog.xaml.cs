using System.Windows;
using System.Windows.Input;

namespace PhotoViewerV3.Gallery.Places;

/// <summary>
/// Modaler Dialog „Ort setzen“ (ADR-0050); Logik in <see cref="SetPlaceViewModel"/>. Enter übernimmt (wenn gültig;
/// in der Trefferliste übernimmt Enter erst den Treffer), Esc bricht ab.
/// </summary>
public sealed partial class SetPlaceDialog : PhotoViewerV3.Shell.Dialogs.AppFluentWindow
{
    private PlaceDialogResult? _result;

    public SetPlaceDialog(SetPlaceViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => SearchBox.Focus();
    }

    public SetPlaceViewModel ViewModel { get; }

    public PlaceDialogResult? Result => _result;

    /// <summary>Zeigt den Dialog modal; null bei Abbruch.</summary>
    public static PlaceDialogResult? Show(Window? owner, SetPlaceViewModel viewModel)
    {
        var window = new SetPlaceDialog(viewModel);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = window.ShowDialog();
        return window.Result;
    }

    /// <summary>Übernehmen (Knopf, Enter, Tests); tut nichts bei ungültiger Eingabe.</summary>
    public void Accept()
    {
        if (ViewModel.BuildResult() is not { } result)
        {
            return;
        }

        _result = result;
        Close();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && ViewModel.HasSearchResults)
        {
            e.Handled = true;
            ResultList.SelectedIndex = 0;
            (ResultList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (SearchBox.IsKeyboardFocusWithin && ViewModel.HasSearchResults && ViewModel.SelectedHit is null)
            {
                ViewModel.SelectedHit = ViewModel.SearchResults[0]; // Enter in der Suche: ersten Treffer übernehmen
                return;
            }

            Accept();
        }
    }
}

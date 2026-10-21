using System.Windows;
using System.Windows.Input;

namespace Lumora.Gallery.CaptureDates;

/// <summary>
/// Modaler Dialog „Aufnahmedatum setzen“ (ADR-0046); Logik in <see cref="SetCaptureDateViewModel"/>. Enter übernimmt (wenn
/// gültig), Esc bricht ab.
/// </summary>
public sealed partial class SetCaptureDateDialog : Lumora.Shell.Dialogs.AppFluentWindow
{
    private CaptureDateDialogResult? _result;

    public SetCaptureDateDialog(SetCaptureDateViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public SetCaptureDateViewModel ViewModel { get; }

    public CaptureDateDialogResult? Result => _result;

    /// <summary>Zeigt den Dialog modal; null bei Abbruch.</summary>
    public static CaptureDateDialogResult? Show(Window? owner, CaptureDateDialogRequest request)
    {
        var window = new SetCaptureDateDialog(new SetCaptureDateViewModel(request));
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
        else if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.ComboBox)
        {
            e.Handled = true;
            Accept();
        }
    }
}

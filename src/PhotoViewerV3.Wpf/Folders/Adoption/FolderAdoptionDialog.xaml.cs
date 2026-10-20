using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PhotoViewerV3.Gallery.ArchiveTarget;
using PhotoViewerV3.Shell;
using PhotoViewerV3.Shell.Dialogs;

namespace PhotoViewerV3.Folders.Adoption;

/// <summary>
/// Modaler Dialog „Ordner in die Bibliothek übernehmen…“ (ADR-0034). Code-Behind nur Fenster-Mechanik (Laden,
/// Namensabfrage für „Neuer Ordner“ über <see cref="AppDialogWindow"/>, Enter/Esc, Suchtreffer); Logik im
/// <see cref="FolderAdoptionViewModel"/>.
/// </summary>
public sealed partial class FolderAdoptionDialog : AppFluentWindow
{
    private readonly FolderAdoptionViewModel _viewModel;
    private FolderAdoptionChoice? _result;

    public FolderAdoptionDialog(FolderAdoptionViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Title = viewModel.Title;
        viewModel.Target.PromptName = PromptNameAsync;
        Loaded += OnLoaded;
    }

    public FolderAdoptionViewModel ViewModel => _viewModel;

    /// <summary>Zeigt den Dialog modal; null bei Abbruch.</summary>
    public static FolderAdoptionChoice? Show(Window? owner, FolderAdoptionViewModel viewModel)
    {
        var window = new FolderAdoptionDialog(viewModel);
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
        return window._result;
    }

    /// <summary>Schließt den Dialog mit Ergebnis (für Tests und Tastatur).</summary>
    public void Finish(bool confirmed)
    {
        _result = confirmed ? _viewModel.BuildChoice() : null;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync().ConfigureAwait(true);
        _ = FolderTree.Focus();
    }

    private Task<string?> PromptNameAsync(string title, string message, string initial)
    {
        var outcome = AppDialogWindow.Show(this, new DialogRequest(title, message)
        {
            PrimaryText = "OK",
            InputInitialValue = initial,
        });
        return Task.FromResult(outcome.Choice == DialogChoice.Primary ? outcome.InputText : null);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Finish(false);
                break;
            case Key.Enter when _viewModel.CanConfirm && !ReferenceEquals(Keyboard.FocusedElement, SearchBox):
                e.Handled = true;
                Finish(true);
                break;
        }
    }

    private void OnSearchHitSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ArchiveFolderNode hit })
        {
            _viewModel.Target.TargetPath = hit.Path;
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Finish(true);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Finish(false);
}

/// <summary>Einrückung (px) der Vorschauzeile → linker Rand.</summary>
public sealed class IndentToMarginConverter : IValueConverter
{
    public static IndentToMarginConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new Thickness(value is double d ? d : 0, 2, 0, 2);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

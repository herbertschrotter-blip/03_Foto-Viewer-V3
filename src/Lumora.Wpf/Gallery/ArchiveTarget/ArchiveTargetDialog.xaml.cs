using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumora.Shell;
using Lumora.Shell.Dialogs;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>
/// Modaler Dialog „Ins Archiv…“ (ADR-0020). Code-Behind nur Fenster-Mechanik: Laden des Baums, Namensabfragen über
/// <see cref="AppDialogWindow"/>, Enter/Esc, Doppelklick auf Suchtreffer. Fachlogik im <see cref="ArchiveTargetViewModel"/>.
/// </summary>
public sealed partial class ArchiveTargetDialog : Lumora.Shell.Dialogs.AppFluentWindow
{
    private readonly ArchiveTargetViewModel _viewModel;
    private string? _result;

    public ArchiveTargetDialog(ArchiveTargetViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Title = viewModel.Title;
        viewModel.PromptName = PromptNameAsync;
        Loaded += OnLoaded;
    }

    /// <summary>Zeigt den Dialog modal und liefert den Zielordner oder null bei Abbruch.</summary>
    public static string? Show(Window? owner, ArchiveTargetViewModel viewModel)
    {
        var window = new ArchiveTargetDialog(viewModel);
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

    public ArchiveTargetViewModel ViewModel => _viewModel;

    /// <summary>Schließt den Dialog mit Ergebnis (für Tests und Tastatur).</summary>
    public void Finish(bool confirmed)
    {
        _result = confirmed && _viewModel.CanConfirm ? _viewModel.TargetPath : null;
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
            // Einfachklick wählt das Ziel, ohne die Suche zu verlassen
            _viewModel.TargetPath = hit.Path;
        }
    }

    private void OnSearchHitDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ArchiveFolderNode hit })
        {
            _viewModel.OpenSearchHitCommand.Execute(hit);
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Finish(true);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Finish(false);
}

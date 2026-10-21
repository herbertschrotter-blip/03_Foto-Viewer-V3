using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Lumora.Services.Library;
using Lumora.Shell.Dialogs;

namespace Lumora.Libraries.Linking;

/// <summary>Dialog „Verknüpfen mit…“ (ADR-0048); Logik in <see cref="LibraryLinkDialogViewModel"/>.</summary>
public sealed partial class LibraryLinkDialog : AppFluentWindow
{
    public LibraryLinkDialog(LibraryLinkDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        Loaded += (_, _) =>
        {
            if (ViewModel.Candidates.Count == 1)
            {
                ViewModel.SelectedCandidate = ViewModel.Candidates[0];
            }

            _ = CandidateBox.Focus();
        };
    }

    public LibraryLinkDialogViewModel ViewModel { get; }

    public LibraryLinkDialogResult? Result { get; private set; }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Result = ViewModel.BuildResult();
        if (Result is not null)
        {
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
            e.Handled = true;
        }
    }
}

/// <summary>Zeigt <see cref="LibraryLinkDialog"/> modal über dem Hauptfenster.</summary>
public sealed class LibraryLinkDialogService(ILibraryLinkService linking) : ILibraryLinkDialogService
{
    public Task<LibraryLinkDialogResult?> ShowLinkAsync(LibraryLinkDialogRequest request)
    {
        var window = new LibraryLinkDialog(new LibraryLinkDialogViewModel(linking, request));
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = window.ShowDialog();
        return Task.FromResult(window.Result);
    }
}

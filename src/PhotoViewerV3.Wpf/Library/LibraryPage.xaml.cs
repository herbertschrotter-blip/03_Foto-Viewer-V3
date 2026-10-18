using System.Windows.Controls;

namespace PhotoViewerV3.Library;

/// <summary>Seite „Bibliothek“: Liste, Anlegen, Scannen. Lädt die Liste bei jedem Anzeigen neu.</summary>
public partial class LibraryPage : UserControl
{
    public LibraryPage(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.LoadCommand.ExecuteAsync(null);
    }
}

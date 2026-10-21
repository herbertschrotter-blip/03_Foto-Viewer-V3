using System.Windows.Controls;

namespace PhotoViewerV3.Libraries;

/// <summary>
/// Bibliotheksverwaltung: Liste, Anlegen, Scannen. Seit 0.2.44 Reiter „Bibliotheken &amp; Eingänge“ der Einstellungen
/// (ADR-0042). Lädt die Liste bei jedem Anzeigen neu.
/// </summary>
public partial class LibraryPage : UserControl
{
    public LibraryPage(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Inhalt unter der Bibliotheksliste (z. B. die Ordnerüberwachung); eigener DataContext des Inhalts.</summary>
    public void SetFooter(object content) => FooterHost.Content = content;
}

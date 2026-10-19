using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoViewerV3.Settings.TagManagement;

/// <summary>Seite „Tags“ (Verwaltung); lädt die Liste samt Zählern bei jedem Anzeigen neu.</summary>
public partial class TagManagementPage : UserControl
{
    private readonly TagManagementViewModel _viewModel;

    public TagManagementPage(TagManagementViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnNewTagKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.CreateCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.CreateCommand.Execute(null);
        }
    }
}

using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace Lumora.Settings.TagManagement;

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
        // Tags gehören der Bibliotheks-Gruppe (ADR-0048): nach einem Wechsel der Bibliothek die Liste neu lesen.
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<TagManagementPage, Lumora.Services.Messaging.ActiveLibraryChangedMessage>(
            this, static (r, m) => Lumora.Shell.UiThread.Post(() => { if (r.IsLoaded) { _ = r._viewModel.LoadCommand.ExecuteAsync(null); } }));
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace Lumora.Settings.CategoryManagement;

/// <summary>Seite „Kategorien“ (Verwaltung, ADR-0027); lädt die Liste samt Zählern bei jedem Anzeigen neu.</summary>
public partial class CategoryManagementPage : UserControl
{
    private readonly CategoryManagementViewModel _viewModel;

    public CategoryManagementPage(CategoryManagementViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _ = viewModel.LoadCommand.ExecuteAsync(null);
        // Kategorien gehören der Bibliotheks-Gruppe (ADR-0048): nach einem Wechsel der Bibliothek die Liste neu lesen.
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<CategoryManagementPage, Lumora.Services.Messaging.ActiveLibraryChangedMessage>(
            this, static (r, m) => Lumora.Shell.UiThread.Post(() => { if (r.IsLoaded) { _ = r._viewModel.LoadCommand.ExecuteAsync(null); } }));
    }

    /// <summary>Enter in „Neue Kategorie“ oder „Werte“ legt an.</summary>
    private void OnNewCategoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.CreateCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.CreateCommand.Execute(null);
        }
    }

    /// <summary>Enter in „Neuer Wert“ fügt den Wert der Kategorie dieser Karte hinzu.</summary>
    private void OnNewValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is FrameworkElement { Tag: CategoryRow row })
        {
            e.Handled = true;
            _viewModel.AddValueCommand.Execute(row);
        }
    }
}

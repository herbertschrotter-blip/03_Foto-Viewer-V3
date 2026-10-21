using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lumora.Gallery.Categories;

/// <summary>
/// Modaler Dialog zur Kategorie-Zuweisung (Strg+K in Galerie und Lightbox, ADR-0027). Liefert nur die Änderungen
/// gegenüber dem Ausgangszustand (<see cref="CategoryPickLogic.ComputeResult"/>). Enter = Übernehmen (außer bei offener
/// Auswahlliste, die Enter selbst verarbeitet), Esc = Abbrechen.
/// </summary>
public sealed partial class CategoryPickerDialog : Lumora.Shell.Dialogs.AppFluentWindow
{
    private readonly IReadOnlyList<CategoryPickItem> _items;
    private CategoryPickResult? _result;

    public CategoryPickerDialog(CategoryPickRequest request)
    {
        InitializeComponent();
        SubtitleText.Text = request.SelectionCount == 1 ? "1 Datei" : $"{request.SelectionCount:N0} Dateien";
        _items = CategoryPickLogic.BuildItems(request);
        CategoryList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => FocusFirstComboBox();
    }

    public IReadOnlyList<CategoryPickItem> Items => _items;

    public CategoryPickResult? Result => _result;

    /// <summary>Zeigt den Dialog modal; null bei Abbruch.</summary>
    public static CategoryPickResult? Show(Window? owner, CategoryPickRequest request)
    {
        var window = new CategoryPickerDialog(request);
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

    /// <summary>Übernehmen (für Tests und Enter).</summary>
    public void Accept()
    {
        _result = CategoryPickLogic.ComputeResult(_items);
        Close();
    }

    private void FocusFirstComboBox()
    {
        if (_items.Count == 0)
        {
            _ = OkButton.Focus();
            return;
        }

        CategoryList.UpdateLayout();
        if (CategoryList.ItemContainerGenerator.ContainerFromIndex(0) is DependencyObject container
            && FindChild<ComboBox>(container) is { } combo)
        {
            _ = combo.Focus();
        }
        else
        {
            _ = OkButton.Focus();
        }
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Enter:
                e.Handled = true;
                Accept();
                break;
        }
    }
}

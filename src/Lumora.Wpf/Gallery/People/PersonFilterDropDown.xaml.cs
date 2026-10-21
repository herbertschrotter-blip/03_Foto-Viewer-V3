using System;
using System.Windows;
using System.Windows.Controls;
using Lumora.Gallery.Controls;

namespace Lumora.Gallery.People;

/// <summary>Chip + Flyout für den Personen-Filter (0.2.38, Texte 0.2.43). Logik liegt im GalleryViewModel (GalleryViewModel.Persons.cs).</summary>
public partial class PersonFilterDropDown : UserControl
{
    public PersonFilterDropDown()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => FlyoutListFilter.Apply<PersonFilterEntry>(PersonList, SearchBox.Text, p => p.SearchText); // Name, Rufname, Beziehung (0.2.47)

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        SearchBox.Text = string.Empty;
        FlyoutListFilter.Clear(PersonList);
    }
}

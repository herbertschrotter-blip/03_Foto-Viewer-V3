using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumora.Services.Faces.Assignment;
using Lumora.Shell.Dialogs;

namespace Lumora.Persons.Dialogs;

/// <summary>Modale Personenauswahl mit Suche; liefert die gewählte Person oder null.</summary>
public sealed partial class PersonPickerDialog : AppFluentWindow
{
    private readonly IReadOnlyList<PersonSummary> _persons;

    public PersonPickerDialog(string title, IReadOnlyList<PersonSummary> persons, int? suggestedPersonId)
    {
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        _persons = persons;
        ApplyFilter(string.Empty);
        if (suggestedPersonId is { } id && persons.FirstOrDefault(p => p.Id == id) is { } suggested)
        {
            PersonList.SelectedItem = suggested;
            PersonList.ScrollIntoView(suggested);
        }

        Loaded += (_, _) => FilterBox.Focus();
    }

    public PersonSummary? Result { get; private set; }

    public static PersonSummary? Show(Window? owner, string title, IReadOnlyList<PersonSummary> persons, int? suggestedPersonId)
    {
        var window = new PersonPickerDialog(title, persons, suggestedPersonId);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = window.ShowDialog();
        return window.Result;
    }

    /// <summary>Filter: Personen, deren Name (Vor-/Nachname, Rufname, Beziehung) den Text enthält (ohne Groß-/Kleinschreibung).</summary>
    internal static IReadOnlyList<PersonSummary> Filter(IReadOnlyList<PersonSummary> persons, string text)
        => string.IsNullOrWhiteSpace(text)
            ? persons
            : persons.Where(p => p.SearchText.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList(); // auch Vor-/Nachname, Rufname, Beziehung (0.2.47)

    private void ApplyFilter(string text)
    {
        var selected = PersonList.SelectedItem;
        var items = Filter(_persons, text);
        PersonList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null && items.Contains(selected))
        {
            PersonList.SelectedItem = selected;
        }
        else if (items.Count == 1)
        {
            PersonList.SelectedIndex = 0;
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter(FilterBox.Text);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => OkButton.IsEnabled = PersonList.SelectedItem is PersonSummary;

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Finish(PersonList.SelectedItem as PersonSummary);

    private void OnOkClick(object sender, RoutedEventArgs e) => Finish(PersonList.SelectedItem as PersonSummary);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(null);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Finish(null);
                break;
            case Key.Enter when PersonList.SelectedItem is PersonSummary person:
                e.Handled = true;
                Finish(person);
                break;
        }
    }

    /// <summary>Schließt mit Ergebnis (auch für Tests).</summary>
    public void Finish(PersonSummary? result)
    {
        Result = result;
        Close();
    }
}

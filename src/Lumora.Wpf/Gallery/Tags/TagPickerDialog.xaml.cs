using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Tags;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.Tags;

/// <summary>Zeile im Tag-Auswahl-Dialog: Häkchen (dreistufig bei gemischtem Ausgangszustand) je Tag.</summary>
public sealed partial class TagPickItem : ObservableObject
{
    public TagPickItem(TagInfo tag, bool? initial, int countInSelection, int selectionCount)
    {
        Tag = tag;
        Initial = initial;
        _isChecked = initial;
        AllowsMixed = initial is null;
        Hint = initial is null ? $"{countInSelection} von {selectionCount}" : string.Empty;
    }

    public TagInfo Tag { get; }

    public string Name => Tag.Name;

    public Brush Brush => TagBrushes.ForHex(Tag.Color);

    /// <summary>Ausgangszustand: true = alle, false = keine, null = ein Teil der Auswahl hat das Tag.</summary>
    public bool? Initial { get; }

    public bool AllowsMixed { get; }

    public string Hint { get; }

    [ObservableProperty] private bool? _isChecked;
}

/// <summary>
/// Modaler Dialog zur Tag-Zuweisung für eine Auswahl (Strg+T). Liefert nur die Änderungen gegenüber dem
/// Ausgangszustand; ein auf „gemischt“ belassenes Häkchen ändert nichts. Enter = Übernehmen, Esc = Abbrechen.
/// </summary>
public sealed partial class TagPickerDialog : Lumora.Shell.Dialogs.AppFluentWindow
{
    private readonly TagPickRequest _request;
    private readonly ObservableCollection<TagPickItem> _items = new();
    private TagPickResult? _result;

    public TagPickerDialog(TagPickRequest request)
    {
        _request = request;
        InitializeComponent();
        SubtitleText.Text = request.SelectionCount == 1 ? "1 Datei" : $"{request.SelectionCount:N0} Dateien";
        foreach (var tag in request.Tags)
        {
            var count = request.CountsInSelection.GetValueOrDefault(tag.Id);
            bool? initial = count == 0 ? false : count >= request.SelectionCount ? true : null;
            _items.Add(new TagPickItem(tag, initial, count, request.SelectionCount));
        }

        TagList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreateRow.Visibility = request.CreateTag is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => _ = (_items.Count == 0 && request.CreateTag is not null ? NewTagBox : (UIElement)OkButton).Focus();
    }

    public IReadOnlyList<TagPickItem> Items => _items;

    public TagPickResult? Result => _result;

    /// <summary>Zeigt den Dialog modal; null bei Abbruch.</summary>
    public static TagPickResult? Show(Window? owner, TagPickRequest request)
    {
        var window = new TagPickerDialog(request);
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

    /// <summary>Berechnet die Änderungen gegenüber dem Ausgangszustand (für Tests ohne Fenster nutzbar).</summary>
    public static TagPickResult ComputeResult(IEnumerable<TagPickItem> items)
    {
        var assign = new List<int>();
        var remove = new List<int>();
        foreach (var item in items)
        {
            if (item.IsChecked == item.Initial || item.IsChecked is null)
            {
                continue;
            }

            (item.IsChecked == true ? assign : remove).Add(item.Tag.Id);
        }

        return new TagPickResult(assign, remove);
    }

    /// <summary>Übernehmen (für Tests und Enter).</summary>
    public void Accept()
    {
        _result = ComputeResult(_items);
        Close();
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
            case Key.Enter when !ReferenceEquals(Keyboard.FocusedElement, NewTagBox):
                e.Handled = true;
                Accept();
                break;
        }
    }

    private void OnNewTagKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = CreateTagAsync();
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e) => _ = CreateTagAsync();

    private async System.Threading.Tasks.Task CreateTagAsync()
    {
        var name = NewTagBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || _request.CreateTag is null)
        {
            return;
        }

        CreateError.Visibility = Visibility.Collapsed;
        CreateButton.IsEnabled = false;
        try
        {
            var existing = _items.FirstOrDefault(i => string.Equals(i.Name, name, System.StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.IsChecked = true;
            }
            else
            {
                var tag = await _request.CreateTag(name);
                _items.Add(new TagPickItem(tag, initial: false, 0, _request.SelectionCount) { IsChecked = true });
                EmptyText.Visibility = Visibility.Collapsed;
            }

            NewTagBox.Text = string.Empty;
        }
        catch (System.Exception ex)
        {
            CreateError.Text = ex.Message;
            CreateError.Visibility = Visibility.Visible;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }
}

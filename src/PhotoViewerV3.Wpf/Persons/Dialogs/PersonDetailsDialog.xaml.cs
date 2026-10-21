using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Shell.Dialogs;

namespace PhotoViewerV3.Persons.Dialogs;

/// <summary>Modaler Dialog „Person benennen/bearbeiten“ (0.2.47, ADR-0045); liefert die Eingabe oder null.</summary>
public sealed partial class PersonDetailsDialog : AppFluentWindow
{
    public PersonDetailsDialog(PersonDetailsEditor editor)
    {
        Editor = editor;
        InitializeComponent();
        DataContext = editor;
        Loaded += (_, _) => FirstNameBox.Focus();
    }

    public PersonDetailsEditor Editor { get; }

    public PersonDetailsInput? Result { get; private set; }

    public static PersonDetailsInput? Show(Window? owner, PersonEditRequest request)
    {
        var window = new PersonDetailsDialog(new PersonDetailsEditor(request));
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

    private void OnOkClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(null);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when !RelationBox.IsDropDownOpen:
                e.Handled = true;
                Finish(null);
                break;
            case Key.Enter when !RelationBox.IsDropDownOpen && e.OriginalSource is not ComboBoxItem:
                e.Handled = true;
                Confirm();
                break;
        }
    }

    private void Confirm()
    {
        if (Editor.BuildInput() is { } input)
        {
            Finish(input);
        }
    }

    /// <summary>Schließt mit Ergebnis (auch für Tests).</summary>
    public void Finish(PersonDetailsInput? result)
    {
        Result = result;
        Close();
    }
}

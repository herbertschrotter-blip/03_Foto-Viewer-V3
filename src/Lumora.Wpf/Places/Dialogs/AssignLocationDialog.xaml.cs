using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace Lumora.Places.Dialogs;

/// <summary>Modaler Dialog „Fotos verorten“ (ADR-0051). Enter = Verorten, Esc = Abbrechen.</summary>
public sealed partial class AssignLocationDialog : Lumora.Shell.Dialogs.AppFluentWindow
{
    public AssignLocationDialog(AssignLocationRequest request)
    {
        InitializeComponent();
        QuestionText.Text = Question(request.FileCount);
        PlaceText.Text = request.PlaceName ?? "Kein Ort in der Nähe bekannt";
        CoordinatesText.Text = FormatCoordinates(request.Latitude, request.Longitude);
        Loaded += (_, _) => OkButton.Focus();
    }

    public AssignLocationAnswer? Answer { get; private set; }

    public static string Question(int count) => count == 1 ? "1 Foto hier verorten?" : $"{count.ToString("N0", CultureInfo.GetCultureInfo("de-DE"))} Fotos hier verorten?";

    /// <summary>„48,20820° N · 16,37380° O“.</summary>
    public static string FormatCoordinates(double latitude, double longitude)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        return $"{Math.Abs(latitude).ToString("F5", de)}° {(latitude >= 0 ? "N" : "S")} · {Math.Abs(longitude).ToString("F5", de)}° {(longitude >= 0 ? "O" : "W")}";
    }

    public static AssignLocationAnswer? Show(Window? owner, AssignLocationRequest request)
    {
        var window = new AssignLocationDialog(request);
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
        return window.Answer;
    }

    /// <summary>Bestätigen (Knopf, Enter, Tests).</summary>
    public void Accept()
    {
        Answer = new AssignLocationAnswer(KeepModifiedBox.IsChecked == true);
        Close();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
    }
}

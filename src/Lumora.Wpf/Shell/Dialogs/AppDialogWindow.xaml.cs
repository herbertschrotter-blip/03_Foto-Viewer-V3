using System;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Lumora.Shell.Dialogs;

/// <summary>Beschreibung eines Dialogs: Texte, Knöpfe, optionales Eingabefeld und Detailzeilen.</summary>
public sealed record DialogRequest(string Title, string Message)
{
    /// <summary>Text des Primärknopfs; null = kein Primärknopf.</summary>
    public string? PrimaryText { get; init; }

    /// <summary>Text des Sekundärknopfs; null = kein Sekundärknopf.</summary>
    public string? SecondaryText { get; init; }

    /// <summary>Text des Schließen-Knopfs (immer sichtbar).</summary>
    public string CloseText { get; init; } = "Abbrechen";

    public bool PrimaryIsDestructive { get; init; }

    public bool SecondaryIsDestructive { get; init; }

    /// <summary>Vorbelegung des Eingabefelds; null = kein Eingabefeld.</summary>
    public string? InputInitialValue { get; init; }

    /// <summary>Mehrzeilige Details (z. B. fehlgeschlagene Dateien), scrollbar; null = ausgeblendet.</summary>
    public string? Details { get; init; }
}

/// <summary>Ergebnis eines Dialogs: gewählter Knopf und ggf. der eingegebene Text.</summary>
public sealed record DialogOutcome(DialogChoice Choice, string? InputText);

/// <summary>
/// Schlichtes modales Dialogfenster der App (Bestätigung, Auswahl, Eingabe, Fehler). Feste Breite,
/// Höhe aus dem Inhalt — bewusst ohne die Größenlogik der Wpf.Ui MessageBox (Layout-Schleife, siehe XAML).
/// Enter = Primärknopf (bzw. Schließen, wenn es keinen gibt), Esc = Schließen.
/// </summary>
public sealed partial class AppDialogWindow : AppFluentWindow
{
    private DialogChoice _choice = DialogChoice.None;

    public AppDialogWindow(DialogRequest request)
    {
        InitializeComponent();
        Apply(request);
        Loaded += OnLoaded;
    }

    /// <summary>Zeigt den Dialog modal (nested message loop) und liefert Knopf + Eingabe zurück.</summary>
    public static DialogOutcome Show(Window? owner, DialogRequest request)
    {
        var window = new AppDialogWindow(request);
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
        return window.Outcome;
    }

    public DialogOutcome Outcome => new(_choice, InputBox.Visibility == Visibility.Visible ? InputBox.Text : null);

    /// <summary>Schließt den Dialog mit dem angegebenen Ergebnis (für Tests und Tastatur).</summary>
    public void Finish(DialogChoice choice)
    {
        _choice = choice;
        Close();
    }

    private void Apply(DialogRequest request)
    {
        Title = request.Title;
        DialogTitleBar.Title = request.Title;
        MessageText.Text = request.Message;

        if (request.InputInitialValue is not null)
        {
            InputBox.Text = request.InputInitialValue;
            InputBox.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(request.Details))
        {
            DetailsText.Text = request.Details;
            DetailsScroll.Visibility = Visibility.Visible;
        }

        if (request.PrimaryText is not null)
        {
            PrimaryButton.Content = request.PrimaryText;
            PrimaryButton.Appearance = request.PrimaryIsDestructive ? ControlAppearance.Danger : ControlAppearance.Primary;
            PrimaryButton.Visibility = Visibility.Visible;
        }

        if (request.SecondaryText is not null)
        {
            SecondaryButton.Content = request.SecondaryText;
            SecondaryButton.Appearance = request.SecondaryIsDestructive ? ControlAppearance.Danger : ControlAppearance.Secondary;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        CloseButton.Content = request.CloseText;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (InputBox.Visibility == Visibility.Visible)
        {
            _ = InputBox.Focus();
            InputBox.SelectAll();
        }
        else if (PrimaryButton.Visibility == Visibility.Visible)
        {
            _ = PrimaryButton.Focus();
        }
        else
        {
            _ = CloseButton.Focus();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Finish(DialogChoice.None);
                break;
            case Key.Enter when PrimaryButton.Visibility == Visibility.Visible:
                e.Handled = true;
                Finish(DialogChoice.Primary);
                break;
            case Key.Enter:
                e.Handled = true;
                Finish(DialogChoice.None);
                break;
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Primary);

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Secondary);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.None);
}

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Lumora.Gallery.Archives;

/// <summary>
/// Entpacken-Dialog (ADR-0022): Code-Behind nur Fenster-Mechanik (Enter = Entpacken, Esc = Abbrechen/Schließen, X bricht
/// einen laufenden Lauf erst ab). Fachlogik im <see cref="ExtractArchiveDialogViewModel"/>.
/// </summary>
public sealed partial class ExtractArchiveDialog : Lumora.Shell.Dialogs.AppFluentWindow
{
    private readonly ExtractArchiveDialogViewModel _viewModel;

    public ExtractArchiveDialog(ExtractArchiveDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += OnCloseRequested;
        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    public ExtractArchiveDialogViewModel ViewModel => _viewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = StartButton.Focus();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.OnClosing())
        {
            e.Cancel = true; // laufender Lauf wird abgebrochen; der Benutzer schließt danach
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _viewModel.CloseCommand.Execute(null);
                break;
            case Key.Enter when _viewModel.CanEditOptions && _viewModel.StartCommand.CanExecute(null):
                e.Handled = true;
                _viewModel.StartCommand.Execute(null);
                break;
        }
    }
}

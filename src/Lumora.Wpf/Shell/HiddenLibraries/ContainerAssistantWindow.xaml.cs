using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Lumora.Services.HiddenLibraries.Containers;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Startet den Container-Assistenten vor „Versteckte Bibliothek anlegen“ (ADR-0061); Tests ersetzen ihn.</summary>
public interface IContainerAssistant
{
    ContainerAssistantResult Run();
}

/// <summary>Produktiv: modales Fenster über dem Hauptfenster.</summary>
public sealed class ContainerAssistant : IContainerAssistant
{
    private readonly IContainerCreation _creation;
    private readonly IContainerFilePicker _picker;

    public ContainerAssistant(IContainerCreation creation, IContainerFilePicker picker)
    {
        _creation = creation;
        _picker = picker;
    }

    public ContainerAssistantResult Run()
    {
        var viewModel = new ContainerAssistantViewModel(_creation, _picker);
        var window = new ContainerAssistantWindow(viewModel);
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return viewModel.Result ?? ContainerAssistantResult.Cancelled();
    }
}

/// <summary>Datei-Dialoge ohne Eintrag unter „Zuletzt verwendet“ (ADR-0049); Lumora merkt sich keinen Pfad.</summary>
public sealed class ContainerFilePicker : IContainerFilePicker
{
    public string? PickNewVhdx(string suggestedPath)
    {
        var folder = Path.GetDirectoryName(suggestedPath);
        var dialog = new SaveFileDialog
        {
            Title = "Speicherort der Containerdatei",
            Filter = "Virtuelle Festplatte (*.vhdx)|*.vhdx",
            DefaultExt = ".vhdx",
            AddExtension = true,
            OverwritePrompt = true,
            AddToRecent = false,
            FileName = Path.GetFileName(suggestedPath),
            InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : string.Empty,
        };
        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FileName : null;
    }

    public string? PickContainerFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "VeraCrypt-Containerdatei wählen",
            Filter = "Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            AddToRecent = false,
        };
        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FileName : null;
    }

    private static Window? ActiveWindow()
    {
        if (Application.Current is null)
        {
            return null;
        }

        foreach (Window window in Application.Current.Windows)
        {
            if (window.IsActive)
            {
                return window;
            }
        }

        return Application.Current.MainWindow;
    }
}

/// <summary>Fenster des Container-Assistenten; die Logik steckt in <see cref="ContainerAssistantViewModel"/>.</summary>
public sealed partial class ContainerAssistantWindow : Dialogs.AppFluentWindow
{
    private readonly ContainerAssistantViewModel _viewModel;

    public ContainerAssistantWindow(ContainerAssistantViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += OnCloseRequested;
        Closing += OnClosing;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
        Loaded += (_, _) => ExistingRadio.Focus();
    }

    /// <summary>Für Tests: true, sobald das Fenster schließt (über das ViewModel oder X).</summary>
    internal bool IsClosing { get; private set; }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (IsClosing)
        {
            return; // Abbrechen aus OnClosing — das Fenster schließt schon
        }

        IsClosing = true;
        try
        {
            DialogResult = _viewModel.Result?.Outcome != ContainerAssistantOutcome.Cancelled;
        }
        catch (InvalidOperationException)
        {
            Close(); // nicht modal (Tests)
        }
    }

    /// <summary>Schließen (X, Alt+F4) = Abbrechen; während ein Schritt läuft, bleibt das Fenster offen.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsClosing)
        {
            return;
        }

        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            return;
        }

        IsClosing = true;
        _viewModel.Cancel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.Cancel();
        }
    }
}

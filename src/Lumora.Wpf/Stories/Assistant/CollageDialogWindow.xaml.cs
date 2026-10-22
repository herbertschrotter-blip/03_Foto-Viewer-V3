using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Lumora.Services.Assistant;

namespace Lumora.Stories.Assistant;

/// <summary>Fenster „Als Collage-Bild speichern“ (ADR-0063).</summary>
public sealed partial class CollageDialogWindow : Shell.Dialogs.AppFluentWindow
{
    private readonly CollageDialogViewModel _viewModel;
    private bool _closing;

    public CollageDialogWindow(CollageDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += OnCloseRequested;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Dispose();
        };
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            DialogResult = _viewModel.Result is not null;
        }
        catch (InvalidOperationException)
        {
            Close(); // nicht modal (Tests)
        }
    }

    /// <summary>Schließen (X, Alt+F4) während des Speicherns bricht nur das Speichern ab.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            _viewModel.Cancel();
            return;
        }

        _closing = true;
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

/// <summary>Produktiv: modales Fenster über dem Hauptfenster.</summary>
public sealed class CollageDialogService : ICollageDialogService
{
    private readonly ICollageService _collage;
    private readonly ICollageFilePicker _picker;
    private readonly IStoryImageSource? _images;
    private readonly IStoryMediaLoader? _media;
    private readonly ILogger<CollageDialogService>? _logger;

    public CollageDialogService(ICollageService collage, ICollageFilePicker picker, IStoryImageSource? images = null, IStoryMediaLoader? media = null, ILogger<CollageDialogService>? logger = null)
    {
        _collage = collage;
        _picker = picker;
        _images = images;
        _media = media;
        _logger = logger;
    }

    public Task<CollageSaveResult?> ShowAsync(CollageRequest request)
    {
        var viewModel = new CollageDialogViewModel(_collage, _picker, request, _images, _media, _logger);
        var window = new CollageDialogWindow(viewModel);
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
        return Task.FromResult(viewModel.Result);
    }
}

/// <summary>SaveFileDialog ohne Eintrag unter „Zuletzt verwendet“ (ADR-0049); Lumora merkt sich keinen Pfad.</summary>
public sealed class CollageFilePicker : ICollageFilePicker
{
    public string? PickSavePath(string initialFolder, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Collage-Bild speichern unter",
            Filter = "JPEG-Bild (*.jpg)|*.jpg|PNG-Bild (*.png)|*.png",
            DefaultExt = ".jpg",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = fileName,
            AddToRecent = false,
        };
        // nur vorhandene Ordner (ohne Bestätigung wird nichts angelegt); sonst der nächste vorhandene darüber
        for (var folder = initialFolder; !string.IsNullOrWhiteSpace(folder); folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
                break;
            }
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}

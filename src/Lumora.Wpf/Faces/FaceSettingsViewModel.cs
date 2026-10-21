using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Models;
using Lumora.Shell;

namespace Lumora.Faces;

/// <summary>
/// Einstellungen → „Gesichtserkennung“ (ADR-0035): Schalter an/aus (Standard aus) und Grafikkarte, Modellstatus mit
/// „Modell herunterladen…“ (nur nach Bestätigung im Dialog, Fortschritt, abbrechbar), Stand des Index und
/// „Index neu aufbauen“. Zustand des Index kommt über <see cref="FaceIndexStatusMessage"/> (auf den UI-Thread gebracht).
/// </summary>
public sealed partial class FaceSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Text des Download-Knopfs (Größe, Herkunft, Lizenz).</summary>
    public const string DownloadButtonText = "Modell herunterladen… (275 MB, InsightFace buffalo_l, nur nicht-kommerzielle Nutzung)";

    private readonly IFaceIndexService _index;
    private readonly IFaceModelProvider _models;
    private readonly IDialogService _dialogs;
    private readonly ILogger<FaceSettingsViewModel> _logger;
    private readonly IMessenger _messenger;
    private CancellationTokenSource? _downloadCts;
    private bool _loading;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _useGpu = true;
    [ObservableProperty] private bool _isModelReady;
    [ObservableProperty] private string _modelStatusText = string.Empty;
    [ObservableProperty] private string _modelFolder = string.Empty;
    [ObservableProperty] private string _indexStatusText = string.Empty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(CancelDownloadCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string? _downloadText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    private bool _isRebuilding;

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    /// <param name="ignore">Mindestgröße für Gesichter (ADR-0040); ohne Dienst bleibt der Abschnitt verborgen.</param>
    public FaceSettingsViewModel(
        IFaceIndexService index,
        IFaceModelProvider models,
        IDialogService dialogs,
        ILogger<FaceSettingsViewModel> logger,
        IMessenger? messenger = null,
        Lumora.Services.Faces.Assignment.IFaceIgnoreService? ignore = null)
    {
        _ignore = ignore;
        _index = index;
        _models = models;
        _dialogs = dialogs;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _messenger.Register<FaceSettingsViewModel, FaceIndexStatusMessage>(this, static (r, m) => UiThread.Post(() => r.ShowStatus(m.Status)));
        _messenger.Register<FaceSettingsViewModel, FaceModelChangedMessage>(this, static (r, _) => UiThread.Post(r.RefreshModel));
    }

    /// <summary>Beim Öffnen der Einstellungsseite.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            IsEnabled = _index.Settings.Enabled;
            UseGpu = _index.Settings.UseGpu;
        }
        finally
        {
            _loading = false;
        }

        RefreshModel();
        ShowStatus(_index.Status);
        _ = LoadMinFaceSizeAsync();
    }

    private void RefreshModel()
    {
        IsModelReady = _models.State == FaceModelState.Ready;
        ModelFolder = _models.ModelFolder;
        ModelStatusText = IsModelReady
            ? "Modell vorhanden (InsightFace buffalo_l: det_10g + w600k_r50)."
            : _models.IsOverridden
                ? "Modell nicht vorhanden — der Ordner aus LUMORA_FACE_MODELS enthält det_10g.onnx und w600k_r50.onnx nicht."
                : "Modell nicht vorhanden.";
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    internal void ShowStatus(FaceIndexStatus status)
    {
        IndexStatusText = Describe(status);
    }

    /// <summary>Text des Index-Zustands für die Einstellungen.</summary>
    internal static string Describe(FaceIndexStatus status)
    {
        var counts = status.Total > 0 ? $"{status.Done:N0} von {status.Total:N0} Fotos analysiert" : null;
        var provider = status.ExecutionProvider is { } p ? $" · {p}" : string.Empty;
        return status.State switch
        {
            FaceIndexState.Disabled => "Ausgeschaltet.",
            FaceIndexState.ModelMissing => "Eingeschaltet, wartet auf das Modell.",
            FaceIndexState.Running => $"Läuft: {counts ?? "Fotos werden gezählt"}{provider}" + (status.MsPerPhoto > 0 ? $" · {status.MsPerPhoto:N0} ms/Foto" : string.Empty),
            FaceIndexState.Paused => $"Angehalten: {counts}{provider}",
            FaceIndexState.Waiting => "Wartet auf den Katalog…",
            FaceIndexState.Error => $"Fehler: {status.Message}",
            _ => counts is null ? "Bereit." : $"Bereit: {counts}{provider}" + (status.Message is { } m ? $" ({m})" : string.Empty),
        };
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _ = ApplyAsync(() => _index.SetEnabledAsync(value), "Gesichtserkennung");
        }
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (!_loading)
        {
            _ = ApplyAsync(() => _index.SetUseGpuAsync(value), "Grafikkarte verwenden");
        }
    }

    private async Task ApplyAsync(Func<Task> apply, string what)
    {
        try
        {
            await apply().ConfigureAwait(true);
            Message = null;
        }
        catch (Exception ex)
        {
            ShowMessage($"{what} konnte nicht gespeichert werden: {ex.Message}", error: true);
            _logger.LogError(ex, "Gesichtserkennung: {What} konnte nicht gespeichert werden", what);
            Load();
        }
    }

    private bool CanDownload() => !IsDownloading && !IsModelReady && !_models.IsOverridden;

    /// <summary>„Modell herunterladen…“: erst nach Bestätigung im Dialog; Fortschritt in der Karte, abbrechbar.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadModelAsync()
    {
        var info = _models.DownloadInfo;
        var ok = await _dialogs.ConfirmAsync(
            "Modell für die Gesichtserkennung herunterladen?",
            $"Lumora lädt das Modellpaket „InsightFace buffalo_l“ ({info.Size / 1024.0 / 1024.0:N0} MB) von\n{info.Source}\n" +
            "herunter, prüft die Prüfsumme und entpackt davon nur det_10g.onnx (Erkennung) und w600k_r50.onnx (Fingerabdruck) nach\n" +
            $"{_models.ModelFolder}\n\n" +
            "Lizenz: Die InsightFace-Modelle sind nur für die nicht-kommerzielle Nutzung freigegeben.\n\n" +
            "Die Gesichtserkennung läuft danach vollständig auf diesem PC — Fotos und Gesichtsdaten verlassen den Rechner nicht.",
            "Herunterladen").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await StartDownloadAsync().ConfigureAwait(true);
    }

    /// <summary>Download ohne Rückfrage (nach der Bestätigung).</summary>
    internal async Task StartDownloadAsync()
    {
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadText = "Verbindung wird aufgebaut…";
        Message = null;
        try
        {
            var progress = new Progress<FaceModelDownloadProgress>(p =>
            {
                DownloadPercent = p.Total > 0 ? Math.Clamp(100.0 * p.Done / p.Total, 0, 100) : DownloadPercent;
                DownloadText = p.Total > 0
                    ? $"{p.Stage}: {p.Done / 1048576.0:N0} von {p.Total / 1048576.0:N0} MB"
                    : $"{p.Stage}…";
            });
            await _models.DownloadAsync(progress, cts.Token).ConfigureAwait(true);
            ShowMessage("Modell heruntergeladen und geprüft.", error: false);
            _index.RequestRun();
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Download abgebrochen — es wurde nichts gespeichert.", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, error: true);
            _logger.LogWarning(ex, "Gesichtserkennung: Modell-Download gescheitert");
        }
        finally
        {
            _downloadCts = null;
            IsDownloading = false;
            DownloadText = null;
            RefreshModel();
        }
    }

    private bool CanCancelDownload() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload() => _downloadCts?.Cancel();

    private bool CanRebuild() => !IsRebuilding;

    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private async Task RebuildIndexAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "Gesichts-Index neu aufbauen?",
            "Alle Fotos der aktiven Bibliothek werden im Hintergrund erneut analysiert. Bestätigte Zuordnungen von Gesichtern zu " +
            "Personen bleiben erhalten, solange das Gesicht wiedergefunden wird. Bei großen Bibliotheken dauert das Stunden.",
            "Neu aufbauen").ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        IsRebuilding = true;
        try
        {
            var count = await _index.RebuildAsync().ConfigureAwait(true);
            ShowMessage(count == 0 ? "Keine Einträge zurückgesetzt." : $"{count:N0} Fotos werden neu analysiert.", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage($"Index konnte nicht zurückgesetzt werden: {ex.Message}", error: true);
            _logger.LogError(ex, "Gesichtserkennung: Index neu aufbauen fehlgeschlagen");
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    private void ShowMessage(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _messenger.UnregisterAll(this);
    }
}

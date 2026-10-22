using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Assistant;
using Lumora.Shell;

namespace Lumora.Stories.Assistant;

/// <summary>Gemerkte Antwort in den Einstellungen (löschbar → beim nächsten Satz fragt Lumora wieder).</summary>
public sealed class AssistantAliasRow
{
    public AssistantAliasRow(AssistantAliasInfo info, AssistantSettingsViewModel owner)
    {
        Info = info;
        Owner = owner;
    }

    public AssistantAliasInfo Info { get; }

    public AssistantSettingsViewModel Owner { get; }

    public string Text => $"„{Info.Term}“ = {Info.PersonNames}";
}

/// <summary>
/// Einstellungen › Assistent (Mockup 12, ADR-0063): an/aus, Ollama-Status + „Ollama installieren…“ (winget, nur nach
/// Bestätigung mit Name/Quelle/Größe/Lizenz), Sprachmodell + „Herunterladen…“ (Größe vorher aus dem Registry-Manifest,
/// Bestätigung, Fortschritt, Abbrechen), Grafikkarte nutzen, Verlauf merken/löschen, gemerkte Antworten, „Ausprobieren“
/// (zeigt nur das Verständnis, erstellt nichts). Alle Werte gelten je Katalog.
/// </summary>
public sealed partial class AssistantSettingsViewModel : ObservableObject
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private readonly IStoryAssistant _assistant;
    private readonly IAssistantRuntime _runtime;
    private readonly IOllamaInstaller _installer;
    private readonly IModelManifestProbe _probe;
    private readonly IDialogService _dialogs;
    private readonly IMessenger _messenger;
    private readonly ILogger _logger;
    private AssistantSettings _settings = AssistantSettings.Default;
    private CancellationTokenSource? _downloadCts;
    private bool _loading;

    public AssistantSettingsViewModel(
        IStoryAssistant assistant,
        IAssistantRuntime runtime,
        IOllamaInstaller installer,
        IModelManifestProbe probe,
        IDialogService dialogs,
        ILogger<AssistantSettingsViewModel>? logger = null,
        IMessenger? messenger = null)
    {
        _assistant = assistant;
        _runtime = runtime;
        _installer = installer;
        _probe = probe;
        _dialogs = dialogs;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public IReadOnlyList<string> SuggestedModels { get; } = AssistantModels.Suggested.Select(m => m.Name).ToList();

    public ObservableCollection<AssistantAliasRow> Aliases { get; } = new();

    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty] private bool _useGpu = true;

    [ObservableProperty] private bool _rememberHistory = true;

    [ObservableProperty] private string _modelName = AssistantModels.DefaultModel;

    [ObservableProperty] private AssistantRuntimeState _state = AssistantRuntimeState.NotInstalled;

    [ObservableProperty] private string _ollamaText = "wird geprüft …";

    [ObservableProperty] private string _modelText = "wird geprüft …";

    [ObservableProperty] private bool _isOllamaReady;

    [ObservableProperty] private bool _isModelReady;

    [ObservableProperty] private bool _canInstallOllama;

    [ObservableProperty] private bool _canStartOllama;

    [ObservableProperty] private bool _canDownloadModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isWorking;

    public bool IsIdle => !IsWorking;

    [ObservableProperty] private bool _isDownloading;

    [ObservableProperty] private double _downloadProgress;

    [ObservableProperty] private string? _progressText;

    [ObservableProperty] private string? _message;

    [ObservableProperty] private string? _error;

    [ObservableProperty] private string _trySentence = "Alle Fotos vom Hund am See";

    [ObservableProperty] private string? _tryResult;

    /// <summary>Angaben zum gewählten Modell (Mockup: Modell, Quelle, Größe, Lizenz).</summary>
    public AssistantModelInfo ModelInfo => AssistantModels.Describe(ModelName);

    public string ModelSource => AssistantModels.Source;

    public bool HasAliases => Aliases.Count > 0;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _settings = await _assistant.GetSettingsAsync();
            Enabled = _settings.Enabled;
            UseGpu = _settings.UseGpu;
            RememberHistory = _settings.RememberHistory;
            ModelName = _settings.Model;
        }
        catch (Exception ex)
        {
            Error = "Einstellungen nicht lesbar: " + ex.Message;
        }
        finally
        {
            _loading = false;
        }

        await RefreshAsync();
        await LoadAliasesAsync();
    }

    /// <summary>„Erneut prüfen“: Zustand von Ollama und Modell.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var status = await _runtime.GetStatusAsync();
            State = status.State;
            IsOllamaReady = status.State is AssistantRuntimeState.ModelMissing or AssistantRuntimeState.Ready;
            IsModelReady = status.IsReady;
            CanInstallOllama = status.State == AssistantRuntimeState.NotInstalled;
            CanStartOllama = status.State == AssistantRuntimeState.NotRunning;
            CanDownloadModel = status.State == AssistantRuntimeState.ModelMissing;
            OllamaText = status.State switch
            {
                AssistantRuntimeState.NotInstalled => "nicht installiert",
                AssistantRuntimeState.NotRunning => "installiert, läuft nicht",
                _ => "läuft" + (string.IsNullOrWhiteSpace(status.Version) ? string.Empty : " (Version " + status.Version + ")"),
            };
            ModelText = status.State switch
            {
                AssistantRuntimeState.Ready => "geladen",
                AssistantRuntimeState.ModelMissing => "nicht geladen",
                _ => "unbekannt (Ollama läuft nicht)",
            };
        }
        catch (Exception ex)
        {
            Error = "Zustand nicht prüfbar: " + ex.Message;
        }
    }

    partial void OnEnabledChanged(bool value) => _ = SaveAsync();

    partial void OnUseGpuChanged(bool value) => _ = SaveAsync();

    partial void OnRememberHistoryChanged(bool value) => _ = SaveAsync();

    partial void OnModelNameChanged(string value) => OnPropertyChanged(nameof(ModelInfo));

    /// <summary>Modellname übernehmen (Eingabe verlassen bzw. Auswahl).</summary>
    [RelayCommand]
    public async Task ApplyModelAsync()
    {
        await SaveAsync();
        await RefreshAsync();
    }

    private async Task SaveAsync()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _settings = new AssistantSettings(Enabled, ModelName, UseGpu, RememberHistory).Normalize();
            await _assistant.SaveSettingsAsync(_settings);
            _messenger.Send(new AssistantSettingsChangedMessage(_settings));
        }
        catch (Exception ex)
        {
            Error = "Nicht gespeichert: " + ex.Message;
        }
    }

    /// <summary>„Ollama installieren…“: erst Bestätigung mit Name, Quelle, Größe und Lizenz, dann winget.</summary>
    [RelayCommand]
    public async Task InstallOllamaAsync()
    {
        Error = null;
        if (!_installer.IsWingetAvailable)
        {
            Error = WingetOllamaInstaller.NoWingetMessage;
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Ollama installieren",
            $"Name: Ollama ({OllamaPackage.WingetId}) — Hersteller {OllamaPackage.Publisher}\n"
            + $"Quelle: {OllamaPackage.Source}\n"
            + $"Größe: {OllamaPackage.ApproximateSize}\n"
            + $"Lizenz: {OllamaPackage.License}\n\n"
            + "Ollama ist ein lokaler Dienst für Sprachmodelle; er läuft nur auf diesem PC. Windows fragt ggf. noch einmal nach.",
            "Zustimmen und installieren");
        if (!ok)
        {
            return;
        }

        IsWorking = true;
        ProgressText = "Ollama wird installiert …";
        try
        {
            var progress = new Progress<string>(line => ProgressText = line.Length > 120 ? line[..120] + "…" : line);
            var result = await _installer.InstallAsync(progress, CancellationToken.None);
            if (result.Success)
            {
                Message = result.Message;
                _runtime.TryStartOllama();
            }
            else
            {
                Error = result.Message;
            }
        }
        catch (Exception ex)
        {
            Error = "Installation fehlgeschlagen: " + ex.Message;
            _logger.LogWarning(ex, "Assistent: Ollama-Installation fehlgeschlagen");
        }
        finally
        {
            IsWorking = false;
            ProgressText = null;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task StartOllamaAsync()
    {
        Error = null;
        if (!_runtime.TryStartOllama())
        {
            Error = "Ollama ließ sich nicht starten — bitte über das Startmenü starten.";
            return;
        }

        Message = "Ollama wird gestartet …";
        for (var i = 0; i < 10 && State != AssistantRuntimeState.Ready && State != AssistantRuntimeState.ModelMissing; i++)
        {
            await Task.Delay(1000);
            await RefreshAsync();
        }

        Message = IsOllamaReady ? "Ollama läuft." : "Ollama antwortet noch nicht — bitte „Erneut prüfen“.";
    }

    /// <summary>„Herunterladen…“: Größe aus dem Manifest, Bestätigung mit Name/Quelle/Größe/Lizenz, dann Ollama-Pull mit Fortschritt.</summary>
    [RelayCommand]
    public async Task DownloadModelAsync()
    {
        Error = null;
        var info = AssistantModels.Describe(ModelName);
        IsWorking = true;
        ProgressText = "Größe wird ermittelt …";
        long? size;
        try
        {
            size = await _probe.GetSizeAsync(ModelName, CancellationToken.None);
        }
        finally
        {
            IsWorking = false;
            ProgressText = null;
        }

        var sizeText = size is { } bytes ? FormatBytes(bytes) + " (laut Ollama-Bibliothek)" : info.ApproximateSize + " (genaue Größe nicht abrufbar)";
        var ok = await _dialogs.ConfirmAsync(
            "Sprachmodell herunterladen",
            $"Name: {ModelName} — {info.Title}\n"
            + $"Quelle: {AssistantModels.Source}\n"
            + $"Größe: {sizeText}\n"
            + $"Lizenz: {info.License}\n\n"
            + "Das Modell bleibt auf diesem PC und übersetzt nur Ihre Sätze in Filter; es sieht nie Fotos.",
            "Zustimmen und herunterladen");
        if (!ok)
        {
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<OllamaPullProgress>(p =>
            {
                DownloadProgress = p.Fraction * 100;
                ProgressText = p.Total > 0
                    ? $"{FormatBytes(p.Completed)} von {FormatBytes(p.Total)} ({p.Fraction:P0})"
                    : p.Status;
            });
            await _runtime.PullModelAsync(ModelName, progress, _downloadCts.Token);
            Message = $"Das Sprachmodell „{ModelName}“ ist bereit.";
        }
        catch (OperationCanceledException)
        {
            Message = "Download abgebrochen — ein neuer Versuch setzt fort.";
        }
        catch (IntentModelException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = "Download fehlgeschlagen: " + ex.Message;
            _logger.LogWarning(ex, "Assistent: Modell-Download fehlgeschlagen");
        }
        finally
        {
            IsDownloading = false;
            ProgressText = null;
            _downloadCts.Dispose();
            _downloadCts = null;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    public async Task ClearHistoryAsync()
    {
        if (!await _dialogs.ConfirmAsync("Verlauf löschen", "Alle zuletzt gefragten Sätze dieser Bibliothek löschen?", "Löschen", isDestructive: true))
        {
            return;
        }

        var count = await _assistant.ClearHistoryAsync();
        Message = count == 1 ? "1 Satz gelöscht." : $"{count:N0} Sätze gelöscht.";
    }

    private async Task LoadAliasesAsync()
    {
        try
        {
            var aliases = await _assistant.GetAliasesAsync();
            Aliases.Clear();
            foreach (var alias in aliases)
            {
                Aliases.Add(new AssistantAliasRow(alias, this));
            }

            OnPropertyChanged(nameof(HasAliases));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assistent: gemerkte Antworten nicht lesbar");
        }
    }

    [RelayCommand]
    public async Task DeleteAliasAsync(AssistantAliasRow? row)
    {
        if (row is null)
        {
            return;
        }

        await _assistant.DeleteAliasAsync(row.Info.Term);
        await LoadAliasesAsync();
    }

    /// <summary>„Ausprobieren“: nur zeigen, wie der Satz verstanden wird — erstellt nichts, merkt nichts.</summary>
    [RelayCommand]
    public async Task TryAsync()
    {
        TryResult = null;
        Error = null;
        if (string.IsNullOrWhiteSpace(TrySentence))
        {
            return;
        }

        IsWorking = true;
        ProgressText = "Die Sprach-KI liest den Satz …";
        try
        {
            var understanding = await _assistant.UnderstandAsync(TrySentence);
            TryResult = AssistantSummary.Describe(understanding);
        }
        catch (AssistantException ex)
        {
            TryResult = ex.Message;
        }
        catch (Exception ex)
        {
            TryResult = "Nicht verstanden: " + ex.Message;
        }
        finally
        {
            IsWorking = false;
            ProgressText = null;
        }
    }

    internal static string FormatBytes(long bytes)
        => bytes >= 1_000_000_000 ? (bytes / 1_000_000_000.0).ToString("0.0", German) + " GB"
            : bytes >= 1_000_000 ? (bytes / 1_000_000.0).ToString("0", German) + " MB"
            : (bytes / 1000.0).ToString("0", German) + " kB";
}

/// <summary>Verständnis in Worten (Einstellungen › Assistent › Ausprobieren).</summary>
public static class AssistantSummary
{
    public static string Describe(AssistantUnderstanding understanding)
    {
        var plan = understanding.Resolution.Plan;
        var lines = new List<string>
        {
            "Was entsteht: " + plan.Action switch
            {
                IntentAction.Album => "Album",
                IntentAction.Collage => "Collage-Bild",
                IntentAction.Slideshow => "Diashow",
                IntentAction.Gallery => "Galerie",
                _ => "Story",
            },
        };
        if (plan.Persons.Count > 0)
        {
            lines.Add("Personen: " + AssistantTitleBuilder.Names(plan.Persons.Select(p => p.Name).ToList()) + (plan.Persons.Count > 1 ? plan.PersonMatch == IntentMatch.All ? " (alle zusammen)" : " (mindestens eine)" : string.Empty));
        }

        if (plan.Time is { } time)
        {
            lines.Add("Zeitraum: " + time.Description + (time.Source is null ? string.Empty : " (" + time.Source + ")"));
        }

        if (plan.Trip is not null)
        {
            lines.Add("Urlaub: Reise-Erkennung" + (plan.Trip.Extra.Count > 0 ? " + " + string.Join(", ", plan.Trip.Extra.Select(m => m.KindLabel + " „" + m.Label + "“")) : string.Empty));
        }

        foreach (var term in plan.Terms)
        {
            lines.Add($"„{term.Text}“: " + (term.NotFound ? "nicht gefunden" : string.Join(" · ", term.Matches.Select(m => m.Text))));
        }

        if (plan.Media != IntentMedia.All)
        {
            lines.Add(plan.Media == IntentMedia.Photos ? "Nur Fotos" : "Nur Videos");
        }

        if (plan.Best is { } best)
        {
            lines.Add($"Die besten {best}");
        }
        else if (plan.BestSuggested)
        {
            lines.Add("Die schönsten (Anzahl schlägt Lumora vor)");
        }

        foreach (var question in understanding.Resolution.Questions)
        {
            lines.Add("Rückfrage: " + question.Title);
        }

        if (understanding.FromRules)
        {
            lines.Add("(ohne Sprach-KI verstanden — einfache Regeln)");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

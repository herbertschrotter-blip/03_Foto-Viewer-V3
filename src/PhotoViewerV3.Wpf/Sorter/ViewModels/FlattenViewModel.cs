using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Sorter.Services;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>
/// ViewModel von „Flatten &amp; Move“: Quellordner → Analyse (Ordner mit Mediendateien, Zielname
/// nach V2-Regel) → Zielwurzel/Optionen → Vorschau → Ausführen → Rückgängig. Quelle und Ziel wählt
/// <see cref="Location"/> über die Wurzeln der Bibliothek (wie der Sortierer, ADR-0024).
/// </summary>
public sealed partial class FlattenViewModel : ObservableObject
{
    private readonly IFlattenService _flatten;
    private readonly IGalleryDialogService _dialogs;
    private readonly ILogger<FlattenViewModel> _logger;

    private FlattenAnalysis? _analysis;
    private bool _isInitialized;

    public FlattenViewModel(
        IFlattenService flatten,
        IGalleryDialogService dialogs,
        IFolderPickerService folderPicker,
        SorterLibraryLocator libraries,
        ILogger<FlattenViewModel> logger,
        IMessenger? messenger = null)
    {
        _flatten = flatten;
        _dialogs = dialogs;
        _logger = logger;
        Location = new SorterRootSelection(
            libraries,
            folderPicker,
            messenger ?? WeakReferenceMessenger.Default,
            logger,
            "Quellordner wählen (unter einer Wurzel der Bibliothek)",
            "Zielwurzel wählen (unter einer Wurzel der Bibliothek)");
        Location.PropertyChanged += OnLocationPropertyChanged;
        Location.Reloaded += OnLocationReloaded;
    }

    /// <summary>Quelle und Ziel über die Wurzeln der aktiven Bibliothek.</summary>
    public SorterRootSelection Location { get; }

    public ObservableCollection<FlattenFolderRowViewModel> Folders { get; } = new();

    public ObservableCollection<PlanRowViewModel> PlanRows { get; } = new();

    [ObservableProperty]
    private string _separator = "_";

    [ObservableProperty]
    private bool _directToTargetRoot;

    [ObservableProperty]
    private bool _deleteEmptySource = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ExecuteCommand), nameof(IncludeAllCommand), nameof(ExcludeAllCommand))]
    private int _folderCount;

    public bool HasFolders => FolderCount > 0;

    [ObservableProperty]
    private string? _analysisSummary;

    [ObservableProperty]
    private string? _analysisWarnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    private int _planCount;

    public bool HasPlan => PlanCount > 0;

    [ObservableProperty]
    private string? _planSummary;

    /// <summary>Das Ziel des Plans ist gesperrt (außerhalb der Wurzeln oder offline, ADR-0024).</summary>
    [ObservableProperty]
    private bool _planTargetBlocked;

    [ObservableProperty]
    private string? _planTargetError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(
        nameof(AnalyzeCommand), nameof(PreviewCommand), nameof(ExecuteCommand), nameof(UndoCommand),
        nameof(IncludeAllCommand), nameof(ExcludeAllCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    private string? _lastRunText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    public bool CanAnalyze => !IsBusy && !string.IsNullOrWhiteSpace(Location.SourceFolder);

    public bool CanWorkOnFolders => !IsBusy && HasFolders;

    public bool CanExecuteUndo => !IsBusy && CanUndo;

    // ---- Initialisierung -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await RunAsync("Initialisierung", async () =>
        {
            var library = await Location.LoadAsync();
            await RefreshLastRunAsync();
            StatusText = library is null
                ? "Keine Bibliothek vorhanden — bitte zuerst eine Bibliothek anlegen und scannen."
                : "Quellordner wählen und „Analysieren“ klicken.";
        });
    }

    partial void OnIsBusyChanged(bool value) => Location.IsEnabled = !value;

    private void OnLocationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SorterRootSelection.SourceFolder):
                AnalyzeCommand.NotifyCanExecuteChanged();
                break;
            case nameof(SorterRootSelection.TargetRoot):
                InvalidatePlan();
                break;
        }
    }

    /// <summary>Bibliothek gewechselt oder Wurzeln geändert: die Analyse kennt die alten Wurzeln — verwerfen.</summary>
    private void OnLocationReloaded(object? sender, SorterRootsReloadedEventArgs e)
    {
        if ((e.LibraryChanged || e.RootsChanged) && !IsBusy && _analysis is not null)
        {
            _analysis = null;
            RebuildFolderRows();
            AnalysisWarnings = null;
            StatusText = e.LibraryChanged
                ? $"Bibliothek gewechselt ({Location.LibraryName}) — bitte neu analysieren."
                : "Die Wurzeln der Bibliothek haben sich geändert — bitte neu analysieren.";
        }
    }

    // ---- Analyse ---------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => RunAsync("Analyse", async () =>
    {
        _analysis = null;
        RebuildFolderRows();
        AnalysisWarnings = null;

        var options = new FlattenAnalyzeOptions
        {
            Separator = string.IsNullOrEmpty(Separator) ? "_" : Separator,
        };
        _analysis = await _flatten.AnalyzeAsync(Location.SourceFolder, options);
        RebuildFolderRows();
        AnalysisWarnings = _analysis.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, _analysis.Warnings);
        StatusText = _analysis.Folders.Count == 0
            ? "Keine Mediendateien unterhalb dieses Ordners."
            : $"Analyse fertig: {AnalysisSummary}";
    });

    private void RebuildFolderRows()
    {
        foreach (var row in Folders)
        {
            row.PropertyChanged -= OnFolderRowPropertyChanged;
        }

        Folders.Clear();
        InvalidatePlan();

        if (_analysis is null)
        {
            FolderCount = 0;
            AnalysisSummary = null;
            return;
        }

        foreach (var folder in _analysis.Folders)
        {
            var row = new FlattenFolderRowViewModel(folder);
            row.PropertyChanged += OnFolderRowPropertyChanged;
            Folders.Add(row);
        }

        FolderCount = Folders.Count;
        var summary = $"{_analysis.TotalFolders:N0} Ordner · {_analysis.TotalFiles:N0} Dateien · {SorterPaths.FormatSize(_analysis.TotalSize)} · max. Tiefe {_analysis.MaxDepth}";
        if (_analysis.NotInCatalog.Count > 0)
        {
            summary += $" · {_analysis.NotInCatalog.Count:N0} nicht im Katalog (erst neu scannen)";
        }

        AnalysisSummary = summary;
    }

    private void OnFolderRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FlattenFolderRowViewModel.TargetName) or nameof(FlattenFolderRowViewModel.IsIncluded))
        {
            InvalidatePlan();
        }
    }

    [RelayCommand(CanExecute = nameof(CanWorkOnFolders))]
    private void IncludeAll()
    {
        foreach (var row in Folders)
        {
            row.IsIncluded = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWorkOnFolders))]
    private void ExcludeAll()
    {
        foreach (var row in Folders)
        {
            row.IsIncluded = false;
        }
    }

    // ---- Plan / Ausführung -----------------------------------------------------------------------

    private SorterPlan? BuildPlan()
    {
        if (_analysis is null)
        {
            return null;
        }

        return _flatten.BuildPlan(_analysis, new FlattenPlanOptions
        {
            TargetRoot = string.IsNullOrWhiteSpace(Location.TargetRoot) ? null : Location.TargetRoot,
            DirectToTargetRoot = DirectToTargetRoot,
            DeleteEmptySourceFolders = DeleteEmptySource,
        });
    }

    private void ShowPlan(SorterPlan plan)
    {
        PlanRows.Clear();
        foreach (var item in plan.Items)
        {
            PlanRows.Add(new PlanRowViewModel(item, plan.SourceFolder, plan.TargetRoot));
        }

        PlanCount = PlanRows.Count;
        PlanTargetBlocked = plan.TargetError is not null;
        PlanTargetError = plan.TargetError;
        var renamed = plan.Items.Count(i => i.IsReady && i.RenameTo is not null);
        PlanSummary = $"{plan.ReadyCount:N0} bereit ({renamed:N0} mit Dedup-Namen) · {plan.NotInCatalogCount:N0} nicht im Katalog · "
                      + $"{plan.InvalidCount:N0} ungültige Ziele · {plan.AlreadyInTargetCount:N0} bereits im Ziel · "
                      + $"{plan.SkippedCount:N0} übersprungen · {plan.TargetFolders.Count:N0} Zielordner unter „{plan.TargetRoot}“";
    }

    private void InvalidatePlan()
    {
        PlanRows.Clear();
        PlanCount = 0;
        PlanSummary = null;
        PlanTargetBlocked = false;
        PlanTargetError = null;
    }

    [RelayCommand(CanExecute = nameof(CanWorkOnFolders))]
    private void Preview()
    {
        ErrorMessage = null;
        var plan = BuildPlan();
        if (plan is null)
        {
            return;
        }

        ShowPlan(plan);
        StatusText = plan.ReadyCount == 0
            ? "Vorschau: nichts zu verschieben."
            : $"Vorschau: {plan.ReadyCount:N0} Datei(en) würden verschoben.";
    }

    [RelayCommand(CanExecute = nameof(CanWorkOnFolders))]
    private Task ExecuteAsync() => RunAsync("Flatten", async () =>
    {
        var plan = BuildPlan();
        if (plan is null)
        {
            return;
        }

        ShowPlan(plan);
        if (plan.TargetError is not null)
        {
            ErrorMessage = plan.TargetError;
            StatusText = "Nicht ausgeführt — das Ziel ist gesperrt.";
            return;
        }

        if (plan.ReadyCount == 0)
        {
            StatusText = "Nichts zu verschieben — keine Datei ist bereit (siehe Vorschau).";
            return;
        }

        var sourceFolders = plan.Items.Where(i => i.IsReady).Select(i => i.GroupPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var message = $"{plan.ReadyCount:N0} Datei(en) aus {sourceFolders:N0} Ordner(n) werden nach\n{plan.TargetRoot}\nverschoben.";
        if (plan.CrossesRoots)
        {
            message += $"\n\nSie wechseln von {plan.SourceRoot.RoleText} „{plan.SourceRoot.Name}“ nach {plan.TargetLibraryRoot!.RoleText} „{plan.TargetLibraryRoot.Name}“.";
        }

        var skipped = plan.NotInCatalogCount + plan.InvalidCount + plan.AlreadyInTargetCount;
        if (skipped > 0)
        {
            message += $"\n\n{skipped:N0} Datei(en) werden übersprungen (siehe Vorschau).";
        }

        if (plan.DeleteEmptySourceFolders)
        {
            message += "\n\nLeere Quellordner werden danach gelöscht (nur wirklich leere).";
        }

        if (!await _dialogs.ConfirmAsync("Flatten & Move", message, "Ausführen"))
        {
            return;
        }

        var result = await _flatten.ExecuteAsync(plan, CreateProgress());
        _analysis = null;
        RebuildFolderRows();

        var status = $"Flatten: {result.Moved:N0} verschoben, {result.Failed:N0} fehlgeschlagen, {result.Skipped:N0} übersprungen";
        if (result.CreatedFolders.Count > 0)
        {
            status += $" · {result.CreatedFolders.Count:N0} Ordner angelegt";
        }

        if (result.DeletedFolders.Count > 0)
        {
            status += $" · {result.DeletedFolders.Count:N0} leere Ordner gelöscht";
        }

        StatusText = status + " — „Analysieren“ zeigt den neuen Stand.";
        _logger.LogInformation("{Status}", StatusText);
        await RefreshLastRunAsync();

        if (result.Failed > 0)
        {
            await _dialogs.ShowErrorsAsync(
                $"Flatten: {result.Failed:N0} Fehler",
                $"{result.Failed:N0} Datei(en) konnten nicht verschoben werden; übersprungene Dateien sind mit aufgeführt.",
                result.Details.Where(d => !d.Success).Select(d => $"{d.FileName}: {d.Message}").ToList());
        }
    });

    [RelayCommand(CanExecute = nameof(CanExecuteUndo))]
    private Task UndoAsync() => RunAsync("Rückgängig", async () =>
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Letzten Flatten-Lauf rückgängig machen",
            (LastRunText ?? "Der letzte Flatten-Lauf wird rückgängig gemacht.") + "\n\nAlle Dateien werden zurückverschoben (ursprüngliche Namen und Ordner), angelegte leere Ordner entfernt.",
            "Rückgängig");
        if (!confirmed)
        {
            return;
        }

        var result = await _flatten.UndoLastRunAsync(CreateProgress());
        _analysis = null;
        RebuildFolderRows();

        if (result is null)
        {
            StatusText = "Nichts rückgängig zu machen.";
        }
        else
        {
            StatusText = $"Rückgängig: {result.Restored:N0} Schritt(e) zurückgenommen, {result.Failed:N0} fehlgeschlagen"
                         + (result.RemovedFolders.Count > 0 ? $" · {result.RemovedFolders.Count:N0} Ordner entfernt" : string.Empty);
            if (result.Failed > 0)
            {
                await _dialogs.ShowErrorsAsync(
                    $"Rückgängig: {result.Failed:N0} Fehler",
                    "Nicht alle Schritte konnten zurückgenommen werden.",
                    result.Details.Where(d => !d.Success).Select(d => $"{d.FileName}: {d.Message}").ToList());
            }
        }

        await RefreshLastRunAsync();
    });

    private async Task RefreshLastRunAsync()
    {
        var run = await _flatten.GetLastRunAsync();
        if (run is null)
        {
            LastRunText = null;
            CanUndo = false;
            return;
        }

        var when = run.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        LastRunText = run.Undone
            ? $"Letzter Lauf ({when}, {run.Moved:N0} Dateien) wurde rückgängig gemacht."
            : $"Letzter Lauf: {when} · {run.Moved:N0} Datei(en) nach „{run.TargetRoot}“ · {run.BatchIds.Count} Batch(es)";
        CanUndo = !run.Undone;
    }

    // ---- Helfer ----------------------------------------------------------------------------------

    private IProgress<SorterProgress> CreateProgress() => new Progress<SorterProgress>(p =>
    {
        ProgressMax = Math.Max(1, p.Total);
        ProgressValue = p.Completed;
        ProgressText = p.Message;
    });

    private async Task RunAsync(string what, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        ProgressValue = 0;
        ProgressMax = 1;
        ProgressText = null;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (FolderNotInLibraryException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (DirectoryNotFoundException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "{What} fehlgeschlagen", what);
        }
        finally
        {
            IsBusy = false;
            ProgressText = null;
        }
    }
}

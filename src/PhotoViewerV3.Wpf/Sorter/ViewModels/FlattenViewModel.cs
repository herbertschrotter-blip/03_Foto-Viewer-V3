using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Sorter.Services;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>
/// ViewModel von „Flatten &amp; Move“: Quellordner → Analyse (Ordner mit Mediendateien, Zielname
/// nach V2-Regel) → Zielwurzel/Optionen → Vorschau → Ausführen → Rückgängig.
/// </summary>
public sealed partial class FlattenViewModel : ObservableObject
{
    private readonly IFlattenService _flatten;
    private readonly IGalleryDialogService _dialogs;
    private readonly IFolderPickerService _folderPicker;
    private readonly SorterLibraryLocator _libraries;
    private readonly ILogger<FlattenViewModel> _logger;

    private FlattenAnalysis? _analysis;
    private bool _isInitialized;

    public FlattenViewModel(
        IFlattenService flatten,
        IGalleryDialogService dialogs,
        IFolderPickerService folderPicker,
        SorterLibraryLocator libraries,
        ILogger<FlattenViewModel> logger)
    {
        _flatten = flatten;
        _dialogs = dialogs;
        _folderPicker = folderPicker;
        _libraries = libraries;
        _logger = logger;
    }

    public ObservableCollection<FlattenFolderRowViewModel> Folders { get; } = new();

    public ObservableCollection<PlanRowViewModel> PlanRows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    private string? _libraryRootPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRootDisplay), nameof(HasCustomTargetRoot))]
    private string? _targetRoot;

    public string TargetRootDisplay => string.IsNullOrWhiteSpace(TargetRoot) ? "= Quellordner" : TargetRoot;

    public bool HasCustomTargetRoot => !string.IsNullOrWhiteSpace(TargetRoot);

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

    [ObservableProperty]
    private bool _planTargetOutsideLibrary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(
        nameof(AnalyzeCommand), nameof(PreviewCommand), nameof(ExecuteCommand), nameof(UndoCommand),
        nameof(IncludeAllCommand), nameof(ExcludeAllCommand), nameof(PickSourceFolderCommand), nameof(PickTargetRootCommand))]
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

    public bool CanAnalyze => !IsBusy && !string.IsNullOrWhiteSpace(SourceFolder);

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
            var library = await _libraries.GetActiveAsync();
            LibraryRootPath = library?.RootPath;
            if (string.IsNullOrWhiteSpace(SourceFolder) && library is not null)
            {
                SourceFolder = library.RootPath;
            }

            await RefreshLastRunAsync();
            StatusText = library is null
                ? "Keine Bibliothek vorhanden — bitte zuerst eine Bibliothek anlegen und scannen."
                : "Quellordner wählen und „Analysieren“ klicken.";
        });
    }

    // ---- Ordnerwahl ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void PickSourceFolder()
    {
        var folder = _folderPicker.PickFolder("Quellordner wählen (innerhalb der Bibliothek)", FirstExisting(SourceFolder, LibraryRootPath));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            SourceFolder = folder;
        }
    }

    [RelayCommand]
    private void UseLibraryRoot()
    {
        if (!string.IsNullOrEmpty(LibraryRootPath))
        {
            SourceFolder = LibraryRootPath;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void PickTargetRoot()
    {
        var folder = _folderPicker.PickFolder("Zielwurzel wählen", FirstExisting(TargetRoot, SourceFolder, LibraryRootPath));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            TargetRoot = folder;
            InvalidatePlan();
        }
    }

    [RelayCommand]
    private void ClearTargetRoot()
    {
        TargetRoot = null;
        InvalidatePlan();
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
        _analysis = await _flatten.AnalyzeAsync(SourceFolder, options);
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
            TargetRoot = string.IsNullOrWhiteSpace(TargetRoot) ? null : TargetRoot,
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
        PlanTargetOutsideLibrary = !plan.TargetInsideLibrary;
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
        PlanTargetOutsideLibrary = false;
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
        if (plan.ReadyCount == 0)
        {
            StatusText = "Nichts zu verschieben — keine Datei ist bereit (siehe Vorschau).";
            return;
        }

        var sourceFolders = plan.Items.Where(i => i.IsReady).Select(i => i.GroupPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var message = $"{plan.ReadyCount:N0} Datei(en) aus {sourceFolders:N0} Ordner(n) werden nach\n{plan.TargetRoot}\nverschoben.";
        var skipped = plan.NotInCatalogCount + plan.InvalidCount + plan.AlreadyInTargetCount;
        if (skipped > 0)
        {
            message += $"\n\n{skipped:N0} Datei(en) werden übersprungen (siehe Vorschau).";
        }

        if (!plan.TargetInsideLibrary)
        {
            message += "\n\nAchtung: Die Zielwurzel liegt außerhalb der Bibliothek — die Dateien verlassen den Katalog.";
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

    private static string? FirstExisting(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));
}

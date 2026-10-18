using System;
using System.Collections.Generic;
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
/// ViewModel der Seite „Sortieren“: Quellordner → Patterns/Sub-Level → Analyse (Gruppen) →
/// Gruppen bearbeiten → Optionen → Vorschau → Sortieren → Rückgängig. Alle Fachlogik liegt im
/// <see cref="IFileSorterService"/>; hier nur Zustand, Validierung der Eingaben und Dialoge.
/// </summary>
public sealed partial class FileSorterViewModel : ObservableObject
{
    private const string JsonFilter = "JSON-Dateien (*.json)|*.json|Alle Dateien (*.*)|*.*";

    private readonly IFileSorterService _sorter;
    private readonly ISorterProfileStore _store;
    private readonly IGalleryDialogService _dialogs;
    private readonly IFolderPickerService _folderPicker;
    private readonly ISorterFileDialogService _fileDialogs;
    private readonly SorterLibraryLocator _libraries;
    private readonly ILogger<FileSorterViewModel> _logger;

    private SorterAnalysis? _analysis;
    private bool _isInitialized;
    private bool _suppressProfileEvents;

    public FileSorterViewModel(
        IFileSorterService sorter,
        ISorterProfileStore store,
        IGalleryDialogService dialogs,
        IFolderPickerService folderPicker,
        ISorterFileDialogService fileDialogs,
        SorterLibraryLocator libraries,
        ILogger<FileSorterViewModel> logger)
    {
        _sorter = sorter;
        _store = store;
        _dialogs = dialogs;
        _folderPicker = folderPicker;
        _fileDialogs = fileDialogs;
        _libraries = libraries;
        _logger = logger;
    }

    // ---- Gebundene Sammlungen ------------------------------------------------------------------

    public ObservableCollection<PatternRowViewModel> Patterns { get; } = new();

    public ObservableCollection<SubLevelRowViewModel> SubLevels { get; } = new();

    public ObservableCollection<GroupRowViewModel> Groups { get; } = new();

    public ObservableCollection<PlanRowViewModel> PlanRows { get; } = new();

    // ---- Quelle / Bibliothek -------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    private string? _libraryName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibrary))]
    private string? _libraryRootPath;

    public bool HasLibrary => !string.IsNullOrEmpty(LibraryRootPath);

    [ObservableProperty]
    private bool _isMultiLevel;

    [ObservableProperty]
    private bool _includeNonMedia;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatternsError))]
    private string? _patternsError;

    public bool HasPatternsError => !string.IsNullOrEmpty(PatternsError);

    // ---- Analyse ---------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroups))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ExecuteCommand), nameof(IncludeAllGroupsCommand), nameof(ExcludeAllGroupsCommand))]
    private int _groupCount;

    public bool HasGroups => GroupCount > 0;

    [ObservableProperty]
    private string? _analysisSummary;

    [ObservableProperty]
    private string? _analysisWarnings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeMarkedGroupsCommand))]
    private int _markedCount;

    // ---- Optionen --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRootDisplay), nameof(HasCustomTargetRoot))]
    private string? _targetRoot;

    public string TargetRootDisplay => string.IsNullOrWhiteSpace(TargetRoot) ? "= Quellordner" : TargetRoot;

    public bool HasCustomTargetRoot => !string.IsNullOrWhiteSpace(TargetRoot);

    [ObservableProperty]
    private bool _unsortedToFolder;

    [ObservableProperty]
    private bool _deleteEmptySource;

    [ObservableProperty]
    private bool _useFlatLayout;

    // ---- Plan / Ausführung -----------------------------------------------------------------------

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
        nameof(MergeMarkedGroupsCommand), nameof(IncludeAllGroupsCommand), nameof(ExcludeAllGroupsCommand),
        nameof(AddPatternCommand), nameof(SavePatternsCommand), nameof(ImportPatternsCommand), nameof(ExportPatternsCommand),
        nameof(ResetPatternsCommand), nameof(AddSubLevelCommand), nameof(ResetSubLevelsCommand),
        nameof(PickSourceFolderCommand), nameof(PickTargetRootCommand))]
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

    [ObservableProperty]
    private bool _isHelpOpen;

    public bool CanAnalyze => !IsBusy && !string.IsNullOrWhiteSpace(SourceFolder);

    public bool CanWorkOnGroups => !IsBusy && HasGroups;

    public bool CanMerge => !IsBusy && MarkedCount >= 2;

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
            await LoadProfilesAsync();
            var library = await _libraries.GetActiveAsync();
            LibraryName = library?.Name;
            LibraryRootPath = library?.RootPath;
            if (string.IsNullOrWhiteSpace(SourceFolder) && library is not null)
            {
                SourceFolder = library.RootPath;
            }

            await RefreshLastRunAsync();
            StatusText = library is null
                ? "Keine Bibliothek vorhanden — bitte zuerst eine Bibliothek anlegen und scannen."
                : $"Bibliothek „{library.Name}“ · {library.RootPath}";
        });
    }

    private async Task LoadProfilesAsync()
    {
        ReplacePatterns(await _store.GetPatternsAsync());
        ReplaceSubLevels(await _store.GetSubLevelsAsync());
    }

    // ---- Pattern-Profil --------------------------------------------------------------------------

    private void ReplacePatterns(IEnumerable<SorterPattern> patterns)
    {
        _suppressProfileEvents = true;
        try
        {
            foreach (var row in Patterns)
            {
                row.Changed -= OnProfileRowChanged;
            }

            Patterns.Clear();
            foreach (var pattern in patterns)
            {
                AddPatternRow(PatternRowViewModel.FromModel(pattern));
            }
        }
        finally
        {
            _suppressProfileEvents = false;
        }

        ValidateProfiles();
    }

    private void ReplaceSubLevels(IEnumerable<SorterSubLevel> subLevels)
    {
        _suppressProfileEvents = true;
        try
        {
            foreach (var row in SubLevels)
            {
                row.Changed -= OnProfileRowChanged;
            }

            SubLevels.Clear();
            foreach (var level in subLevels)
            {
                AddSubLevelRow(SubLevelRowViewModel.FromModel(level));
            }
        }
        finally
        {
            _suppressProfileEvents = false;
        }

        ValidateProfiles();
    }

    private void AddPatternRow(PatternRowViewModel row)
    {
        row.Changed += OnProfileRowChanged;
        Patterns.Add(row);
    }

    private void AddSubLevelRow(SubLevelRowViewModel row)
    {
        row.Changed += OnProfileRowChanged;
        SubLevels.Add(row);
    }

    private void OnProfileRowChanged(object? sender, EventArgs e)
    {
        if (!_suppressProfileEvents)
        {
            ValidateProfiles();
        }
    }

    /// <summary>Live-Validierung aller Zeilen inkl. doppelter Namen; Ergebnis in <see cref="PatternsError"/>.</summary>
    private void ValidateProfiles()
    {
        var duplicates = Patterns
            .Select(p => p.Name.Trim())
            .Where(n => n.Length > 0)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Patterns)
        {
            row.Validate(duplicates.Contains(row.Name.Trim()));
        }

        foreach (var row in SubLevels)
        {
            row.Validate();
        }

        var errors = Patterns.Count(p => p.HasError) + SubLevels.Count(s => s.HasError);
        PatternsError = errors == 0
            ? null
            : $"{errors} Eintrag/Einträge mit Fehlern — bitte korrigieren (rote Hinweise).";
    }

    private bool ProfilesAreValid => Patterns.All(p => !p.HasError) && SubLevels.All(s => !s.HasError);

    /// <summary>Validiert und speichert Patterns + Sub-Level; false bei Fehlern (Meldung gesetzt).</summary>
    private async Task<bool> SaveProfilesAsync()
    {
        ValidateProfiles();
        if (!ProfilesAreValid)
        {
            ErrorMessage = PatternsError;
            return false;
        }

        try
        {
            await _store.SavePatternsAsync(Patterns.Select(p => p.ToModel()).ToList());
            await _store.SaveSubLevelsAsync(SubLevels.Select(s => s.ToModel()).ToList());
            return true;
        }
        catch (SorterProfileException ex)
        {
            ErrorMessage = "Profil ungültig: " + string.Join(" · ", ex.Errors);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void AddPattern()
    {
        var baseName = "Neues Pattern";
        var name = baseName;
        var n = 2;
        while (Patterns.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {n++}";
        }

        var row = new PatternRowViewModel
        {
            Name = name,
            Regex = @"^([a-zA-Z]+)",
            GroupCaptureText = "1",
            PriorityText = "50",
            Enabled = true,
        };
        AddPatternRow(row);
        ValidateProfiles();
    }

    [RelayCommand]
    private void RemovePattern(PatternRowViewModel? row)
    {
        if (row is null || row.BuiltIn)
        {
            return;
        }

        row.Changed -= OnProfileRowChanged;
        Patterns.Remove(row);
        ValidateProfiles();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SavePatternsAsync() => RunAsync("Profil speichern", async () =>
    {
        if (await SaveProfilesAsync())
        {
            StatusText = "Pattern-Profil gespeichert.";
        }
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ImportPatternsAsync() => RunAsync("Import", async () =>
    {
        var path = _fileDialogs.PickOpenFile("Pattern-Profil importieren (V2-Format)", JsonFilter, null);
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = await _store.ImportPatternsAsync(path);
            ReplacePatterns(imported);
            StatusText = $"{imported.Count} Pattern importiert aus „{Path.GetFileName(path)}“.";
        }
        catch (SorterProfileException ex)
        {
            ErrorMessage = "Import fehlgeschlagen: " + string.Join(" · ", ex.Errors);
        }
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ExportPatternsAsync() => RunAsync("Export", async () =>
    {
        if (!await SaveProfilesAsync())
        {
            return;
        }

        var path = _fileDialogs.PickSaveFile("Pattern-Profil exportieren", JsonFilter, "file-sorter-patterns.json", null);
        if (path is null)
        {
            return;
        }

        await _store.ExportPatternsAsync(path);
        StatusText = $"Pattern-Profil exportiert nach „{path}“.";
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ResetPatternsAsync() => RunAsync("Zurücksetzen", async () =>
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Patterns zurücksetzen",
            "Alle Patterns werden auf die Standardwerte (Prefix, Datum_YYYY-MM-DD, Datum_YYYYMMDD) zurückgesetzt. Eigene Patterns gehen verloren.",
            "Zurücksetzen",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        ReplacePatterns(await _store.ResetPatternsAsync());
        StatusText = "Patterns auf Standard zurückgesetzt.";
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void AddSubLevel()
    {
        AddSubLevelRow(new SubLevelRowViewModel { Name = $"Ebene {SubLevels.Count + 2}", Regex = string.Empty });
        ValidateProfiles();
    }

    [RelayCommand]
    private void RemoveSubLevel(SubLevelRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.Changed -= OnProfileRowChanged;
        SubLevels.Remove(row);
        ValidateProfiles();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ResetSubLevelsAsync() => RunAsync("Zurücksetzen", async () =>
    {
        ReplaceSubLevels(await _store.ResetSubLevelsAsync());
        StatusText = "Sub-Level auf Standard („Variante“) zurückgesetzt.";
    });

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
        var folder = _folderPicker.PickFolder("Zielwurzel wählen (Sort-to-Root)", FirstExisting(TargetRoot, SourceFolder, LibraryRootPath));
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
        ClearResults();
        if (!await SaveProfilesAsync())
        {
            return;
        }

        var options = new SorterAnalyzeOptions
        {
            MultiLevel = IsMultiLevel,
            IncludeNonMediaFiles = IncludeNonMedia,
        };
        _analysis = await _sorter.AnalyzeAsync(SourceFolder, options);
        RebuildGroupRows();
        AnalysisWarnings = _analysis.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, _analysis.Warnings);
        StatusText = _analysis.Groups.Count == 0
            ? "Keine Mediendateien direkt in diesem Ordner."
            : $"Analyse fertig: {AnalysisSummary}";
    });

    private void RebuildGroupRows()
    {
        foreach (var row in Groups)
        {
            row.PropertyChanged -= OnGroupRowPropertyChanged;
        }

        Groups.Clear();
        MarkedCount = 0;
        InvalidatePlan();

        if (_analysis is null)
        {
            GroupCount = 0;
            AnalysisSummary = null;
            return;
        }

        foreach (var group in _analysis.Groups)
        {
            var row = new GroupRowViewModel(group);
            row.PropertyChanged += OnGroupRowPropertyChanged;
            Groups.Add(row);
        }

        GroupCount = Groups.Count;
        var summary = $"{_analysis.Groups.Count:N0} Gruppen · {_analysis.TotalFiles:N0} Dateien";
        if (_analysis.NotInCatalog.Count > 0)
        {
            summary += $" · {_analysis.NotInCatalog.Count:N0} nicht im Katalog (erst neu scannen)";
        }

        if (_analysis.IsMultiLevel)
        {
            summary += $" · Multi-Level mit {_analysis.SubLevels.Count} Ebene(n)";
        }

        AnalysisSummary = summary;
    }

    private void OnGroupRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GroupRowViewModel.IsMarked):
                MarkedCount = Groups.Count(g => g.IsMarked);
                break;
            case nameof(GroupRowViewModel.TargetFolder):
            case nameof(GroupRowViewModel.IsIncluded):
                InvalidatePlan();
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private void MergeMarkedGroups()
    {
        if (_analysis is null)
        {
            return;
        }

        var marked = Groups.Where(g => g.IsMarked).ToList();
        if (marked.Count < 2)
        {
            return;
        }

        var target = marked[0];
        var merged = 0;
        try
        {
            foreach (var source in marked.Skip(1))
            {
                _sorter.MergeGroups(_analysis, source.Key, target.Key);
                merged++;
            }
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        RebuildGroupRows();
        StatusText = $"{merged} Gruppe(n) in „{target.Key}“ zusammengeführt.";
    }

    [RelayCommand(CanExecute = nameof(CanWorkOnGroups))]
    private void IncludeAllGroups()
    {
        foreach (var group in Groups)
        {
            group.IsIncluded = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWorkOnGroups))]
    private void ExcludeAllGroups()
    {
        foreach (var group in Groups)
        {
            group.IsIncluded = false;
        }
    }

    // ---- Plan / Ausführung -----------------------------------------------------------------------

    private SorterPlan? BuildPlan()
    {
        if (_analysis is null)
        {
            return null;
        }

        var mappings = _analysis.Groups.ToDictionary(g => g.Key, g => g.TargetFolder, StringComparer.Ordinal);
        var options = new SorterPlanOptions
        {
            TargetRoot = string.IsNullOrWhiteSpace(TargetRoot) ? null : TargetRoot,
            Unsorted = UnsortedToFolder ? UnsortedHandling.MoveToUnsortedFolder : UnsortedHandling.Skip,
            DeleteEmptySourceFolders = DeleteEmptySource,
            Layout = UseFlatLayout ? SubLevelLayout.Flat : SubLevelLayout.Nested,
        };
        return _sorter.BuildPlan(_analysis, mappings, options);
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
        PlanSummary = $"{plan.ReadyCount:N0} bereit · {plan.ConflictCount:N0} Konflikte · {plan.NotInCatalogCount:N0} nicht im Katalog · "
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

    [RelayCommand(CanExecute = nameof(CanWorkOnGroups))]
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

    [RelayCommand(CanExecute = nameof(CanWorkOnGroups))]
    private Task ExecuteAsync() => RunAsync("Sortieren", async () =>
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

        var skipped = plan.ConflictCount + plan.NotInCatalogCount + plan.InvalidCount + plan.AlreadyInTargetCount;
        var message = $"{plan.ReadyCount:N0} Datei(en) werden in {plan.TargetFolders.Count:N0} Ordner unter\n{plan.TargetRoot}\nverschoben.";
        if (skipped > 0)
        {
            message += $"\n\n{skipped:N0} Datei(en) werden übersprungen (Konflikt, nicht im Katalog, ungültiges Ziel — siehe Vorschau).";
        }

        if (!plan.TargetInsideLibrary)
        {
            message += "\n\nAchtung: Die Zielwurzel liegt außerhalb der Bibliothek — die Dateien verlassen den Katalog.";
        }

        if (plan.DeleteEmptySourceFolders)
        {
            message += "\n\nDer Quellordner wird gelöscht, wenn er danach wirklich leer ist.";
        }

        if (!await _dialogs.ConfirmAsync("Sortieren", message, "Sortieren"))
        {
            return;
        }

        var result = await _sorter.ExecuteAsync(plan, CreateProgress());
        await AfterExecutionAsync("Sortieren", result);
    });

    private async Task AfterExecutionAsync(string verb, SorterExecutionResult result)
    {
        _analysis = null;
        RebuildGroupRows();

        var status = $"{verb}: {result.Moved:N0} verschoben, {result.Failed:N0} fehlgeschlagen, {result.Skipped:N0} übersprungen";
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
                $"{verb}: {result.Failed:N0} Fehler",
                $"{result.Failed:N0} Datei(en) konnten nicht verschoben werden; übersprungene Dateien sind mit aufgeführt.",
                result.Details.Where(d => !d.Success).Select(d => $"{d.FileName}: {d.Message}").ToList());
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteUndo))]
    private Task UndoAsync() => RunAsync("Rückgängig", async () =>
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Letzten Lauf rückgängig machen",
            (LastRunText ?? "Der letzte Sortier-Lauf wird rückgängig gemacht.") + "\n\nAlle Dateien werden zurückverschoben, angelegte leere Ordner entfernt.",
            "Rückgängig");
        if (!confirmed)
        {
            return;
        }

        var result = await _sorter.UndoLastRunAsync(CreateProgress());
        _analysis = null;
        RebuildGroupRows();

        if (result is null)
        {
            StatusText = "Nichts rückgängig zu machen.";
        }
        else
        {
            StatusText = $"Rückgängig: {result.Restored:N0} Datei(en) zurückverschoben, {result.Failed:N0} fehlgeschlagen"
                         + (result.RemovedFolders.Count > 0 ? $" · {result.RemovedFolders.Count:N0} Ordner entfernt" : string.Empty);
            if (result.Failed > 0)
            {
                await _dialogs.ShowErrorsAsync(
                    $"Rückgängig: {result.Failed:N0} Fehler",
                    "Nicht alle Dateien konnten zurückverschoben werden.",
                    result.Details.Where(d => !d.Success).Select(d => $"{d.FileName}: {d.Message}").ToList());
            }
        }

        await RefreshLastRunAsync();
    });

    private async Task RefreshLastRunAsync()
    {
        var run = await _sorter.GetLastRunAsync();
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

    [RelayCommand]
    private void ToggleHelp() => IsHelpOpen = !IsHelpOpen;

    // ---- Helfer ----------------------------------------------------------------------------------

    private IProgress<SorterProgress> CreateProgress() => new Progress<SorterProgress>(p =>
    {
        ProgressMax = Math.Max(1, p.Total);
        ProgressValue = p.Completed;
        ProgressText = p.Message;
    });

    private void ClearResults()
    {
        _analysis = null;
        RebuildGroupRows();
        AnalysisWarnings = null;
    }

    /// <summary>Sperrt die Seite, fängt Fehler als Meldung ab (Bibliotheks-/Ordnerfehler mit ihrem Text).</summary>
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

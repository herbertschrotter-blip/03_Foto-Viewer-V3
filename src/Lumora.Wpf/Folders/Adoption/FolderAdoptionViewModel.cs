using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Gallery.ArchiveTarget;
using Lumora.Services.Database;
using Lumora.Services.Operations;

namespace Lumora.Folders.Adoption;

/// <summary>Zeile der Vorschau „alt → neu“: ein Zielordner mit Einrückung, Dateianzahl und Hinweisen.</summary>
public sealed record AdoptionPreviewRow(string Name, string RelativeTarget, double Indent, int FileCount, string SourceText, string Notes)
{
    public bool HasNotes => Notes.Length > 0;

    public string CountText => FileCount == 0 ? "–" : FileCount == 1 ? "1 Datei" : $"{FileCount:N0} Dateien";
}

/// <summary>Auswahl des Dialogs: berechneter Plan und Optionen.</summary>
public sealed record FolderAdoptionChoice(FolderAdoptionPlan Plan, FolderAdoptionOptions Options);

/// <summary>
/// ViewModel des Dialogs „Ordner in die Bibliothek übernehmen…“ (ADR-0034). Der Zielordner-Baum ist derselbe wie im
/// Dialog „In die Bibliothek…“ (<see cref="ArchiveTargetViewModel"/> als <see cref="Target"/>: Katalog- und
/// Dateisystem-Ordner des Bibliotheksordners, Suche, „Neuer Ordner“, zuletzt benutztes Ziel vorgewählt). Jede Änderung
/// von Ziel oder „Zusammenziehen“ berechnet den Plan neu (Thread-Pool) und zeigt ihn als Baum „alt → neu“.
/// </summary>
public sealed partial class FolderAdoptionViewModel : ObservableObject
{
    private const int MaxPreviewRows = 400;

    private readonly FolderAdoptionContext _context;
    private readonly AdoptionSourceSnapshot _snapshot;
    private readonly IFolderAdoptionService _adoption;
    private readonly ILogger _logger;
    private int _previewVersion;

    public FolderAdoptionViewModel(
        FolderAdoptionContext context,
        AdoptionSourceSnapshot snapshot,
        ILibraryContextFactory factory,
        IFileOperationService fileOps,
        IFolderAdoptionService adoption,
        ILogger? logger = null)
    {
        _context = context;
        _snapshot = snapshot;
        _adoption = adoption;
        _logger = logger ?? NullLogger.Instance;
        Target = new ArchiveTargetViewModel(
            new ArchiveTargetRequest(context.LibraryId, context.ArchiveRootId, context.ArchiveRootPath, context.ArchiveRootName, Array.Empty<int>(), context.LastTargetPath, null),
            factory,
            fileOps,
            logger);
        Target.PropertyChanged += OnTargetChanged;
    }

    /// <summary>Zielordner-Baum unter dem Bibliotheksordner (geteilt mit „In die Bibliothek…“).</summary>
    public ArchiveTargetViewModel Target { get; }

    public string SourceName => Path.GetFileName(_context.SourceFolder);

    public string Title => $"Ordner „{SourceName}“ in die Bibliothek übernehmen";

    public string SourceText => _context.IsWholeInbox
        ? $"Eingang „{_context.InboxRootName}“ (ganzer Eingang): {_context.SourceFolder} · {FileCountText(_snapshot.Root.TotalFileCount)}"
        : $"Aus dem Eingang „{_context.InboxRootName}“: {_context.SourceFolder} · {FileCountText(_snapshot.Root.TotalFileCount)}";

    public bool IsWholeInbox => _context.IsWholeInbox;

    public string UnregisterText => $"Eingang „{_context.InboxRootName}“ danach austragen (nur aus dem Katalog)";

    public ObservableCollection<AdoptionPreviewRow> PreviewRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private FolderAdoptionPlan? _plan;

    [ObservableProperty]
    private string? _previewSummary;

    [ObservableProperty]
    private string? _previewError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _isComputing;

    [ObservableProperty]
    private bool _collapseEmptyChains = true;

    [ObservableProperty]
    private bool _recycleSource = true;

    [ObservableProperty]
    private bool _unregisterInbox = true;

    public bool CanConfirm => !IsComputing && Plan is { IsEmpty: false } && Target.CanConfirm
                              && string.Equals(Plan.TargetFolder, NormalizeTarget(Target.TargetPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Hinweis unter den Optionen: was mit Quelle und Eingang passiert.</summary>
    public string RecycleHint => _context.IsWholeInbox && !UnregisterInbox
        ? "Der Eingangsordner bleibt (leer) bestehen, nur sein Inhalt kommt in den Papierkorb."
        : "Nur wenn danach keine Datei mehr darin liegt — sonst bleibt er und die Reste werden gemeldet.";

    public async Task LoadAsync()
    {
        await Target.LoadAsync().ConfigureAwait(true);
        await RefreshPreviewAsync().ConfigureAwait(true);
    }

    /// <summary>Ergebnis für den Aufrufer (null, solange nichts Gültiges berechnet ist).</summary>
    public FolderAdoptionChoice? BuildChoice() => CanConfirm
        ? new FolderAdoptionChoice(Plan!, new FolderAdoptionOptions
        {
            CollapseEmptyChains = CollapseEmptyChains,
            RecycleSource = RecycleSource,
            UnregisterInbox = IsWholeInbox && UnregisterInbox,
        })
        : null;

    partial void OnCollapseEmptyChainsChanged(bool value) => _ = RefreshPreviewAsync();

    partial void OnUnregisterInboxChanged(bool value) => OnPropertyChanged(nameof(RecycleHint));

    private void OnTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArchiveTargetViewModel.TargetPath))
        {
            _ = RefreshPreviewAsync();
        }
        else if (e.PropertyName is nameof(ArchiveTargetViewModel.CanConfirm))
        {
            OnPropertyChanged(nameof(CanConfirm));
        }
    }

    /// <summary>Plan neu berechnen; nur das Ergebnis der jüngsten Anfrage wird angezeigt.</summary>
    public async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        var target = NormalizeTarget(Target.TargetPath);
        if (target is null || !Target.CanConfirm)
        {
            Plan = null;
            PreviewRows.Clear();
            PreviewSummary = null;
            PreviewError = "Zielordner im Bibliotheksordner wählen.";
            return;
        }

        IsComputing = true;
        var collapse = CollapseEmptyChains;
        try
        {
            var plan = await Task.Run(() => _adoption.BuildPlan(_snapshot, target, collapse)).ConfigureAwait(true);
            if (version != _previewVersion)
            {
                return;
            }

            Plan = plan;
            PreviewError = plan.IsEmpty ? "Der Ordner enthält keine Dateien." : null;
            FillRows(plan);
            PreviewSummary = Summarize(plan);
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
            {
                Plan = null;
                PreviewRows.Clear();
                PreviewSummary = null;
                PreviewError = $"Vorschau nicht möglich: {ex.Message}";
                _logger.LogWarning(ex, "Übernahme-Vorschau fehlgeschlagen: {Source} → {Target}", _context.SourceFolder, target);
            }
        }
        finally
        {
            if (version == _previewVersion)
            {
                IsComputing = false;
            }
        }
    }

    private void FillRows(FolderAdoptionPlan plan)
    {
        PreviewRows.Clear();
        var sourceParent = Path.GetDirectoryName(_context.SourceFolder) ?? _context.SourceFolder;
        var renamedByFolder = plan.Files.Where(f => f.Renamed)
            .GroupBy(f => Path.GetDirectoryName(f.DestPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in plan.Folders.Take(MaxPreviewRows))
        {
            var notes = new List<string>();
            if (folder.CollapsedPaths.Count > 0)
            {
                notes.Add(folder.CollapsedPaths.Count == 1 ? "1 leerer Zwischenordner entfällt" : $"{folder.CollapsedPaths.Count} leere Zwischenordner entfallen");
            }

            if (folder.MergesExisting)
            {
                notes.Add("vorhandener Ordner — wird zusammengeführt");
            }

            if (folder.MergesSources)
            {
                notes.Add($"{folder.SourcePaths.Count} Quellordner zusammengeführt");
            }

            if (renamedByFolder.TryGetValue(folder.TargetPath, out var renamed))
            {
                notes.Add(renamed == 1 ? "1 Datei bekommt „_1“" : $"{renamed} Dateien bekommen „_1“, „_2“ …");
            }

            var sources = string.Join(", ", folder.SourcePaths.Select(p => Path.GetRelativePath(sourceParent, p)));
            PreviewRows.Add(new AdoptionPreviewRow(
                Path.GetFileName(folder.TargetPath),
                folder.RelativeTargetPath,
                Math.Max(0, folder.Depth - 1) * 18,
                folder.FileCount,
                $"← {sources}",
                string.Join(" · ", notes)));
        }
    }

    private string Summarize(FolderAdoptionPlan plan)
    {
        if (plan.IsEmpty)
        {
            return "Nichts zu übernehmen.";
        }

        var parts = new List<string> { $"{FileCountText(plan.FileCount)} → {FolderCountText(plan.Folders.Count)} unter „{Path.GetFileName(plan.TargetFolder)}“" };
        if (plan.CollapsedFolders.Count > 0)
        {
            parts.Add($"{plan.CollapsedFolders.Count:N0} leere Zwischenordner entfallen");
        }

        if (plan.MergedFolderCount > 0)
        {
            parts.Add($"{plan.MergedFolderCount:N0} Ordner zusammengeführt");
        }

        if (plan.RenamedCount > 0)
        {
            parts.Add($"{plan.RenamedCount:N0} Dateien wegen gleicher Namen umbenannt");
        }

        if (plan.IgnoredEntries.Count > 0)
        {
            parts.Add($"{plan.IgnoredEntries.Count:N0} Systemeinträge (z. B. .thumbs, Thumbs.db) werden nicht übernommen und gehen mit dem Quellordner in den Papierkorb");
        }

        if (plan.Folders.Count > MaxPreviewRows)
        {
            parts.Add($"Vorschau zeigt die ersten {MaxPreviewRows} Ordner");
        }

        return string.Join(" · ", parts);
    }

    private string? NormalizeTarget(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : ArchiveTargetViewModel.NormalizePath(path);

    private static string FileCountText(int n) => n == 1 ? "1 Datei" : $"{n:N0} Dateien";

    private static string FolderCountText(int n) => n == 1 ? "1 Ordner" : $"{n:N0} Ordner";
}

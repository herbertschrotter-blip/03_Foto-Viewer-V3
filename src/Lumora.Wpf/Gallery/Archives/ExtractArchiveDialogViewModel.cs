using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Data;
using Lumora.Gallery.Services;
using Lumora.Services.Archive;

namespace Lumora.Gallery.Archives;

/// <summary>
/// ViewModel des Entpacken-Dialogs: Optionen (Ziel, vorhandene Dateien, nur Medien, verschachtelt, Papierkorb),
/// Lauf mit Fortschritt und Abbruch, Ergebnis mit Fehlerliste. Mehrere Archive laufen nacheinander; der
/// Incremental-Scan der Bibliothek läuft einmal nach dem letzten Archiv. UI-Thread; der Service arbeitet auf dem Pool.
/// </summary>
public sealed partial class ExtractArchiveDialogViewModel : ObservableObject
{
    private readonly ArchiveExtractDialogRequest _request;
    private readonly IArchiveService _archives;
    private readonly IFolderPickerService _folderPicker;
    private readonly ILogger _logger;
    private readonly List<ArchiveExtractResult> _results = new();
    private CancellationTokenSource? _cts;

    public ExtractArchiveDialogViewModel(ArchiveExtractDialogRequest request, IArchiveService archives, IFolderPickerService folderPicker, ILogger logger)
    {
        _request = request;
        _archives = archives;
        _folderPicker = folderPicker;
        _logger = logger;
        Targets = request.Targets;
        _customTargetFolder = request.LibraryRootPath ?? string.Empty;
    }

    public event EventHandler? CloseRequested;

    public IReadOnlyList<ArchiveExtractTarget> Targets { get; }

    public IReadOnlyList<ArchiveExtractResult> Results => _results;

    public string Title => Targets.Count == 1 ? $"„{Targets[0].Name}“ entpacken" : $"{Targets.Count} Archive entpacken";

    public string TargetsText => Targets.Count == 1
        ? Targets[0].Path
        : string.Join(", ", Targets.Take(5).Select(t => t.Name)) + (Targets.Count > 5 ? $" … (+{Targets.Count - 5})" : string.Empty);

    /// <summary>Hinweis unter der Zielwahl: wohin die Dateien kommen.</summary>
    public string DefaultTargetHint => Targets.Count == 1
        ? _archives.GetDefaultTargetFolder(Targets[0].Path)
        : "je Archiv ein Unterordner mit dem Archivnamen neben dem Archiv";

    public string CustomTargetHint => Targets.Count == 1
        ? "Die Dateien landen direkt in diesem Ordner."
        : "Je Archiv entsteht darin ein Unterordner mit dem Archivnamen.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCustomTarget))]
    private bool _useDefaultTarget = true;

    public bool UseCustomTarget
    {
        get => !UseDefaultTarget;
        set => UseDefaultTarget = !value;
    }

    [ObservableProperty] private string _customTargetFolder;
    [ObservableProperty] private ArchiveOverwriteMode _overwriteMode = ArchiveOverwriteMode.Skip;
    [ObservableProperty] private bool _mediaOnly = true;
    [ObservableProperty] private bool _extractNested = true;
    [ObservableProperty] private bool _recycleAfterExtract;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOptions), nameof(ShowProgress))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(BrowseFolderCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOptions), nameof(ShowProgress))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isFinished;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string? _currentEntry;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private bool _resultIsWarning;

    public ObservableCollection<string> ErrorLines { get; } = new();

    public bool CanEditOptions => !IsRunning && !IsFinished;
    public bool ShowProgress => IsRunning || IsFinished;
    public bool HasErrors => ErrorLines.Count > 0;

    private bool CanStart => !IsRunning && !IsFinished;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!UseDefaultTarget && string.IsNullOrWhiteSpace(CustomTargetFolder))
        {
            ResultText = "Bitte einen Zielordner wählen.";
            ResultIsWarning = true;
            return;
        }

        IsRunning = true;
        ResultText = null;
        ResultIsWarning = false;
        ErrorLines.Clear();
        OnPropertyChanged(nameof(HasErrors));
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var index = 0;
        try
        {
            foreach (var target in Targets)
            {
                index++;
                var prefix = Targets.Count > 1 ? $"Archiv {index}/{Targets.Count}: " : string.Empty;
                ProgressText = $"{prefix}{target.Name} wird gelesen…";
                CurrentEntry = null;
                var progress = new Progress<ArchiveExtractProgress>(p => ReportProgress(prefix, p));
                var options = new ArchiveExtractOptions
                {
                    TargetFolder = ResolveTargetFolder(target),
                    OverwriteMode = OverwriteMode,
                    MediaOnly = MediaOnly,
                    ExtractNested = ExtractNested,
                    RecycleArchiveAfterExtract = RecycleAfterExtract,
                    ArchiveFileId = target.FileId,
                    LibraryId = _request.LibraryId,
                    ScanAfterExtract = index == Targets.Count,
                };

                try
                {
                    var result = await _archives.ExtractAsync(target.Path, options, progress, token);
                    _results.Add(result);
                    foreach (var error in result.Errors)
                    {
                        ErrorLines.Add($"{target.Name} → {error.Entry}: {error.Message}");
                    }

                    if (result.Canceled)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Passwort, kaputtes Archiv, Zielordner nicht beschreibbar: klare Meldung, nächstes Archiv.
                    ErrorLines.Add($"{target.Name}: {ex.Message}");
                    _logger.LogWarning(ex, "Entpacken fehlgeschlagen: {Path}", target.Path);
                }
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            IsFinished = true;
            OnPropertyChanged(nameof(HasErrors));
            ShowSummary(token.IsCancellationRequested);
        }
    }

    private string? ResolveTargetFolder(ArchiveExtractTarget target)
    {
        if (UseDefaultTarget)
        {
            return null;
        }

        var custom = CustomTargetFolder.Trim();
        return Targets.Count == 1 ? custom : Path.Combine(custom, ArchiveService.GetArchiveBaseName(target.Name));
    }

    private void ReportProgress(string prefix, ArchiveExtractProgress p)
    {
        var round = p.Round > 0 ? $" (verschachtelt, Ebene {p.Round}: {p.ArchiveName})" : string.Empty;
        var bytes = p.BytesTotal > 0 ? $" · {GalleryFileDto.FormatSize(p.BytesDone)} / {GalleryFileDto.FormatSize(p.BytesTotal)}" : string.Empty;
        ProgressText = $"{prefix}{p.FilesDone} / {p.FilesTotal} Dateien{bytes}{round}";
        CurrentEntry = p.CurrentEntry;
        ProgressPercent = p.BytesTotal > 0
            ? Math.Clamp(100.0 * p.BytesDone / p.BytesTotal, 0, 100)
            : p.FilesTotal > 0 ? Math.Clamp(100.0 * p.FilesDone / p.FilesTotal, 0, 100) : 0;
    }

    private void ShowSummary(bool canceled)
    {
        var files = _results.Sum(r => r.FilesExtracted);
        var skipped = _results.Sum(r => r.FilesSkipped);
        var nested = _results.Sum(r => r.NestedArchivesExtracted);
        var recycled = _results.Count(r => r.ArchiveRecycled);
        var added = _results.Select(r => r.Scan).Where(s => s is not null).Sum(s => s!.Added);
        var parts = new List<string> { $"{files:N0} Datei(en) entpackt" };
        if (skipped > 0)
        {
            parts.Add($"{skipped:N0} übersprungen (schon vorhanden)");
        }

        if (nested > 0)
        {
            parts.Add($"{nested:N0} verschachtelte(s) Archiv(e)");
        }

        if (recycled > 0)
        {
            parts.Add($"{recycled:N0} Archiv(e) im Papierkorb");
        }

        if (_results.Any(r => r.Scan is not null))
        {
            parts.Add($"{added:N0} neu im Katalog");
        }

        if (ErrorLines.Count > 0)
        {
            parts.Add($"{ErrorLines.Count:N0} Fehler");
        }

        ResultText = (canceled || _results.Any(r => r.Canceled) ? "Abgebrochen: " : "Fertig: ") + string.Join(", ", parts) + ".";
        ResultIsWarning = ErrorLines.Count > 0 || canceled;
        ProgressPercent = canceled ? ProgressPercent : 100;
        CurrentEntry = null;
        if (_results.Count > 0)
        {
            ProgressText = Targets.Count == 1 ? _results[0].TargetFolder : $"{_results.Count} von {Targets.Count} Archiven verarbeitet";
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanEditOptions))]
    private void BrowseFolder()
    {
        var picked = _folderPicker.PickFolder("Zielordner für das Entpacken", string.IsNullOrWhiteSpace(CustomTargetFolder) ? _request.LibraryRootPath : CustomTargetFolder);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CustomTargetFolder = picked;
            UseDefaultTarget = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fenster wird geschlossen (X/Esc): laufenden Lauf abbrechen; true = Schließen erlaubt.</summary>
    public bool OnClosing()
    {
        if (!IsRunning)
        {
            return true;
        }

        _cts?.Cancel();
        return false;
    }
}

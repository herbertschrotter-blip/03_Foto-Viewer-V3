using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Library;

/// <summary>
/// Bibliotheksverwaltung: Liste aller Bibliotheken, Anlegen (Ordnerdialog), Scannen (Fresh bzw. ClearAndRescan
/// nach Bestätigung) mit Fortschrittspanel und Abbruch, Aktivieren, Umbenennen, Entfernen.
/// Nach erfolgreichem Scan wird automatisch zur Galerie gewechselt.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private static readonly TimeSpan ElapsedTickInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryService _libraries;
    private readonly IScanService _scanner;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _scanStopwatch = new();
    private CancellationTokenSource? _scanCts;

    public LibraryViewModel(
        ILibraryService libraries,
        IScanService scanner,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<LibraryViewModel> logger)
    {
        _libraries = libraries;
        _scanner = scanner;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _elapsedTimer = new DispatcherTimer(ElapsedTickInterval, DispatcherPriority.Background, OnElapsedTick, dispatcher);
        _elapsedTimer.Stop();
    }

    public ObservableCollection<LibraryListItem> Libraries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isErrorOpen;

    // ---- Scan-Panel ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanPanelVisible))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RemoveCommand), nameof(CreateLibraryCommand), nameof(SetActiveCommand), nameof(RenameCommand), nameof(CancelScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string? _scanLibraryName;

    [ObservableProperty]
    private string _scanPhaseText = string.Empty;

    [ObservableProperty]
    private int _scanFolders;

    [ObservableProperty]
    private int _scanFiles;

    [ObservableProperty]
    private string _scanSizeText = "0 B";

    [ObservableProperty]
    private string? _scanCurrentPath;

    [ObservableProperty]
    private string _scanElapsedText = "0,0 s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanPanelVisible))]
    private bool _isScanSummaryOpen;

    [ObservableProperty]
    private string _scanSummaryTitle = string.Empty;

    [ObservableProperty]
    private string? _scanSummary;

    [ObservableProperty]
    private bool _scanSummaryIsWarning;

    public bool IsScanPanelVisible => IsScanning || IsScanSummaryOpen;

    public bool IsEmptyStateVisible => !IsLoading && Libraries.Count == 0;

    partial void OnErrorMessageChanged(string? value) => IsErrorOpen = !string.IsNullOrEmpty(value);

    private bool CanModify() => !IsScanning;

    private bool CanModifyItem(LibraryListItem? item) => !IsScanning;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var all = await _libraries.GetAllAsync().ConfigureAwait(true);

            // Aktiv = jüngstes LastOpenedAt, sonst jüngstes CreatedAt (gleiche Regel wie ILibraryService.GetActiveAsync)
            var activeId = all
                .OrderByDescending(l => l.LastOpenedAt ?? DateTime.MinValue)
                .ThenByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Select(l => (int?)l.Id)
                .FirstOrDefault();

            Libraries.Clear();
            foreach (var info in all)
            {
                Libraries.Add(new LibraryListItem(info, info.Id == activeId));
            }

            _logger.LogInformation("Bibliotheken geladen: {Count} (aktiv: {ActiveId})", all.Count, activeId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliotheken konnten nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Bibliotheken konnten nicht geladen werden");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmptyStateVisible));
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task CreateLibraryAsync()
    {
        var folder = _dialogs.PickFolder("Foto-Ordner für die neue Bibliothek wählen");
        if (folder is null)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync(
            "Neue Bibliothek",
            $"Ordner: {folder}\n\nName der Bibliothek:",
            LibraryService.SuggestName(folder),
            "Anlegen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        LibraryInfo info;
        try
        {
            info = await _libraries.CreateAsync(name, folder).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht angelegt werden: {ex.Message}";
            _logger.LogError(ex, "Bibliothek konnte nicht angelegt werden: {Folder}", folder);
            return;
        }

        ErrorMessage = null;
        await LoadAsync().ConfigureAwait(true);

        var scanNow = await _dialogs.ConfirmAsync(
            "Bibliothek angelegt",
            $"„{info.Name}“ wurde angelegt. Soll der Ordner jetzt durchsucht und der Katalog aufgebaut werden?",
            "Jetzt scannen",
            cancelText: "Später").ConfigureAwait(true);
        if (scanNow && Libraries.FirstOrDefault(l => l.Id == info.Id) is { } item)
        {
            await StartScanAsync(item, ScanMode.Fresh).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task ScanAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        var mode = item.IsEmpty ? ScanMode.Fresh : ScanMode.ClearAndRescan;
        if (mode == ScanMode.ClearAndRescan)
        {
            var ok = await _dialogs.ConfirmAsync(
                "Bibliothek neu scannen?",
                "Alle Katalogdaten dieser Bibliothek werden neu aufgebaut; Favoriten und Bewertungen gehen dabei verloren.\n\n" +
                "Hinweis: Der echte Diff-Rescan, der nur Änderungen übernimmt und Favoriten behält, kommt in Welle 2.",
                "Neu aufbauen",
                isDestructive: true).ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }

        await StartScanAsync(item, mode).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        _logger.LogInformation("Scan-Abbruch angefordert");
        _scanCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task SetActiveAsync(LibraryListItem? item)
    {
        if (item is null || item.IsActive)
        {
            return;
        }

        try
        {
            await _libraries.SetActiveAsync(item.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht aktiviert werden: {ex.Message}";
            _logger.LogError(ex, "SetActive fehlgeschlagen: {Id}", item.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task RenameAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        var newName = await _dialogs.PromptTextAsync("Bibliothek umbenennen", "Neuer Name:", item.Name, "Umbenennen")
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name)
        {
            return;
        }

        try
        {
            await _libraries.RenameAsync(item.Id, newName).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogError(ex, "Rename fehlgeschlagen: {Id}", item.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyItem))]
    private async Task RemoveAsync(LibraryListItem? item)
    {
        if (item is null)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Bibliothek aus dem Katalog entfernen?",
            $"„{item.Name}“ wird mit allen Katalogdaten (Ordner, Dateien, Vorschaubilder, Favoriten, Bewertungen) " +
            "aus dem Katalog entfernt.\n\n" +
            "Die Fotos und Videos auf der Festplatte bleiben unverändert — es werden keine Dateien gelöscht.",
            "Entfernen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        try
        {
            await _libraries.DeleteAsync(item.Id).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bibliothek konnte nicht entfernt werden: {ex.Message}";
            _logger.LogError(ex, "Delete fehlgeschlagen: {Id}", item.Id);
        }
    }

    [RelayCommand]
    private void GoToGallery() => _navigation.NavigateTo(NavigationTags.Gallery);

    [RelayCommand]
    private void DismissScanSummary() => IsScanSummaryOpen = false;

    /// <summary>Scan ohne Rückfragen starten (Aufrufer hat bestätigt). Aktiviert die Bibliothek vorher, falls nötig.</summary>
    internal async Task StartScanAsync(LibraryListItem item, ScanMode mode)
    {
        if (IsScanning)
        {
            return;
        }

        if (!item.IsActive)
        {
            // Galerie und ScanCompletedMessage beziehen sich auf die aktive Bibliothek → vor dem Scan aktivieren
            await _libraries.SetActiveAsync(item.Id).ConfigureAwait(true);
        }

        ErrorMessage = null;
        IsScanSummaryOpen = false;
        ScanLibraryName = item.Name;
        ScanPhaseText = Formatting.ScanPhase(ScanPhase.Enumerating);
        ScanFolders = 0;
        ScanFiles = 0;
        ScanSizeText = Formatting.Bytes(0);
        ScanCurrentPath = null;
        ScanElapsedText = Formatting.Duration(TimeSpan.Zero);

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        _scanStopwatch.Restart();
        _elapsedTimer.Start();

        // Progress<T> stellt die Callbacks auf den UI-Thread (SynchronizationContext beim Erzeugen)
        var progress = new Progress<ScanProgress>(ApplyProgress);
        _logger.LogInformation("Scan gestartet: {Name} (Id={Id}) Modus={Mode}", item.Name, item.Id, mode);

        try
        {
            var result = await _scanner.ScanAsync(item.Id, mode, progress, _scanCts.Token).ConfigureAwait(true);
            ShowSummary(
                "Scan abgeschlossen",
                $"„{item.Name}“: {result.FilesInserted:N0} Dateien in {result.FoldersInserted:N0} Ordnern " +
                $"({Formatting.Bytes(result.TotalBytes)}) in {Formatting.Duration(result.TotalDuration)} katalogisiert.",
                warning: false);
            _logger.LogInformation(
                "Scan fertig: {Name} — {Files} Dateien, {Folders} Ordner, {Fts} FTS-Zeilen in {Sec:N1} s",
                item.Name, result.FilesInserted, result.FoldersInserted, result.FtsRowsInserted, result.TotalDuration.TotalSeconds);

            await LoadAsync().ConfigureAwait(true);
            _navigation.NavigateTo(NavigationTags.Gallery);
        }
        catch (OperationCanceledException)
        {
            ShowSummary("Scan abgebrochen", "Der Scan wurde abgebrochen; unvollständige Daten wurden verworfen.", warning: true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Scan fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "Scan fehlgeschlagen: {Name} (Id={Id})", item.Name, item.Id);
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            _elapsedTimer.Stop();
            _scanStopwatch.Stop();
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    /// <summary>Rauchtest-Pfad (SmokeTestRunner): Bibliothek auf rootPath anlegen, falls nötig, und scannen.</summary>
    internal async Task RunSmokeScanAsync(string rootPath)
    {
        await LoadAsync().ConfigureAwait(true);

        var normalized = LibraryService.NormalizeRootPath(rootPath);
        var item = Libraries.FirstOrDefault(l =>
            string.Equals(LibraryService.NormalizeRootPath(l.RootPath), normalized, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            var info = await _libraries.CreateAsync(LibraryService.SuggestName(normalized), normalized).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            item = Libraries.First(l => l.Id == info.Id);
        }

        await StartScanAsync(item, item.IsEmpty ? ScanMode.Fresh : ScanMode.ClearAndRescan).ConfigureAwait(true);
    }

    /// <summary>Rauchtest-Pfad: Bibliothek auf rootPath wieder aus dem Katalog entfernen.</summary>
    internal async Task RunSmokeCleanupAsync(string rootPath)
    {
        await LoadAsync().ConfigureAwait(true);

        var normalized = LibraryService.NormalizeRootPath(rootPath);
        var item = Libraries.FirstOrDefault(l =>
            string.Equals(LibraryService.NormalizeRootPath(l.RootPath), normalized, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        await _libraries.DeleteAsync(item.Id).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    private void ApplyProgress(ScanProgress p)
    {
        ScanFolders = p.FoldersDiscovered;
        ScanFiles = p.FilesDiscovered;
        ScanSizeText = Formatting.Bytes(p.TotalBytesDiscovered);
        ScanCurrentPath = p.CurrentPath;
        ScanPhaseText = Formatting.ScanPhase(p.Phase);
    }

    private void ShowSummary(string title, string text, bool warning)
    {
        ScanSummaryTitle = title;
        ScanSummary = text;
        ScanSummaryIsWarning = warning;
        IsScanSummaryOpen = true;
    }

    private void OnElapsedTick(object? sender, EventArgs e) =>
        ScanElapsedText = Formatting.Duration(_scanStopwatch.Elapsed);
}

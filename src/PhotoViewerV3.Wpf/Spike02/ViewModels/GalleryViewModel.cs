using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Gallery.Services;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Spike02.Data;

namespace PhotoViewerV3.Spike02.ViewModels;

/// <summary>Eintrag der Sortier-Auswahl in der Toolbar.</summary>
public sealed record GallerySortOption(GallerySort Sort, string Label);

/// <summary>
/// ViewModel der Galerie (ADR-0010): hält nur die aktuelle Page (≤ 1000 Items) als
/// <see cref="GalleryItem"/>, nie die ganze Library. Alle DB-Zugriffe laufen über
/// <see cref="GalleryPageSource"/> auf dem Thread-Pool; das ViewModel selbst arbeitet auf dem
/// UI-Thread — Nachrichten und Thumbnail-Callbacks von Worker-Threads werden dorthin dispatcht.
/// </summary>
public sealed partial class GalleryViewModel : ObservableObject
{
    private const int TileImageWidth = 180;
    private const int ReloadDebounceMs = 300;

    /// <summary>So lange bleibt eine Ergebnis-Meldung (Verschieben, Papierkorb, Favorit …) stehen, bevor der Lade-Status sie ersetzt.</summary>
    private static readonly TimeSpan ResultStatusHold = TimeSpan.FromSeconds(8);

    private static readonly GallerySortOption[] s_sortOptions =
    [
        new(GallerySort.ModifiedDesc, "Datum neu → alt"),
        new(GallerySort.ModifiedAsc, "Datum alt → neu"),
        new(GallerySort.NameAsc, "Name A → Z"),
        new(GallerySort.NameDesc, "Name Z → A"),
        new(GallerySort.SizeDesc, "Größe groß → klein"),
        new(GallerySort.SizeAsc, "Größe klein → groß"),
    ];

    private static readonly int[] s_pageSizeOptions = [200, 500, 1000];

    private readonly ILibraryContextFactory _contextFactory;
    private readonly GalleryPageSource _source;
    private readonly IThumbnailService _thumbnails;
    private readonly ICatalogUpdateService _catalog;
    private readonly IFileOperationService _fileOps;
    private readonly ILightboxService _lightbox;
    private readonly IGalleryDialogService _dialogs;
    private readonly IFolderPickerService _folderPicker;
    private readonly ILogger<GalleryViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    private readonly Dictionary<int, GalleryItem> _currentItems = new();
    private readonly HashSet<int> _selectedIds = new();

    /// <summary>
    /// Unveränderlicher Schnappschuss der Ids in <see cref="_currentItems"/> für den Thumbnail-Callback,
    /// der auf Worker-Threads läuft: <see cref="_currentItems"/> wird nur auf dem UI-Thread verändert und darf
    /// dort nicht nebenläufig gelesen werden. Wird nach jeder Änderung durch eine neue Instanz ersetzt.
    /// </summary>
    private volatile HashSet<int> _currentIdsSnapshot = new();

    private CancellationTokenSource? _activeLoadCts;
    private CancellationTokenSource? _reloadDebounceCts;
    private DateTime _resultStatusUntil;
    private bool _isInitialized;
    private bool _suppressReload;
    private bool _suppressSelectionEvents;
    private DateTime _startupBegin;
    private int _libraryId;
    private string? _libraryRootPath;

    public GalleryViewModel(
        ILibraryContextFactory contextFactory,
        GalleryPageSource source,
        IThumbnailService thumbnails,
        ICatalogUpdateService catalog,
        IFileOperationService fileOps,
        ILightboxService lightbox,
        IGalleryDialogService dialogs,
        IFolderPickerService folderPicker,
        ILogger<GalleryViewModel> logger)
    {
        _contextFactory = contextFactory;
        _source = source;
        _thumbnails = thumbnails;
        _catalog = catalog;
        _fileOps = fileOps;
        _lightbox = lightbox;
        _dialogs = dialogs;
        _folderPicker = folderPicker;
        _logger = logger;

        CurrentPage = new ObservableCollection<GalleryItem>();
        Subfolders = new ObservableCollection<GallerySubfolderDto>();
        _thumbnails.ThumbnailReady += OnThumbnailReady;
        RegisterMessages();
    }

    // ---- Gebundene Daten ---------------------------------------------------------------------

    public ObservableCollection<GalleryItem> CurrentPage { get; }

    /// <summary>Unterordner-Kacheln im Container-State (Ordner ohne direkte Dateien, PLAN Kap. 16).</summary>
    public ObservableCollection<GallerySubfolderDto> Subfolders { get; }

    public IReadOnlyList<GallerySortOption> SortOptions => s_sortOptions;
    public IReadOnlyList<int> PageSizeOptions => s_pageSizeOptions;

    /// <summary>
    /// Liefert das Owner-Window für die Lightbox; wird von der gerade geladenen View gesetzt (Galerie-Seite
    /// und Ordner-Seite teilen dieses ViewModel). Fällt auf das Hauptfenster zurück, wenn keine View geladen ist.
    /// </summary>
    public Func<Window?>? OwnerWindowResolver { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPageIndex), nameof(CanGoBack), nameof(CanGoForward))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand), nameof(FirstPageCommand), nameof(LastPageCommand))]
    private int _pageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(CanGoForward))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(LastPageCommand))]
    private int _pageSize = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(CanGoForward), nameof(IsEmpty), nameof(EmptyMessage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(LastPageCommand))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private GalleryFilter _filter = GalleryFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private string? _searchTerm;

    [ObservableProperty]
    private GallerySortOption _selectedSort = s_sortOptions[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoForward), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand), nameof(FirstPageCommand), nameof(LastPageCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusText = "Bereit.";
    [ObservableProperty] private double _lastPageLoadMs;
    [ObservableProperty] private double _firstPageLoadMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _errorMessage;

    [ObservableProperty] private string _dbPathDisplay = string.Empty;
    [ObservableProperty] private string _libraryNameDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowGrid), nameof(ShowNoLibrary))]
    private bool _hasLibrary;

    [ObservableProperty] private bool _isRootOffline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderFilter), nameof(FolderChipText), nameof(EmptyMessage))]
    private int? _folderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderChipText))]
    private string? _folderName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderChipText))]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowGrid))]
    private bool _showSubfolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText), nameof(CanActOnSelection))]
    [NotifyCanExecuteChangedFor(
        nameof(FavoriteSelectionCommand), nameof(UnfavoriteSelectionCommand),
        nameof(SetSelectionRatingCommand), nameof(ClearSelectionRatingCommand),
        nameof(MoveSelectionCommand), nameof(RecycleSelectionCommand))]
    private int _selectedCount;

    /// <summary>Gemeinsame Bewertung aller ausgewählten Dateien (null, wenn unterschiedlich oder leer).</summary>
    [ObservableProperty] private byte? _selectionRating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActOnSelection), nameof(CanExecuteUndo))]
    [NotifyCanExecuteChangedFor(
        nameof(FavoriteSelectionCommand), nameof(UnfavoriteSelectionCommand),
        nameof(SetSelectionRatingCommand), nameof(ClearSelectionRatingCommand),
        nameof(MoveSelectionCommand), nameof(RecycleSelectionCommand), nameof(UndoCommand))]
    private bool _isOperationRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int DisplayPageIndex => PageIndex + 1;
    public bool CanGoBack => !IsLoading && PageIndex > 0;
    public bool CanGoForward => !IsLoading && PageIndex < PageCount - 1;

    public bool HasFolderFilter => FolderId is not null;
    public string FolderChipText => IncludeSubfolders
        ? $"Ordner: {FolderName} (mit Unterordnern)"
        : $"Ordner: {FolderName}";

    public bool HasSelection => SelectedCount > 0;
    public bool CanActOnSelection => SelectedCount > 0 && !IsOperationRunning;
    public bool CanExecuteUndo => CanUndo && !IsOperationRunning;
    public string SelectionText => SelectedCount == 1 ? "1 ausgewählt" : $"{SelectedCount:N0} ausgewählt";

    public bool ShowNoLibrary => !HasLibrary;
    public bool ShowGrid => HasLibrary && !ShowSubfolders;
    public bool IsEmpty => HasLibrary && !IsLoading && !ShowSubfolders && TotalCount == 0 && ErrorMessage is null;

    public string EmptyMessage
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchTerm))
            {
                return $"Keine Treffer für „{SearchTerm.Trim()}“.";
            }

            if (FolderId is not null)
            {
                return "In diesem Ordner liegen keine Dateien, die zum Filter passen.";
            }

            return Filter switch
            {
                GalleryFilter.Images => "Keine Bilder in dieser Bibliothek.",
                GalleryFilter.Videos => "Keine Videos in dieser Bibliothek.",
                GalleryFilter.Favorites => "Noch keine Favoriten — markiere Dateien mit dem Herz.",
                _ => "Diese Bibliothek enthält noch keine Dateien. Starte unter „Bibliothek“ einen Scan.",
            };
        }
    }

    // ---- Lebenszyklus --------------------------------------------------------------------------

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _startupBegin = DateTime.UtcNow;
        DbPathDisplay = _contextFactory.DatabasePath;

        await ReloadLibraryAsync(null);

        FirstPageLoadMs = (DateTime.UtcNow - _startupBegin).TotalMilliseconds;
        if (HasLibrary)
        {
            _logger.LogInformation("Start Gallery → erste Page sichtbar: {Ms:N1} ms", FirstPageLoadMs);
        }
    }

    /// <summary>Setzt Filter/Ordner/Suche/Page zurück und lädt die (neue) aktive Library.</summary>
    private async Task ReloadLibraryAsync(int? libraryId)
    {
        _activeLoadCts?.Cancel();
        _reloadDebounceCts?.Cancel();
        ResetViewState();

        if (!await LoadLibraryAsync(libraryId))
        {
            return;
        }

        try
        {
            _source.Open();
            _logger.LogInformation("Gallery-DB geöffnet: {Path}", _source.DatabasePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Datenbank konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogError(ex, "Fehler beim Öffnen der Gallery-DB");
            return;
        }

        await ReloadTotalAndPageAsync(restoreSelection: false);
        await RefreshCanUndoAsync();
    }

    private void ResetViewState()
    {
        _suppressReload = true;
        try
        {
            Filter = GalleryFilter.All;
            SearchTerm = null;
            FolderId = null;
            FolderName = null;
            IncludeSubfolders = true;
            SelectedSort = s_sortOptions[0];
            PageIndex = 0;
            TotalCount = 0;
            ErrorMessage = null;
            ShowSubfolders = false;
            Subfolders.Clear();
            ClearItems();
            ClearSelectionState();
        }
        finally
        {
            _suppressReload = false;
        }
    }

    /// <summary>Lädt die Library mit der Id bzw. (null) die aktive Library (ADR-0003: jüngstes LastOpenedAt).</summary>
    private async Task<bool> LoadLibraryAsync(int? libraryId)
    {
        try
        {
            await using var ctx = _contextFactory.Create();
            var lib = libraryId is { } id
                ? await SelectInfo(ctx.Libraries.Where(l => l.Id == id)).FirstOrDefaultAsync()
                : null;
            lib ??= await SelectInfo(ctx.Libraries
                    .OrderByDescending(l => l.LastOpenedAt)
                    .ThenByDescending(l => l.CreatedAt))
                .FirstOrDefaultAsync();

            if (lib is null)
            {
                _libraryId = 0;
                _libraryRootPath = null;
                HasLibrary = false;
                LibraryNameDisplay = string.Empty;
                StatusText = "Keine Bibliothek vorhanden.";
                _logger.LogWarning("Keine Library in DB gefunden — Gallery bleibt leer");
                return false;
            }

            _libraryId = lib.Id;
            _libraryRootPath = lib.RootPath;
            LibraryNameDisplay = $"{lib.Name}  ·  {lib.RootPath}";
            HasLibrary = true;

            // Offline-Check kann bei UNC-Pfaden blockieren → Thread-Pool.
            var rootPath = lib.RootPath;
            IsRootOffline = !await Task.Run(() => Directory.Exists(rootPath));
            if (IsRootOffline)
            {
                _logger.LogWarning("Library-Root nicht erreichbar (offline): {RootPath}", rootPath);
            }

            _logger.LogInformation("Aktive Library: Id={Id} Name={Name} RootPath={RootPath}", lib.Id, lib.Name, lib.RootPath);
            return true;
        }
        catch (Exception ex)
        {
            HasLibrary = false;
            ErrorMessage = $"Bibliothek konnte nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Fehler beim Lesen der Libraries-Tabelle");
            return false;
        }

        static IQueryable<LibraryInfoRow> SelectInfo(IQueryable<Library> query)
            => query.Select(l => new LibraryInfoRow(l.Id, l.Name, l.RootPath));
    }

    private sealed record LibraryInfoRow(int Id, string Name, string RootPath);

    // ---- Nachrichten (WeakReferenceMessenger, ggf. von Worker-Threads) ------------------------

    private void RegisterMessages()
    {
        _messenger.Register<GalleryViewModel, ActiveLibraryChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleActiveLibraryChangedAsync(m)));
        _messenger.Register<GalleryViewModel, ScanCompletedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleScanCompletedAsync(m)));
        _messenger.Register<GalleryViewModel, FolderSelectedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleFolderSelectedAsync(m)));
        _messenger.Register<GalleryViewModel, FilesChangedMessage>(
            this, static (r, m) => r.RunOnUi(() => r.HandleFilesChangedAsync(m)));
    }

    private Task HandleActiveLibraryChangedAsync(ActiveLibraryChangedMessage m)
    {
        _logger.LogInformation("ActiveLibraryChangedMessage: LibraryId={LibraryId}", m.LibraryId);
        return ReloadLibraryAsync(m.LibraryId);
    }

    private Task HandleScanCompletedAsync(ScanCompletedMessage m)
    {
        _logger.LogInformation("ScanCompletedMessage: LibraryId={LibraryId} Files={Files}", m.LibraryId, m.Result.FilesInserted);
        if (!HasLibrary)
        {
            return ReloadLibraryAsync(m.LibraryId);
        }

        return m.LibraryId == _libraryId
            ? ReloadTotalAndPageAsync(restoreSelection: true)
            : Task.CompletedTask;
    }

    private Task HandleFolderSelectedAsync(FolderSelectedMessage m)
    {
        if (!HasLibrary || m.LibraryId != _libraryId)
        {
            return Task.CompletedTask;
        }

        if (m.FolderId == FolderId && m.IncludeSubfolders == IncludeSubfolders)
        {
            return Task.CompletedTask; // bereits aktiv (z. B. von uns selbst gesendet)
        }

        _logger.LogInformation("FolderSelectedMessage: FolderId={FolderId} Rekursiv={Rek}", m.FolderId, m.IncludeSubfolders);
        return ApplyFolderFilterAsync(m.FolderId, m.IncludeSubfolders);
    }

    private async Task HandleFilesChangedAsync(FilesChangedMessage m)
    {
        if (!HasLibrary || m.LibraryId != _libraryId)
        {
            return;
        }

        _logger.LogDebug("FilesChangedMessage: Kind={Kind} Count={Count}", m.Kind, m.FileIds.Count);
        if (m.Kind == FilesChangeKind.Metadata && Filter != GalleryFilter.Favorites)
        {
            await RefreshMetadataAsync(m.FileIds);
        }
        else
        {
            // Moved/Removed/Added (oder Favoriten-Filter aktiv): Page neu laden, gebündelt.
            ScheduleReload();
        }

        // Favorit/Bewertung ändern nichts an den OperationBatches — Undo-Status nur bei Datei-Operationen prüfen.
        if (m.Kind != FilesChangeKind.Metadata)
        {
            await RefreshCanUndoAsync();
        }
    }

    private async Task RefreshMetadataAsync(IReadOnlyList<int> fileIds)
    {
        var inPage = fileIds.Where(_currentItems.ContainsKey).ToList();
        if (inPage.Count == 0)
        {
            return;
        }

        try
        {
            var rows = await _source.GetMetadataAsync(inPage, CancellationToken.None);
            foreach (var row in rows)
            {
                if (_currentItems.TryGetValue(row.Id, out var item))
                {
                    item.UpdateMetadata(row.IsFavorite, row.Rating, row.Missing);
                }
            }

            UpdateSelectionSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadaten-Refresh fehlgeschlagen");
        }
    }

    /// <summary>Debounced (300 ms) Reload der aktuellen Page mit Erhalt der Auswahl.</summary>
    private void ScheduleReload()
    {
        _reloadDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDebounceCts = cts;
        FireAndForget(async () =>
        {
            try
            {
                await Task.Delay(ReloadDebounceMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ReloadTotalAndPageAsync(restoreSelection: true);
        });
    }

    // ---- Laden ---------------------------------------------------------------------------------

    private CancellationToken BeginLoad()
    {
        _activeLoadCts?.Cancel();
        _activeLoadCts = new CancellationTokenSource();
        return _activeLoadCts.Token;
    }

    /// <summary>Zählt neu und lädt die aktuelle Page (inkl. Container-State-Prüfung).</summary>
    private async Task ReloadTotalAndPageAsync(bool restoreSelection)
    {
        if (!HasLibrary || !_source.IsOpen)
        {
            return;
        }

        var ct = BeginLoad();
        IsLoading = true;
        try
        {
            ErrorMessage = null;
            var sw = Stopwatch.StartNew();
            var query = BuildQuery();
            var total = await _source.GetTotalCountAsync(query, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            TotalCount = total;
            if (PageIndex > PageCount - 1)
            {
                PageIndex = PageCount - 1;
            }

            if (total == 0 && query.HasFolder && !query.IncludeSubfolders && !query.HasSearch)
            {
                var subfolders = await _source.GetSubfoldersAsync(_libraryId, query.FolderId!.Value, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (subfolders.Count > 0)
                {
                    ClearItems();
                    ClearSelectionState();
                    Subfolders.Clear();
                    foreach (var sub in subfolders)
                    {
                        Subfolders.Add(sub);
                    }

                    ShowSubfolders = true;
                    SetLoadStatus($"Ordner „{FolderName}“ enthält keine Dateien, aber {subfolders.Count} Unterordner.");
                    _logger.LogInformation("Container-State: FolderId={FolderId} Unterordner={Count}", query.FolderId, subfolders.Count);
                    return;
                }
            }

            if (ShowSubfolders)
            {
                ShowSubfolders = false;
                Subfolders.Clear();
            }

            await LoadPageCoreAsync(query, ct, restoreSelection);
            sw.Stop();
            _logger.LogInformation(
                "Reload: Total={Total} Seite={Page}/{Pages} in {Ms:N1} ms",
                TotalCount, DisplayPageIndex, PageCount, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Fehler beim Reload");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Pager: nur die Page wechseln (Count bleibt), Auswahl wird verworfen.</summary>
    private async Task NavigateToPageAsync(int pageIndex)
    {
        if (!HasLibrary || !_source.IsOpen || ShowSubfolders)
        {
            return;
        }

        PageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        var ct = BeginLoad();
        IsLoading = true;
        try
        {
            ErrorMessage = null;
            ClearSelectionState();
            await LoadPageCoreAsync(BuildQuery(), ct, restoreSelection: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Fehler beim Page-Load");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadPageCoreAsync(GalleryQuery query, CancellationToken ct, bool restoreSelection)
    {
        var sw = Stopwatch.StartNew();
        var rows = await _source.LoadPageAsync(query, PageIndex, PageSize, TotalCount, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        sw.Stop();
        LastPageLoadMs = sw.Elapsed.TotalMilliseconds;

        List<GalleryItem> newItems = [];
        await DispatchAsync(() => newItems = ApplyRows(rows, restoreSelection));

        SetLoadStatus($"{TotalCount:N0} Treffer · Seite {DisplayPageIndex} von {PageCount:N0} · {LastPageLoadMs:N1} ms");
        _logger.LogInformation(
            "Page geladen: Idx={Idx} Size={Size} Rows={Rows} Time={Ms:N1}ms Filter={Filter} Sort={Sort} Folder={Folder} Rekursiv={Rek} Search={Search}",
            PageIndex, PageSize, rows.Count, LastPageLoadMs, Filter, SelectedSort.Sort,
            FolderId?.ToString() ?? "-", IncludeSubfolders, SearchTerm ?? "-");

        // Thumbnails nur anstoßen (Service dedupliziert, ist thread-sicher). Auf dem Thread-Pool,
        // damit der synchrone Vorlauf des Services (DB-Batch-Check) den UI-Thread nicht blockiert.
        FireAndForget(() => Task.Run(() => RequestThumbnailsAsync(newItems)));
    }

    private GalleryQuery BuildQuery()
    {
        var term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim();
        return new GalleryQuery(_libraryId, Filter, term, FolderId, IncludeSubfolders, SelectedSort.Sort);
    }

    /// <summary>
    /// Übernimmt die geladenen Zeilen in <see cref="CurrentPage"/>, möglichst ohne die Items
    /// neu zu erzeugen: unveränderte Dateien behalten Instanz und Thumbnail (kein Flackern),
    /// geänderte werden ersetzt (Thumbnail wird bei gleichem Änderungsdatum übernommen).
    /// Gibt die Items zurück, für die noch ein Thumbnail angefordert werden muss.
    /// </summary>
    private List<GalleryItem> ApplyRows(IReadOnlyList<GalleryFileDto> rows, bool restoreSelection)
    {
        _suppressSelectionEvents = true;
        try
        {
            var target = new List<GalleryItem>(rows.Count);
            var replacedById = new Dictionary<int, GalleryItem>();
            var newIds = new HashSet<int>(rows.Count);

            foreach (var dto in rows)
            {
                newIds.Add(dto.Id);
                if (_currentItems.TryGetValue(dto.Id, out var existing) && existing.CoreMatches(dto))
                {
                    existing.UpdateMetadata(dto.IsFavorite, dto.Rating, dto.Missing);
                    target.Add(existing);
                    continue;
                }

                var item = new GalleryItem(dto);
                if (existing is not null)
                {
                    if (existing.Thumbnail is not null && existing.ModifiedAtUnix == dto.ModifiedAtUnix)
                    {
                        item.Thumbnail = existing.Thumbnail;
                    }

                    replacedById[dto.Id] = item;
                }

                target.Add(item);
            }

            // 1) Verschwundene entfernen, ersetzte Instanzen austauschen
            for (var i = CurrentPage.Count - 1; i >= 0; i--)
            {
                var current = CurrentPage[i];
                if (!newIds.Contains(current.Id))
                {
                    DetachItem(current);
                    CurrentPage.RemoveAt(i);
                }
                else if (replacedById.TryGetValue(current.Id, out var replacement))
                {
                    DetachItem(current);
                    CurrentPage[i] = replacement;
                    AttachItem(replacement);
                    replacedById.Remove(current.Id);
                }
            }

            // 2) Reihenfolge herstellen, fehlende einfügen
            for (var i = 0; i < target.Count; i++)
            {
                var wanted = target[i];
                if (i < CurrentPage.Count && ReferenceEquals(CurrentPage[i], wanted))
                {
                    continue;
                }

                var j = IndexOfFrom(CurrentPage, wanted, i + 1);
                if (j >= 0)
                {
                    CurrentPage.Move(j, i);
                }
                else
                {
                    CurrentPage.Insert(i, wanted);
                    AttachItem(wanted);
                }
            }

            while (CurrentPage.Count > target.Count)
            {
                DetachItem(CurrentPage[^1]);
                CurrentPage.RemoveAt(CurrentPage.Count - 1);
            }

            _currentItems.Clear();
            foreach (var item in target)
            {
                _currentItems[item.Id] = item;
            }

            PublishCurrentIds();

            // 3) Auswahl: nach Ids wiederherstellen oder verwerfen
            if (restoreSelection)
            {
                _selectedIds.IntersectWith(newIds);
            }
            else
            {
                _selectedIds.Clear();
            }

            foreach (var item in target)
            {
                item.IsSelected = _selectedIds.Contains(item.Id);
            }

            UpdateSelectionSummary();
            return target.Where(it => it.Thumbnail is null).ToList();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private static int IndexOfFrom(ObservableCollection<GalleryItem> list, GalleryItem item, int start)
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private void ClearItems()
    {
        foreach (var item in CurrentPage)
        {
            DetachItem(item);
        }

        CurrentPage.Clear();
        _currentItems.Clear();
        PublishCurrentIds();
    }

    private void PublishCurrentIds() => _currentIdsSnapshot = new HashSet<int>(_currentItems.Keys);

    private async Task RequestThumbnailsAsync(IReadOnlyList<GalleryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var requests = new List<ThumbnailRequest>(items.Count);
        foreach (var it in items)
        {
            requests.Add(new ThumbnailRequest(_libraryId, it.Id, it.SourcePath, it.MediaType, it.SourceModifiedAtUtc));
        }

        try
        {
            await _thumbnails.RequestThumbnailsAsync(requests);
        }
        catch (Exception ex)
        {
            // Root offline / IO-Fehler: nur loggen, Platzhalter bleiben stehen.
            _logger.LogWarning(ex, "Thumbnail-Anforderung fehlgeschlagen");
        }
    }

    /// <summary>Läuft auf einem Worker-Thread des ThumbnailService — nur den unveränderlichen Id-Schnappschuss lesen.</summary>
    private void OnThumbnailReady(ThumbnailReadyEventArgs args)
    {
        if (args.LibraryId != _libraryId || !_currentIdsSnapshot.Contains(args.FileId))
        {
            return;
        }

        // Bitmap auf dem Worker-Thread laden + freezen (hält den Decode vom UI-Thread fern),
        // dann nur das Property-Setzen auf den UI-Thread dispatchen.
        var image = TryLoadThumbnail(args.ThumbnailPath);
        if (image is null)
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_currentItems.TryGetValue(args.FileId, out var item))
            {
                item.Thumbnail = image;
            }
        });
    }

    private static ImageSource? TryLoadThumbnail(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = TileImageWidth;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bmp.StreamSource = fs;
                bmp.EndInit();
            }

            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ---- Auswahl -------------------------------------------------------------------------------

    private void AttachItem(GalleryItem item) => item.PropertyChanged += OnItemPropertyChanged;
    private void DetachItem(GalleryItem item) => item.PropertyChanged -= OnItemPropertyChanged;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectionEvents || sender is not GalleryItem item)
        {
            return;
        }

        if (e.PropertyName == nameof(GalleryItem.IsSelected))
        {
            if (item.IsSelected)
            {
                _selectedIds.Add(item.Id);
            }
            else
            {
                _selectedIds.Remove(item.Id);
            }

            UpdateSelectionSummary();
        }
        else if (e.PropertyName == nameof(GalleryItem.Rating) && item.IsSelected)
        {
            UpdateSelectionSummary();
        }
    }

    private void UpdateSelectionSummary()
    {
        SelectedCount = _selectedIds.Count;

        byte? common = null;
        var first = true;
        var uniform = true;
        foreach (var id in _selectedIds)
        {
            if (!_currentItems.TryGetValue(id, out var item))
            {
                continue;
            }

            if (first)
            {
                common = item.Rating;
                first = false;
            }
            else if (item.Rating != common)
            {
                uniform = false;
                break;
            }
        }

        SelectionRating = uniform ? common : null;
    }

    private void ClearSelectionState()
    {
        _suppressSelectionEvents = true;
        try
        {
            foreach (var item in CurrentPage)
            {
                item.IsSelected = false;
            }

            _selectedIds.Clear();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateSelectionSummary();
    }

    private IReadOnlyList<int> SelectedIdsSnapshot() => _selectedIds.ToArray();

    [RelayCommand]
    private void SelectAll()
    {
        _suppressSelectionEvents = true;
        try
        {
            _selectedIds.Clear();
            foreach (var item in CurrentPage)
            {
                item.IsSelected = true;
                _selectedIds.Add(item.Id);
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void ClearSelection() => ClearSelectionState();

    // ---- Navigation, Filter, Suche, Sortierung -------------------------------------------------

    [RelayCommand]
    private Task ReloadAsync()
        => HasLibrary ? ReloadTotalAndPageAsync(restoreSelection: true) : ReloadLibraryAsync(null);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PrevPageAsync() => NavigateToPageAsync(PageIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPageAsync() => NavigateToPageAsync(PageIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task FirstPageAsync() => NavigateToPageAsync(0);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task LastPageAsync() => NavigateToPageAsync(PageCount - 1);

    [RelayCommand]
    private Task SearchAsync() => ChangeQueryAsync();

    [RelayCommand]
    private Task ClearSearchAsync()
    {
        SearchTerm = null;
        return ChangeQueryAsync();
    }

    [RelayCommand]
    private Task ClearFolderFilterAsync()
    {
        // Lokal zurücksetzen — bewusst keine FolderSelectedMessage (der Baum behält seine Auswahl).
        _suppressReload = true;
        try
        {
            FolderId = null;
            FolderName = null;
            IncludeSubfolders = true;
        }
        finally
        {
            _suppressReload = false;
        }

        return ChangeQueryAsync();
    }

    /// <summary>Klick auf eine Unterordner-Kachel: Filter lokal setzen und den Ordnerbaum per Nachricht nachziehen.</summary>
    [RelayCommand]
    private async Task OpenSubfolderAsync(GallerySubfolderDto? folder)
    {
        if (folder is null || !HasLibrary)
        {
            return;
        }

        await ApplyFolderFilterAsync(folder.Id, includeSubfolders: false, knownName: folder.Name);
        _messenger.Send(new FolderSelectedMessage(_libraryId, folder.Id, false));
    }

    private async Task ApplyFolderFilterAsync(int? folderId, bool includeSubfolders, string? knownName = null)
    {
        _suppressReload = true;
        try
        {
            FolderId = folderId;
            IncludeSubfolders = includeSubfolders;
            FolderName = knownName;
            PageIndex = 0;
        }
        finally
        {
            _suppressReload = false;
        }

        if (folderId is { } id && knownName is null)
        {
            try
            {
                var info = await _source.GetFolderInfoAsync(id, CancellationToken.None);
                FolderName = info?.Name ?? $"#{id}";
            }
            catch (Exception ex)
            {
                FolderName = $"#{id}";
                _logger.LogWarning(ex, "Ordnername konnte nicht geladen werden: {FolderId}", id);
            }
        }

        ClearSelectionState();
        await ReloadTotalAndPageAsync(restoreSelection: false);
    }

    private Task ChangeQueryAsync()
    {
        if (!HasLibrary)
        {
            return Task.CompletedTask;
        }

        PageIndex = 0;
        ClearSelectionState();
        return ReloadTotalAndPageAsync(restoreSelection: false);
    }

    partial void OnFilterChanged(GalleryFilter value)
    {
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    partial void OnSelectedSortChanged(GallerySortOption value)
    {
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    partial void OnPageSizeChanged(int value)
    {
        if (!_suppressReload && _isInitialized)
        {
            FireAndForget(ChangeQueryAsync);
        }
    }

    // ---- Favorit / Bewertung (ICatalogUpdateService) -------------------------------------------

    [RelayCommand]
    private async Task ToggleFavoriteAsync(GalleryItem? item)
    {
        if (item is null)
        {
            return;
        }

        var target = !item.IsFavorite;
        item.IsFavorite = target; // optimistisch; FilesChangedMessage(Metadata) bestätigt aus der DB
        await RunServiceAsync(
            "Favorit",
            () => _catalog.SetFavoriteAsync([item.Id], target),
            onError: () => item.IsFavorite = !target);
    }

    [RelayCommand]
    private async Task SetItemRatingAsync(RatingRequest? request)
    {
        if (request?.Parameter is not GalleryItem item)
        {
            return;
        }

        var previous = item.Rating;
        item.Rating = request.Rating;
        await RunServiceAsync(
            "Bewertung",
            () => _catalog.SetRatingAsync([item.Id], request.Rating),
            onError: () => item.Rating = previous);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task FavoriteSelectionAsync() => SetSelectionFavoriteAsync(true);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task UnfavoriteSelectionAsync() => SetSelectionFavoriteAsync(false);

    private Task SetSelectionFavoriteAsync(bool isFavorite)
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync("Favorit", async () =>
        {
            await _catalog.SetFavoriteAsync(ids, isFavorite);
            foreach (var id in ids)
            {
                if (_currentItems.TryGetValue(id, out var item))
                {
                    item.IsFavorite = isFavorite;
                }
            }

            SetResultStatus(isFavorite
                ? $"{ids.Count:N0} Datei(en) als Favorit markiert."
                : $"Favorit bei {ids.Count:N0} Datei(en) entfernt.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task SetSelectionRatingAsync(RatingRequest? request) => ApplySelectionRatingAsync(request?.Rating);

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private Task ClearSelectionRatingAsync() => ApplySelectionRatingAsync(null);

    private Task ApplySelectionRatingAsync(byte? rating)
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync("Bewertung", async () =>
        {
            await _catalog.SetRatingAsync(ids, rating);
            foreach (var id in ids)
            {
                if (_currentItems.TryGetValue(id, out var item))
                {
                    item.Rating = rating;
                }
            }

            UpdateSelectionSummary();
            SetResultStatus(rating is { } r
                ? $"{ids.Count:N0} Datei(en) mit {r} Stern(en) bewertet."
                : $"Bewertung bei {ids.Count:N0} Datei(en) entfernt.");
        });
    }

    // ---- Datei-Operationen (IFileOperationService) ---------------------------------------------

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task MoveSelectionAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return;
        }

        var target = _folderPicker.PickFolder($"{ids.Count:N0} Datei(en) verschieben nach…", _libraryRootPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await RunOperationAsync("Verschieben", async () =>
        {
            var result = await _fileOps.MoveAsync(ids, target);
            await ReportBatchResultAsync("Verschieben", result);
            ScheduleReload();
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task RecycleSelectionAsync()
    {
        var ids = SelectedIdsSnapshot();
        if (ids.Count == 0)
        {
            return;
        }

        var message = ids.Count == 1
            ? "1 Datei in den Windows-Papierkorb verschieben?"
            : $"{ids.Count:N0} Dateien in den Windows-Papierkorb verschieben?";
        var confirmed = await _dialogs.ConfirmAsync("In den Papierkorb verschieben", message, "In den Papierkorb", isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync("Papierkorb", async () =>
        {
            var result = await _fileOps.RecycleAsync(ids);
            await ReportBatchResultAsync("Papierkorb", result);
            ScheduleReload();
        });
    }

    [RelayCommand(CanExecute = nameof(CanExecuteUndo))]
    private Task UndoAsync()
        => RunOperationAsync("Rückgängig", async () =>
        {
            var result = await _fileOps.UndoLastAsync();
            if (result is null)
            {
                SetResultStatus("Nichts rückgängig zu machen.");
                return;
            }

            await ReportBatchResultAsync("Rückgängig", result);
            ScheduleReload();
        });

    private async Task ReportBatchResultAsync(string verb, FileOperationBatchResult result)
    {
        var total = result.Succeeded + result.Failed;
        SetResultStatus(result.Failed == 0
            ? $"{verb}: {result.Succeeded:N0} Datei(en) erfolgreich."
            : $"{verb}: {result.Succeeded:N0} erfolgreich, {result.Failed:N0} fehlgeschlagen.");
        _logger.LogInformation(
            "{Verb}: Batch={BatchId} Typ={Type} OK={Succeeded} Fehler={Failed}",
            verb, result.BatchId, result.Type, result.Succeeded, result.Failed);

        if (result.Failed > 0)
        {
            var lines = result.Items
                .Where(i => !i.Success)
                .Select(i => $"{Path.GetFileName(i.SourcePath)}: {i.Error ?? "unbekannter Fehler"}")
                .ToList();
            await _dialogs.ShowErrorsAsync(
                $"{verb}: {result.Failed:N0} Fehler",
                $"{result.Failed:N0} von {total:N0} Datei(en) konnten nicht verarbeitet werden.",
                lines);
        }
    }

    private async Task RefreshCanUndoAsync()
    {
        try
        {
            var batches = await _fileOps.GetRecentBatchesAsync(10);
            CanUndo = batches.Any(b => b.CanUndo);
        }
        catch (Exception ex)
        {
            CanUndo = false;
            _logger.LogDebug(ex, "Undo-Status konnte nicht ermittelt werden");
        }
    }

    /// <summary>Einzelaktion ohne Sperre der Aktionsleiste (Herz/Sterne auf der Kachel).</summary>
    private async Task RunServiceAsync(string what, Func<Task> action, Action? onError = null)
    {
        try
        {
            ErrorMessage = null;
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            onError?.Invoke();
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "{What} fehlgeschlagen", what);
        }
    }

    /// <summary>Aktion auf der Auswahl: sperrt die Aktionsleiste, meldet Fehler, prüft danach Undo.</summary>
    private async Task RunOperationAsync(string what, Func<Task> action)
    {
        if (IsOperationRunning)
        {
            return;
        }

        IsOperationRunning = true;
        try
        {
            ErrorMessage = null;
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{what} fehlgeschlagen: {ex.Message}";
            _logger.LogError(ex, "{What} fehlgeschlagen", what);
        }
        finally
        {
            IsOperationRunning = false;
        }

        await RefreshCanUndoAsync();
    }

    // ---- Lightbox ------------------------------------------------------------------------------

    /// <summary>Enter/Doppelklick: Lightbox mit allen Items der aktuellen Page, Start = Item.</summary>
    [RelayCommand]
    private void OpenInLightbox(GalleryItem? item)
    {
        if (item is null || CurrentPage.Count == 0)
        {
            return;
        }

        var entries = new List<LightboxEntry>(CurrentPage.Count);
        var startIndex = 0;
        for (var i = 0; i < CurrentPage.Count; i++)
        {
            var it = CurrentPage[i];
            if (ReferenceEquals(it, item))
            {
                startIndex = i;
            }

            entries.Add(new LightboxEntry(
                it.Id, it.SourcePath, it.Name, it.MediaType, it.IsFavorite, it.Rating, it.Size, it.SourceModifiedAtUtc));
        }

        try
        {
            var owner = OwnerWindowResolver?.Invoke() ?? Application.Current?.MainWindow;
            _lightbox.Open(entries, startIndex, owner);
        }
        catch (Exception ex)
        {
            // z. B. DirectoryNotFound bei Library-Root offline: melden, nicht abstürzen.
            ErrorMessage = $"Lightbox konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogError(ex, "Lightbox-Open fehlgeschlagen für File={FileId}", item.Id);
        }
    }

    // ---- Statuszeile ---------------------------------------------------------------------------

    /// <summary>Ergebnis einer Aktion; bleibt <see cref="ResultStatusHold"/> stehen, auch wenn der Reload danach den Lade-Status setzen will.</summary>
    private void SetResultStatus(string text)
    {
        StatusText = text;
        _resultStatusUntil = DateTime.UtcNow + ResultStatusHold;
    }

    /// <summary>Lade-Status („n Treffer · Seite x von y“); überschreibt eine frische Ergebnis-Meldung nicht.</summary>
    private void SetLoadStatus(string text)
    {
        if (DateTime.UtcNow < _resultStatusUntil)
        {
            return;
        }

        StatusText = text;
    }

    // ---- Threading-Helfer ----------------------------------------------------------------------

    private void RunOnUi(Func<Task> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            FireAndForget(work);
            return;
        }

        dispatcher.InvokeAsync(() => FireAndForget(work));
    }

    private async void FireAndForget(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hintergrundaktion der Galerie fehlgeschlagen");
        }
    }

    private static Task DispatchAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}

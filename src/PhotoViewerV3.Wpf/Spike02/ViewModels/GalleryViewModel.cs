using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Spike02.Data;

namespace PhotoViewerV3.Spike02.ViewModels;

public sealed partial class GalleryViewModel : ObservableObject
{
    private const int TileImageWidth = 180;

    private readonly ILibraryContextFactory _contextFactory;
    private readonly GalleryPageSource _source;
    private readonly IThumbnailService _thumbnails;
    private readonly ILogger<GalleryViewModel> _logger;
    private readonly Dictionary<int, GalleryItem> _currentItems = new();

    private CancellationTokenSource? _activeLoadCts;
    private bool _isInitialized;
    private DateTime _startupBegin;
    private int _libraryId;

    public GalleryViewModel(
        ILibraryContextFactory contextFactory,
        GalleryPageSource source,
        IThumbnailService thumbnails,
        ILogger<GalleryViewModel> logger)
    {
        _contextFactory = contextFactory;
        _source = source;
        _thumbnails = thumbnails;
        _logger = logger;
        CurrentPage = new ObservableCollection<GalleryItem>();
        _thumbnails.ThumbnailReady += OnThumbnailReady;
    }

    public ObservableCollection<GalleryItem> CurrentPage { get; }

    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private int _pageSize = 500;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private GalleryFilter _filter = GalleryFilter.All;
    [ObservableProperty] private string? _searchTerm;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Bereit.";
    [ObservableProperty] private double _lastPageLoadMs;
    [ObservableProperty] private double _firstPageLoadMs;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _dbPathDisplay = string.Empty;
    [ObservableProperty] private string _libraryNameDisplay = string.Empty;

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int DisplayPageIndex => PageIndex + 1;
    public bool CanGoBack => PageIndex > 0;
    public bool CanGoForward => PageIndex < PageCount - 1;

    partial void OnPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayPageIndex));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _startupBegin = DateTime.UtcNow;
        DbPathDisplay = _contextFactory.DatabasePath;

        // Aktive Library bestimmen (ADR-0003: eine aktive Library).
        // Sortierung: zuletzt geöffnet > zuletzt angelegt. NULLs landen bei DESC unten.
        try
        {
            await using var ctx = _contextFactory.Create();
            var lib = await ctx.Libraries
                .OrderByDescending(l => l.LastOpenedAt)
                .ThenByDescending(l => l.CreatedAt)
                .Select(l => new { l.Id, l.Name, l.RootPath })
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (lib is null)
            {
                ErrorMessage = "Noch keine Library angelegt. Bitte zuerst per CLI scannen:\n" +
                               "dotnet run --project tests/PhotoViewerV3.PerfTests -c Release -- spike-v01-scan --root <PFAD> --name <NAME>";
                _logger.LogWarning("Keine Library in DB gefunden — Gallery bleibt leer");
                return;
            }

            _libraryId = lib.Id;
            LibraryNameDisplay = $"{lib.Name}  ·  {lib.RootPath}";
            _logger.LogInformation("Aktive Library: Id={Id} Name={Name} RootPath={RootPath}", lib.Id, lib.Name, lib.RootPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Library konnte nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Fehler beim Lesen der Libraries-Tabelle");
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

        await ReloadAsync().ConfigureAwait(false);
        FirstPageLoadMs = (DateTime.UtcNow - _startupBegin).TotalMilliseconds;
        _logger.LogInformation("Start Gallery → erste Page sichtbar: {Ms:N1} ms", FirstPageLoadMs);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!_source.IsOpen)
        {
            return;
        }

        await LoadCurrentPageAsync().ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PrevPageAsync()
    {
        PageIndex = Math.Max(0, PageIndex - 1);
        return LoadCurrentPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPageAsync()
    {
        PageIndex = Math.Min(PageCount - 1, PageIndex + 1);
        return LoadCurrentPageAsync();
    }

    [RelayCommand]
    private Task FirstPageAsync()
    {
        PageIndex = 0;
        return LoadCurrentPageAsync();
    }

    [RelayCommand]
    private Task LastPageAsync()
    {
        PageIndex = PageCount - 1;
        return LoadCurrentPageAsync();
    }

    [RelayCommand]
    private async Task SetFilterAsync(string? filterName)
    {
        if (!Enum.TryParse<GalleryFilter>(filterName, ignoreCase: true, out var f))
        {
            return;
        }

        Filter = f;
        PageIndex = 0;
        await ReloadTotalAndPageAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        PageIndex = 0;
        await ReloadTotalAndPageAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchTerm = null;
        PageIndex = 0;
        await ReloadTotalAndPageAsync().ConfigureAwait(false);
    }

    private async Task ReloadTotalAndPageAsync()
    {
        if (!_source.IsOpen)
        {
            return;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var query = BuildQuery();
            var total = _source.GetTotalCount(query);
            TotalCount = total;
            await LoadCurrentPageAsync().ConfigureAwait(false);
            sw.Stop();
            StatusText = $"{TotalCount:N0} Treffer · Page {DisplayPageIndex} von {PageCount:N0} · {sw.Elapsed.TotalMilliseconds:N1} ms";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Fehler beim Reload");
        }
    }

    private async Task LoadCurrentPageAsync()
    {
        _activeLoadCts?.Cancel();
        _activeLoadCts = new CancellationTokenSource();
        var ct = _activeLoadCts.Token;

        try
        {
            IsLoading = true;
            ErrorMessage = null;
            var sw = Stopwatch.StartNew();
            var query = BuildQuery();
            if (TotalCount == 0)
            {
                TotalCount = _source.GetTotalCount(query);
            }
            var rows = await _source.LoadPageAsync(query, PageIndex, PageSize, ct).ConfigureAwait(false);
            sw.Stop();
            LastPageLoadMs = sw.Elapsed.TotalMilliseconds;
            await ReplaceItemsAsync(rows).ConfigureAwait(false);
            StatusText = $"{TotalCount:N0} Treffer · Page {DisplayPageIndex} von {PageCount:N0} · {LastPageLoadMs:N1} ms";
            _logger.LogInformation(
                "Page geladen: Idx={Idx} Size={Size} Rows={Rows} Time={Ms:N1}ms Filter={Filter} Search={Search}",
                PageIndex, PageSize, rows.Count, LastPageLoadMs, Filter, SearchTerm ?? "-");
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
            IsLoading = false;
        }
    }

    private GalleryQuery BuildQuery()
    {
        var term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim();
        return new GalleryQuery(_libraryId, Filter, term);
    }

    private async Task ReplaceItemsAsync(IReadOnlyList<GalleryFileDto> rows)
    {
        var items = new List<GalleryItem>(rows.Count);
        foreach (var r in rows)
        {
            items.Add(new GalleryItem(r));
        }

        await DispatchAsync(() =>
        {
            CurrentPage.Clear();
            _currentItems.Clear();
            foreach (var it in items)
            {
                CurrentPage.Add(it);
                _currentItems[it.Id] = it;
            }
        }).ConfigureAwait(false);

        // Thumbnails für die jetzt sichtbare Page anfordern (Service dedupliziert + cached).
        var requests = new List<ThumbnailRequest>(items.Count);
        foreach (var it in items)
        {
            requests.Add(new ThumbnailRequest(_libraryId, it.Id, it.SourcePath, it.MediaType, it.SourceModifiedAtUtc));
        }

        try
        {
            await _thumbnails.RequestThumbnailsAsync(requests).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail-Anforderung fehlgeschlagen");
        }
    }

    private void OnThumbnailReady(ThumbnailReadyEventArgs args)
    {
        if (args.LibraryId != _libraryId)
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

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
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

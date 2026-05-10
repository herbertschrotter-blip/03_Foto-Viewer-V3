using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Spike02.Data;

namespace PhotoViewerV3.Spike02.ViewModels;

public sealed partial class GalleryViewModel : ObservableObject
{
    private const string SpikeDbRelativePath = @"spike-01\library-spike.db";

    private readonly IAppDataPaths _paths;
    private readonly GalleryPageSource _source;
    private readonly ILogger<GalleryViewModel> _logger;

    private CancellationTokenSource? _activeLoadCts;
    private bool _isInitialized;
    private DateTime _startupBegin;

    public GalleryViewModel(IAppDataPaths paths, GalleryPageSource source, ILogger<GalleryViewModel> logger)
    {
        _paths = paths;
        _source = source;
        _logger = logger;
        CurrentPage = new ObservableCollection<GalleryFileDto>();
    }

    public ObservableCollection<GalleryFileDto> CurrentPage { get; }

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

        var dbPath = Path.Combine(_paths.AppDataRoot, "spike-01", "library-spike.db");
        DbPathDisplay = dbPath;

        var spikeDir = Path.GetDirectoryName(dbPath)!;
        var fileExists = File.Exists(dbPath);
        var dirExists = Directory.Exists(spikeDir);
        _logger.LogInformation(
            "Diag: AppDataRoot='{Root}' Len={RootLen}; dbPath='{DbPath}' Len={DbLen}; File.Exists={Exists}; Directory.Exists={DirExists}",
            _paths.AppDataRoot, _paths.AppDataRoot.Length, dbPath, dbPath.Length, fileExists, dirExists);

        if (dirExists)
        {
            try
            {
                var entries = Directory.GetFileSystemEntries(spikeDir);
                _logger.LogInformation("Diag: Spike-Verzeichnis Inhalt ({Count}): {Entries}",
                    entries.Length, string.Join("; ", entries));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Diag: Fehler beim Lesen des Spike-Verzeichnisses");
            }
        }

        if (!_source.TryOpen(dbPath, out var error))
        {
            ErrorMessage = $"{error}\nBitte zuerst Spike #01 ausführen, um die Test-DB zu erzeugen:\n" +
                           "dotnet run --project tests/PhotoViewerV3.PerfTests -c Release -- spike01-sqlite-bulk";
            _logger.LogWarning("Spike-DB nicht geöffnet: {Error}", error);
            return;
        }

        _logger.LogInformation("Spike-DB geöffnet: {Path}", dbPath);
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
        return new GalleryQuery(Filter, term);
    }

    private Task ReplaceItemsAsync(IReadOnlyList<GalleryFileDto> rows)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CurrentPage.Clear();
            foreach (var r in rows)
            {
                CurrentPage.Add(r);
            }

            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() =>
        {
            CurrentPage.Clear();
            foreach (var r in rows)
            {
                CurrentPage.Add(r);
            }
        }).Task;
    }
}

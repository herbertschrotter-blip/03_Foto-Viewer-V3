using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Albums;
using Lumora.Services.Infrastructure;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Shell;

namespace Lumora.Albums;

/// <summary>Kachel eines Albums auf der Seite „Alben“.</summary>
public sealed partial class AlbumTileViewModel : ObservableObject
{
    public AlbumTileViewModel(AlbumInfo info) => Info = info;

    public AlbumInfo Info { get; }

    public int Id => Info.Id;

    public string Name => Info.Name;

    public bool IsSmart => Info.IsSmart;

    public string CountText => Info.CountText;

    /// <summary>Bedingungen eines intelligenten Albums in Worten (Tooltip, Zweitzeile).</summary>
    public string? FilterText => Info.Filter is { } filter ? string.Join(" UND ", filter.Describe()) : null;

    [ObservableProperty] private ImageSource? _image;
}

/// <summary>
/// Seite „Alben“ (ADR-0054): Kacheln mit Titelbild und Anzahl, Album anlegen/umbenennen/löschen, Titelbild zurücksetzen,
/// Album öffnen (Galerie gefiltert bzw. Filter des intelligenten Albums geladen), „Album exportieren…“ mit Fortschritt
/// und Abbrechen (echte Kopien; Bibliotheksordner nur nach Rückfrage; in einer versteckten Bibliothek gesperrt).
/// </summary>
public sealed partial class AlbumsViewModel : ObservableObject, IDisposable
{
    private readonly AlbumActions _actions;
    private readonly GalleryViewModel _gallery;
    private readonly INavigationService _navigation;
    private readonly ILibraryService _libraries;
    private readonly AlbumCoverProvider _covers;
    private readonly ICatalogStoragePaths? _catalog;
    private readonly ILogger<AlbumsViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
    private CancellationTokenSource? _exportCts;
    private bool _loaded;

    public AlbumsViewModel(
        AlbumActions actions,
        GalleryViewModel gallery,
        INavigationService navigation,
        ILibraryService libraries,
        AlbumCoverProvider covers,
        ILogger<AlbumsViewModel> logger,
        ICatalogStoragePaths? catalog = null)
    {
        _actions = actions;
        _gallery = gallery;
        _navigation = navigation;
        _libraries = libraries;
        _covers = covers;
        _catalog = catalog;
        _logger = logger;
        _covers.ImagesReady += OnImagesReady;
        _messenger.Register<AlbumsViewModel, AlbumsChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.LoadAsync(); }));
        _messenger.Register<AlbumsViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.LoadAsync(); }));
    }

    public ObservableCollection<AlbumTileViewModel> Albums { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty] private string _summaryText = string.Empty;

    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportAlbumCommand))]
    private bool _isExporting;

    [ObservableProperty] private double _exportProgress;

    [ObservableProperty] private string _exportText = string.Empty;

    public bool IsEmpty => !IsLoading && Albums.Count == 0;

    /// <summary>Exportieren ist in einer versteckten Bibliothek gesperrt (Kopien lägen außerhalb des Containers).</summary>
    public bool CanExport => _catalog?.IsHidden != true;

    /// <summary>Beim Anzeigen der Seite.</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            ErrorText = null;
            var albums = await _actions.Albums.GetAllAsync();
            Albums.Clear();
            foreach (var album in albums)
            {
                var tile = new AlbumTileViewModel(album);
                if (album.Cover is { } cover)
                {
                    tile.Image = _covers.TryGet(cover.FileId);
                }

                Albums.Add(tile);
            }

            _covers.Request(albums.Where(a => a.Cover is not null).Select(a => a.Cover!));
            var smart = albums.Count(a => a.IsSmart);
            SummaryText = albums.Count == 0
                ? "Noch keine Alben"
                : $"{albums.Count:N0} {(albums.Count == 1 ? "Album" : "Alben")}{(smart > 0 ? $" · davon {smart:N0} intelligent" : string.Empty)}";
            _loaded = true;
        }
        catch (Exception ex)
        {
            ErrorText = $"Alben konnten nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Alben konnten nicht geladen werden");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void OnImagesReady(IReadOnlyList<int> fileIds)
    {
        var ready = fileIds.ToHashSet();
        foreach (var tile in Albums)
        {
            if (tile.Info.Cover is { } cover && ready.Contains(cover.FileId))
            {
                tile.Image = _covers.TryGet(cover.FileId);
            }
        }
    }

    [RelayCommand]
    private async Task CreateAlbumAsync()
    {
        var name = await _actions.Dialogs.PromptTextAsync(
            "Neues Album",
            "Name des Albums — Fotos fügst du in der Galerie über das Kontextmenü „Zu Album hinzufügen“, die Aktionsleiste oder Strg+L hinzu. Sie werden nur verknüpft, nicht kopiert.",
            string.Empty,
            "Anlegen");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var album = await _actions.Albums.CreateAsync(name);
            StatusText = $"Album „{album.Name}“ angelegt.";
        });
    }

    /// <summary>Album öffnen: Galerie gefiltert auf das Album bzw. Filter des intelligenten Albums geladen.</summary>
    [RelayCommand]
    public async Task OpenAlbumAsync(AlbumTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        _navigation.NavigateTo(NavigationTags.Gallery);
        await _gallery.OpenAlbumAsync(tile.Id);
    }

    [RelayCommand]
    private async Task RenameAlbumAsync(AlbumTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var name = await _actions.Dialogs.PromptTextAsync("Album umbenennen", $"Neuer Name für „{tile.Name}“:", tile.Name, "Umbenennen");
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == tile.Name)
        {
            return;
        }

        await RunAsync(() => _actions.Albums.RenameAsync(tile.Id, name));
    }

    [RelayCommand]
    private async Task DeleteAlbumAsync(AlbumTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var message = tile.IsSmart
            ? $"Das intelligente Album „{tile.Name}“ löschen? Nur der gespeicherte Filter geht — keine Fotos."
            : $"Das Album „{tile.Name}“ löschen? Nur die Verknüpfungen gehen ({tile.CountText}) — die Fotos selbst bleiben, wo sie sind.";
        if (!await _actions.Dialogs.ConfirmAsync("Album löschen", message, "Album löschen", isDestructive: true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _actions.Albums.DeleteAsync(tile.Id);
            StatusText = $"Album „{tile.Name}“ gelöscht — die Fotos sind unverändert.";
        });
    }

    [RelayCommand]
    private Task ResetCoverAsync(AlbumTileViewModel? tile)
        => tile is null ? Task.CompletedTask : RunAsync(() => _actions.Albums.SetCoverAsync(tile.Id, null));

    private bool CanExportAlbum(AlbumTileViewModel? tile) => !IsExporting;

    /// <summary>„Album exportieren…“: Zielordner wählen, echte Kopien, Fortschritt, Abbrechen.</summary>
    [RelayCommand(CanExecute = nameof(CanExportAlbum))]
    private async Task ExportAlbumAsync(AlbumTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (!CanExport)
        {
            await _actions.Dialogs.ShowErrorAsync("Album exportieren", "In einer versteckten Bibliothek lässt sich kein Album exportieren — die Kopien lägen außerhalb des Containers.");
            return;
        }

        var target = _actions.Dialogs.PickFolder($"Zielordner für die Kopien von „{tile.Name}“");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await ExportToAsync(tile, target);
    }

    /// <summary>Export in einen Ordner (öffentlich für den Rauchtest): fragt nach, wenn das Ziel in einer Bibliothek liegt.</summary>
    public async Task<AlbumExportResult?> ExportToAsync(AlbumTileViewModel tile, string target)
    {
        if (await IsInsideLibraryAsync(target)
            && !await _actions.Dialogs.ConfirmAsync(
                "In eine Bibliothek exportieren?",
                $"„{target}“ liegt in einer Bibliothek. Die Kopien erscheinen beim nächsten Scan als zusätzliche Dateien (Doppelte). Trotzdem dorthin exportieren?",
                "Trotzdem exportieren"))
        {
            return null;
        }

        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        ExportText = $"„{tile.Name}“ wird exportiert …";
        try
        {
            var progress = new Progress<AlbumExportProgress>(p =>
            {
                ExportProgress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                ExportText = $"„{tile.Name}“: {p.Done:N0} von {p.Total:N0} kopiert";
            });
            var result = await _actions.Albums.ExportAsync(tile.Id, target, progress, _exportCts.Token);
            var parts = new List<string> { $"{result.Copied:N0} Dateien nach „{result.TargetFolder}“ kopiert" };
            if (result.Renamed > 0)
            {
                parts.Add($"{result.Renamed:N0} mit neuem Namen (gleichnamige gab es schon)");
            }

            if (result.Missing > 0)
            {
                parts.Add($"{result.Missing:N0} fehlten");
            }

            if (result.Failed > 0)
            {
                parts.Add($"{result.Failed:N0} Fehler");
            }

            StatusText = (result.Canceled ? "Abgebrochen: " : string.Empty) + string.Join(", ", parts) + ".";
            if (result.Errors.Count > 0)
            {
                await _actions.Dialogs.ShowErrorAsync("Album exportieren", string.Join(Environment.NewLine, result.Errors.Take(20)));
            }

            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorText = $"Exportieren fehlgeschlagen: {ex.Message}";
            _logger.LogWarning(ex, "Album exportieren fehlgeschlagen");
            return null;
        }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    private async Task<bool> IsInsideLibraryAsync(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var library in await _libraries.GetAllAsync())
        {
            foreach (var root in library.Roots)
            {
                var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            ErrorText = null;
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _logger.LogError(ex, "Alben: Aktion fehlgeschlagen");
        }
    }

    public void Dispose()
    {
        _covers.ImagesReady -= OnImagesReady;
        _messenger.UnregisterAll(this);
    }
}

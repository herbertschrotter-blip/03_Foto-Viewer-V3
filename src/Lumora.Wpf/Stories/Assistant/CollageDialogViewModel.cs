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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Assistant;

namespace Lumora.Stories.Assistant;

/// <summary>Was in die Collage soll: sachliche Überschrift, Kandidaten (beliebige Reihenfolge), gewünschte Anzahl.</summary>
public sealed record CollageRequest(string? Title, IReadOnlyList<int> CandidateIds, int? PreferredCount = null);

/// <summary>Speicherort wählen (SaveFileDialog ohne „Zuletzt verwendet“-Spur); Tests ersetzen ihn.</summary>
public interface ICollageFilePicker
{
    /// <summary>Gewählter Pfad oder null (abgebrochen).</summary>
    string? PickSavePath(string initialFolder, string fileName);
}

/// <summary>Öffnet den Dialog „Als Collage-Bild speichern“ (Tests ersetzen ihn).</summary>
public interface ICollageDialogService
{
    /// <summary>Gespeicherte Collage oder null (abgebrochen).</summary>
    Task<CollageSaveResult?> ShowAsync(CollageRequest request);
}

/// <summary>Auswahl in einer Segment-Leiste des Dialogs.</summary>
public sealed partial class CollageChoice : ObservableObject
{
    public CollageChoice(string label, object value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public object Value { get; }

    public CollageDialogViewModel? Owner { get; set; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Foto in der Vorschau (Position in DIP auf der Vorschau-Fläche); Klick tauscht es aus.</summary>
public sealed partial class CollagePreviewTile : ObservableObject
{
    public CollagePreviewTile(int index, int fileId, double x, double y, double width, double height)
    {
        Index = index;
        FileId = fileId;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int Index { get; }

    public int FileId { get; }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public CollageDialogViewModel? Owner { get; set; }

    [ObservableProperty] private ImageSource? _image;
}

/// <summary>
/// Dialog „Als Collage-Bild speichern“ (Mockup 12, ADR-0063): Format 16:9 / A4 quer / A4 hoch / Quadrat, Größe Full HD /
/// 4K / Druck 300 dpi, 12/24/36/48 Fotos, Anordnung Großes Titelbild / Raster / Zeitstrahl, sachliche Überschrift an/aus
/// (änderbar, nie erfunden), Datum je Foto, Speicherort. „Neu mischen“ wählt andere gute Fotos aus dem Vorrat, ein Klick
/// auf ein Foto tauscht es gegen das nächste. Die Vorschau nutzt dieselbe Layout-Rechnung wie das gespeicherte Bild
/// (Vorschaubilder statt Originale); gespeichert wird erst nach „Speichern“ und dem Dateidialog.
/// </summary>
public sealed partial class CollageDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>Breite der Vorschau-Fläche in DIP.</summary>
    public const double PreviewWidth = 540;

    private readonly ICollageService _collage;
    private readonly IStoryImageSource? _images;
    private readonly IStoryMediaLoader? _media;
    private readonly ICollageFilePicker _picker;
    private readonly CollageRequest _request;
    private readonly ILogger _logger;
    private IReadOnlyList<CollagePhoto> _pool = [];
    private List<CollagePhoto> _selected = [];
    private int _mix;
    private int _swapCursor;
    private int? _heroId;
    private bool _pathConfirmed;
    private CancellationTokenSource? _saveCts;

    public CollageDialogViewModel(ICollageService collage, ICollageFilePicker picker, CollageRequest request, IStoryImageSource? images = null, IStoryMediaLoader? media = null, ILogger? logger = null)
    {
        _collage = collage;
        _picker = picker;
        _request = request;
        _images = images;
        _media = media;
        _logger = logger ?? NullLogger.Instance;
        _title = request.Title ?? string.Empty;
        _showTitle = !string.IsNullOrWhiteSpace(request.Title);
        var count = CollageOptions.CountChoices.OrderBy(c => Math.Abs(c - (request.PreferredCount ?? 36))).First();
        Formats = Choices(("16:9", CollageFormat.Wide16x9), ("A4 quer", CollageFormat.A4Landscape), ("A4 hoch", CollageFormat.A4Portrait), ("Quadrat", CollageFormat.Square));
        Sizes = Choices(("Full HD", CollageSize.FullHd), ("4K", CollageSize.Uhd4K), ("Druck 300 dpi", CollageSize.Print300));
        Counts = Choices(CollageOptions.CountChoices.Select(c => (c.ToString(System.Globalization.CultureInfo.InvariantCulture), (object)c)).ToArray());
        Arrangements = Choices(("Großes Titelbild", CollageArrangement.Hero), ("Raster", CollageArrangement.Grid), ("Zeitstrahl", CollageArrangement.Timeline));
        Select(Formats, CollageFormat.Wide16x9);
        Select(Sizes, CollageSize.Uhd4K);
        Select(Counts, count);
        Select(Arrangements, CollageArrangement.Hero);
        if (_images is not null)
        {
            _images.ImagesReady += OnImagesReady;
        }
    }

    public ObservableCollection<CollageChoice> Formats { get; }

    public ObservableCollection<CollageChoice> Sizes { get; }

    public ObservableCollection<CollageChoice> Counts { get; }

    public ObservableCollection<CollageChoice> Arrangements { get; }

    public ObservableCollection<CollagePreviewTile> Tiles { get; } = new();

    [ObservableProperty] private string _title;

    [ObservableProperty] private bool _showTitle;

    [ObservableProperty] private bool _showDates;

    [ObservableProperty] private string _targetPath = string.Empty;

    partial void OnTargetPathChanged(string value) => _pathConfirmed = false; // von Hand geändert → beim Speichern bestätigen

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _statusText;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty] private double _previewHeight = PreviewWidth * 9 / 16;

    [ObservableProperty] private double _titleTop;

    [ObservableProperty] private double _titleLeft;

    [ObservableProperty] private double _titleWidth;

    [ObservableProperty] private double _titleHeight;

    [ObservableProperty] private string _sizeText = string.Empty;

    /// <summary>Gespeicherte Collage, sobald fertig.</summary>
    public CollageSaveResult? Result { get; private set; }

    public event EventHandler? CloseRequested;

    public CollageOptions Options => new(
        (CollageFormat)Selected(Formats),
        (CollageSize)Selected(Sizes),
        (int)Selected(Counts),
        (CollageArrangement)Selected(Arrangements),
        ShowTitle && !string.IsNullOrWhiteSpace(Title),
        ShowDates);

    /// <summary>Fotos in Platz-Reihenfolge (Tests).</summary>
    public IReadOnlyList<CollagePhoto> SelectedPhotos => _selected;

    public int PoolCount => _pool.Count;

    /// <summary>Vorrat laden (gute Fotos, bestes zuerst), erste Auswahl, Speicherort vorschlagen.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "Fotos werden ausgewählt …";
        try
        {
            var ranked = await _collage.RankAsync(_request.CandidateIds, CollageOptions.CountChoices.Max() * 3);
            _pool = await _collage.LoadPhotosAsync(ranked);
            var folder = await _collage.GetDefaultFolderAsync();
            if (string.IsNullOrWhiteSpace(TargetPath) && folder.Length > 0)
            {
                TargetPath = Path.Combine(folder, CollageService.SuggestFileName(Title));
            }

            Pick();
            StatusText = _pool.Count == 0 ? "Keine Fotos für eine Collage (Videos zählen nicht)." : null;
        }
        catch (Exception ex)
        {
            ErrorText = "Die Fotos konnten nicht geladen werden: " + ex.Message;
            _logger.LogWarning(ex, "Collage: Fotos nicht geladen");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Choose(CollageChoice? choice)
    {
        if (choice?.Owner is null)
        {
            return;
        }

        var list = new[] { Formats, Sizes, Counts, Arrangements }.First(l => l.Contains(choice));
        foreach (var c in list)
        {
            c.IsSelected = ReferenceEquals(c, choice);
        }

        if (ReferenceEquals(list, Counts))
        {
            Pick(); // andere Anzahl: neu auswählen
        }
        else
        {
            RebuildPreview();
        }
    }

    partial void OnShowTitleChanged(bool value) => RebuildPreview();

    partial void OnTitleChanged(string value) => RebuildPreview();

    partial void OnShowDatesChanged(bool value) => RebuildPreview();

    /// <summary>„Neu mischen“: andere gute Fotos aus dem Vorrat.</summary>
    [RelayCommand]
    public void Remix()
    {
        _mix++;
        Pick();
    }

    /// <summary>Foto per Klick tauschen: gegen das nächste noch nicht gezeigte aus dem Vorrat.</summary>
    [RelayCommand]
    public void Swap(CollagePreviewTile? tile)
    {
        if (tile is null || tile.Index >= _selected.Count)
        {
            return;
        }

        var used = _selected.Select(p => p.FileId).ToHashSet();
        var unused = _pool.Where(p => !used.Contains(p.FileId)).ToList();
        if (unused.Count == 0)
        {
            StatusText = "Keine weiteren Fotos zum Tauschen.";
            return;
        }

        var replacement = unused[_swapCursor++ % unused.Count];
        if (_selected[tile.Index].FileId == _heroId)
        {
            _heroId = replacement.FileId;
        }

        _selected[tile.Index] = replacement;
        RebuildPreview();
    }

    [RelayCommand]
    private void Browse()
    {
        var current = string.IsNullOrWhiteSpace(TargetPath) ? null : TargetPath;
        var folder = current is null ? string.Empty : Path.GetDirectoryName(current) ?? string.Empty;
        var name = current is null ? CollageService.SuggestFileName(Title) : Path.GetFileName(current);
        if (_picker.PickSavePath(folder, name) is { } path)
        {
            TargetPath = path;
            _pathConfirmed = true; // im Dateidialog bestätigt (auch das Überschreiben)
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        ErrorText = null;
        if (_selected.Count == 0)
        {
            ErrorText = "Keine Fotos für die Collage.";
            return;
        }

        // Gespeichert wird nur über den Dateidialog (Überschreiben fragt er selbst) — nie still auf den Vorschlag
        if (!_pathConfirmed || string.IsNullOrWhiteSpace(TargetPath))
        {
            Browse();
            if (!_pathConfirmed || string.IsNullOrWhiteSpace(TargetPath))
            {
                return;
            }
        }

        if (!await _collage.IsAllowedTargetAsync(TargetPath))
        {
            ErrorText = "In einer versteckten Bibliothek lässt sich die Collage nur im Container speichern (im Bibliotheksordner).";
            return;
        }

        IsBusy = true;
        _saveCts = new CancellationTokenSource();
        try
        {
            var total = _selected.Count;
            var progress = new Progress<int>(n => StatusText = $"Collage wird gezeichnet … {n} von {total} Fotos");
            Result = await _collage.SaveAsync(Options, _selected, Title, TargetPath, progress, _saveCts.Token);
            StatusText = $"Gespeichert: {Path.GetFileName(Result.Path)} ({Result.Width} × {Result.Height})";
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            ErrorText = "Die Collage konnte nicht gespeichert werden: " + ex.Message;
            _logger.LogWarning(ex, "Collage: Speichern fehlgeschlagen");
        }
        finally
        {
            IsBusy = false;
            _saveCts?.Dispose();
            _saveCts = null;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_saveCts is { } cts)
        {
            cts.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Auswahl: die besten <c>Count</c> (beim Mischen zufällig aus dem besten Teil des Vorrats), Titelbild = bestes.</summary>
    private void Pick()
    {
        var count = Math.Min((int)Selected(Counts), _pool.Count);
        List<CollagePhoto> chosen;
        if (_mix == 0)
        {
            chosen = _pool.Take(count).ToList();
        }
        else
        {
            var random = new Random(_mix * 7919);
            var top = _pool.Take(Math.Max(count, Math.Min(_pool.Count, count * 2))).ToList();
            chosen = top.OrderBy(_ => random.Next()).Take(count).ToList();
            if (chosen.Select(p => p.FileId).ToHashSet().SetEquals(_selected.Select(p => p.FileId)) && _pool.Count > count)
            {
                chosen = _pool.Skip(_mix % Math.Max(1, _pool.Count - count + 1)).Take(count).ToList();
            }
        }

        _heroId = chosen.Count > 0 ? chosen[0].FileId : null;
        _selected = Order(chosen);
        RebuildPreview();
    }

    /// <summary>Großes Titelbild: das beste zuerst, sonst alles in Aufnahme-Reihenfolge.</summary>
    private List<CollagePhoto> Order(List<CollagePhoto> chosen)
    {
        var byTime = chosen.OrderBy(p => p.LocalTime ?? DateTime.MaxValue).ThenBy(p => p.FileId).ToList();
        if ((CollageArrangement)Selected(Arrangements) != CollageArrangement.Hero || chosen.Count == 0)
        {
            return byTime;
        }

        var best = chosen.FirstOrDefault(p => p.FileId == _heroId) ?? chosen[0];
        return byTime.Where(p => p.FileId != best.FileId).Prepend(best).ToList();
    }

    private void RebuildPreview()
    {
        var options = Options;
        _selected = Order(_selected.ToList()); // Titelbild bleibt vorn (bzw. das getauschte), sonst Aufnahme-Reihenfolge

        var (width, height, dpi) = CollageLayout.Dimensions(options.Format, options.Size);
        SizeText = $"{width} × {height} Pixel{(dpi != 96 ? $" · {dpi} dpi" : string.Empty)}";
        var layout = CollageLayout.Compute(width, height, dpi, _selected.Select(p => p.Aspect).ToList(), options.Arrangement, options.ShowTitle);
        var scale = PreviewWidth / width;
        PreviewHeight = height * scale;
        if (layout.Title is { } t)
        {
            (TitleLeft, TitleTop, TitleWidth, TitleHeight) = (t.X * scale, t.Y * scale, t.Width * scale, t.Height * scale);
        }
        else
        {
            TitleHeight = 0;
        }

        Tiles.Clear();
        foreach (var cell in layout.Cells.Where(c => c.Index < _selected.Count))
        {
            var photo = _selected[cell.Index];
            Tiles.Add(new CollagePreviewTile(cell.Index, photo.FileId, cell.Rect.X * scale, cell.Rect.Y * scale, cell.Rect.Width * scale, cell.Rect.Height * scale)
            {
                Owner = this,
                Image = _images?.TryGet(photo.FileId),
            });
        }

        _ = RequestImagesAsync();
    }

    private async Task RequestImagesAsync()
    {
        if (_images is null || _media is null)
        {
            return;
        }

        var missing = Tiles.Where(t => t.Image is null).Select(t => t.FileId).Distinct().ToList();
        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            var media = await _media.LoadAsync(missing);
            _images.Request(media.Values);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Collage: Vorschaubilder nicht geladen");
        }
    }

    private void OnImagesReady(IReadOnlyList<int> fileIds)
    {
        var ready = fileIds.ToHashSet();
        foreach (var tile in Tiles.Where(t => ready.Contains(t.FileId)))
        {
            tile.Image = _images?.TryGet(tile.FileId);
        }
    }

    private ObservableCollection<CollageChoice> Choices(params (string Label, object Value)[] items)
        => new(items.Select(i => new CollageChoice(i.Label, i.Value) { Owner = this }));

    private static void Select(IEnumerable<CollageChoice> choices, object value)
    {
        foreach (var c in choices)
        {
            c.IsSelected = Equals(c.Value, value);
        }
    }

    private static object Selected(IEnumerable<CollageChoice> choices) => (choices.FirstOrDefault(c => c.IsSelected) ?? choices.First()).Value;

    public void Dispose()
    {
        if (_images is not null)
        {
            _images.ImagesReady -= OnImagesReady;
        }

        _saveCts?.Cancel();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Folders;
using PhotoViewerV3.Gallery.Controls;
using PhotoViewerV3.Libraries;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Settings;
using PhotoViewerV3.Spike02.Data;
using PhotoViewerV3.Spike02.ViewModels;
using Wpf.Ui.Appearance;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>Ein fehlgeschlagener Prüfpunkt im Rauchtest (der Schritt wird als FAIL protokolliert, der Lauf geht weiter).</summary>
internal sealed class SmokeCheckException : Exception
{
    public SmokeCheckException(string message) : base(message)
    {
    }
}

/// <summary>
/// Integrations-Rauchtest der gesamten App (Welle 2): Bibliothek anlegen und scannen, Galerie (Thumbnails,
/// Sortierung, Seitengröße, FTS-Suche, Filter, Ordnerfilter mit Container-State, Favorit/Bewertung,
/// Verschieben/Rückgängig, Papierkorb), Lightbox (Bild + Video), Ordnerbaum, Einstellungen (Cache, Theme),
/// ClearAndRescan. Läuft auf dem UI-Thread (alle awaits bleiben im Dispatcher-Kontext) und steuert ausschließlich
/// ViewModels, Commands und Messenger — keine Maus-/Tastatureingaben. Jeder Schritt prüft Dateisystem und DB.
/// </summary>
internal sealed class SmokeScenario
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(120);
    private static readonly Regex TokenSplit = new("[^\\p{L}\\p{N}]+", RegexOptions.Compiled);

    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    private readonly string _root;
    private readonly string? _shotsDir;
    private readonly bool _cleanup;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
    private readonly List<string> _results = new();

    private readonly GalleryViewModel _gallery;
    private readonly LibraryViewModel _library;
    private readonly FolderTreeViewModel _folders;
    private readonly SettingsViewModel _settings;
    private readonly ILightboxService _lightbox;
    private readonly ILibraryContextFactory _db;
    private readonly ILibraryService _libraries;
    private readonly IMediaFormatRegistry _formats;
    private readonly IThemeService _theme;
    private readonly IThumbnailService _thumbnails;
    private readonly StatusBarViewModel _statusBar;
    private readonly SmokeFolderPickerService _picker;

    private int _libraryId;
    private int _mediaCount;
    private readonly HashSet<int> _movedIds = new();

    public SmokeScenario(IServiceProvider services, INavigationService navigation, ILogger logger, string root, string? shotsDir, bool cleanup)
    {
        _navigation = navigation;
        _logger = logger;
        _root = root;
        _shotsDir = shotsDir;
        _cleanup = cleanup;

        _gallery = services.GetRequiredService<GalleryViewModel>();
        _library = services.GetRequiredService<LibraryViewModel>();
        _folders = services.GetRequiredService<FolderTreeViewModel>();
        _settings = services.GetRequiredService<SettingsViewModel>();
        _lightbox = services.GetRequiredService<ILightboxService>();
        _db = services.GetRequiredService<ILibraryContextFactory>();
        _libraries = services.GetRequiredService<ILibraryService>();
        _formats = services.GetRequiredService<IMediaFormatRegistry>();
        _theme = services.GetRequiredService<IThemeService>();
        _thumbnails = services.GetRequiredService<IThumbnailService>();
        _statusBar = services.GetRequiredService<StatusBarViewModel>();
        _picker = services.GetRequiredService<SmokeFolderPickerService>();
    }

    public int Failed { get; private set; }

    public IReadOnlyList<string> Results => _results;

    public async Task RunAsync()
    {
        await StepAsync("Start → Startseite", StartPageAsync).ConfigureAwait(true);
        await StepAsync("Bibliothek anlegen + Fresh-Scan", FreshScanAsync).ConfigureAwait(true);
        await StepAsync("Thumbnails JPG/PNG/MP4/MOV", ThumbnailsAsync).ConfigureAwait(true);
        await StepAsync("Sortierung", SortingAsync).ConfigureAwait(true);
        await StepAsync("Seitengröße", PageSizeAsync).ConfigureAwait(true);
        await StepAsync("Suche (FTS)", SearchAsync).ConfigureAwait(true);
        await StepAsync("Filter Bilder/Videos/Favoriten", FiltersAsync).ConfigureAwait(true);
        await StepAsync("Ordnerfilter + Container-State „Leer“", FolderFilterAsync).ConfigureAwait(true);
        await StepAsync("Favorit + Bewertung", FavoriteRatingAsync).ConfigureAwait(true);
        await StepAsync("Verschieben + Rückgängig", MoveUndoAsync).ConfigureAwait(true);
        await StepAsync("Papierkorb", RecycleAsync).ConfigureAwait(true);
        await StepAsync("Lightbox Bild + Video", LightboxAsync).ConfigureAwait(true);
        await StepAsync("Ordnerbaum-Seite", FolderPageAsync).ConfigureAwait(true);
        await StepAsync("Einstellungen + Theme", SettingsAsync).ConfigureAwait(true);
        await StepAsync("ClearAndRescan", RescanAsync).ConfigureAwait(true);
        if (_cleanup)
        {
            await StepAsync("Cleanup", CleanupAsync).ConfigureAwait(true);
        }
    }

    // ---- Schritte ------------------------------------------------------------------------------

    private async Task StartPageAsync()
    {
        var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
        var expected = active is null ? NavigationTags.Library : NavigationTags.Gallery;
        await WaitUntilAsync(() => _navigation.CurrentTag is not null, ShortWait, "erste Navigation").ConfigureAwait(true);
        Check(_navigation.CurrentTag == expected, $"Startseite = {expected} (aktive Bibliothek: {active?.Name ?? "keine"})");
        await SnapshotAsync("01-start-bibliothek").ConfigureAwait(true);
    }

    private async Task FreshScanAsync()
    {
        _mediaCount = CountMediaFiles();
        _navigation.NavigateTo(NavigationTags.Library);
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null, $"Scan ohne Fehler ({_library.ScanSummaryTitle}: {_library.ScanSummary})");
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Wechsel zur Galerie nach dem Scan").ConfigureAwait(true);
        await WaitUntilAsync(
            () => _gallery.HasLibrary && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount && _gallery.CurrentPage.Count == _mediaCount,
            ShortWait,
            $"Galerie zeigt {_mediaCount} Dateien").ConfigureAwait(true);

        _libraryId = await DbScalarAsync<int>("SELECT Id FROM Libraries ORDER BY LastOpenedAt DESC, Id DESC LIMIT 1").ConfigureAwait(true);
        var files = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        var fts = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts").ConfigureAwait(true);
        var txt = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Extension = '.txt'").ConfigureAwait(true);
        Check(files == _mediaCount, $"Files-Tabelle: {files} Zeilen (erwartet {_mediaCount})");
        Check(fts == files, $"FTS-Index: {fts} Zeilen");
        Check(txt == 0, "TXT-Datei ignoriert");
        Check(!_gallery.IsRootOffline, "Bibliotheksordner online");
    }

    private async Task ThumbnailsAsync()
    {
        // Warten, bis jede Kachel ein Bild hat — oder die Queue leer bleibt (Fehler je Datei stehen dann in der DB).
        var deadline = DateTime.UtcNow + LongWait;
        while (DateTime.UtcNow < deadline)
        {
            var missing = _gallery.CurrentPage.Count(i => i.Thumbnail is null);
            if (missing == 0)
            {
                break;
            }

            if (_thumbnails.PendingCount == 0)
            {
                await Task.Delay(2000).ConfigureAwait(true);
                if (_gallery.CurrentPage.Count(i => i.Thumbnail is null) == missing && _thumbnails.PendingCount == 0)
                {
                    break;
                }
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        var withThumb = _gallery.CurrentPage.Count(i => i.Thumbnail is not null);
        var byStatus = await DbRowsAsync("SELECT Status, COUNT(*) FROM Thumbnails GROUP BY Status").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Thumbnails je Status (2=Generated, 3=Failed): {Rows}", string.Join("; ", byStatus));
        var failures = await DbRowsAsync(
            "SELECT f.Name, t.ErrorCode, t.ErrorMessage FROM Thumbnails t JOIN Files f ON f.Id = t.FileId WHERE t.Status = 3").ConfigureAwait(true);
        foreach (var failure in failures)
        {
            _logger.LogWarning("Rauchtest   Thumbnail FEHLER: {Row}", failure);
        }

        var perExtension = await DbRowsAsync(
            "SELECT f.Extension, COUNT(*) FROM Thumbnails t JOIN Files f ON f.Id = t.FileId WHERE t.Status = 2 GROUP BY f.Extension ORDER BY f.Extension").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Generated je Endung: {Rows}", string.Join("; ", perExtension));
        var generated = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE Status = 2").ConfigureAwait(true);
        var cachePaths = await DbRowsAsync("SELECT CachePath FROM Thumbnails WHERE Status = 2").ConfigureAwait(true);
        var missingOnDisk = cachePaths.Count(p => !File.Exists(p));
        var portrait = await DbRowsAsync(
            "SELECT f.Name, t.ByteSize FROM Thumbnails t JOIN Files f ON f.Id = t.FileId WHERE t.Status = 2 ORDER BY f.Name LIMIT 3").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   Beispiel-Thumbnails: {Rows}", string.Join("; ", portrait));

        Check(generated == _mediaCount, $"Thumbnails Generated: {generated}/{_mediaCount} (Failed: {failures.Count})");
        foreach (var ext in new[] { ".jpg", ".png", ".mp4", ".mov" })
        {
            Check(perExtension.Any(r => r.StartsWith(ext + " |", StringComparison.OrdinalIgnoreCase)), $"Thumbnail für {ext} erzeugt");
        }

        Check(missingOnDisk == 0, $"alle {cachePaths.Count} Cache-Dateien vorhanden");
        Check(withThumb == _mediaCount, $"Kacheln mit Vorschaubild: {withThumb}/{_mediaCount}");
        await SnapshotAsync("02-galerie-thumbnails").ConfigureAwait(true);
    }

    private async Task SortingAsync()
    {
        await SetSortAsync(GallerySort.NameAsc).ConfigureAwait(true);
        var names = _gallery.CurrentPage.Select(i => i.Name).ToList();
        Check(IsSorted(names, (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase)), $"Name A→Z: „{names.First()}“ … „{names.Last()}“");

        await SetSortAsync(GallerySort.NameDesc).ConfigureAwait(true);
        names = _gallery.CurrentPage.Select(i => i.Name).ToList();
        Check(IsSorted(names, (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase)), $"Name Z→A: „{names.First()}“ … „{names.Last()}“");

        await SetSortAsync(GallerySort.SizeDesc).ConfigureAwait(true);
        var sizes = _gallery.CurrentPage.Select(i => i.Size).ToList();
        Check(IsSorted(sizes, (a, b) => b.CompareTo(a)), $"Größe groß→klein: {sizes.First():N0} … {sizes.Last():N0} B");

        await SetSortAsync(GallerySort.ModifiedDesc).ConfigureAwait(true);
        Check(_gallery.TotalCount == _mediaCount && _gallery.CurrentPage.Count == _mediaCount, "Standard-Sortierung wiederhergestellt");
    }

    private async Task PageSizeAsync()
    {
        _gallery.PageSize = 200;
        await WaitForGalleryAsync("Seitengröße 200").ConfigureAwait(true);
        Check(_gallery.PageSize == 200 && _gallery.PageCount == 1 && _gallery.CurrentPage.Count == _gallery.TotalCount,
            $"Seitengröße 200 → {_gallery.PageCount} Seite(n), {_gallery.CurrentPage.Count} Kacheln");
        Check(!_gallery.NextPageCommand.CanExecute(null) && !_gallery.PrevPageCommand.CanExecute(null), "Pager bei einer Seite deaktiviert");
        _gallery.PageSize = 500;
        await WaitForGalleryAsync("Seitengröße 500").ConfigureAwait(true);
        Check(_gallery.PageSize == 500 && _gallery.StatusText.Contains("Seite 1 von 1", StringComparison.Ordinal), $"Status: {_gallery.StatusText}");
    }

    private async Task SearchAsync()
    {
        var expected = MediaFiles().Count(f => TokenSplit.Split(Path.GetFileName(f)).Any(t => t.StartsWith("img", StringComparison.OrdinalIgnoreCase)));
        _gallery.SearchTerm = "IMG";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche IMG").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, "Suche ohne Fehler");
        Check(_gallery.TotalCount == expected, $"Suche „IMG“: {_gallery.TotalCount} Treffer (erwartet {expected})");

        _gallery.SearchTerm = "foto-(2024):\"x*";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche mit Sonderzeichen").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Sonderzeichen-Suche ohne FTS-Syntaxfehler ({_gallery.TotalCount} Treffer)");

        _gallery.SearchTerm = "---";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nur Sonderzeichen").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null && _gallery.TotalCount == 0, "Suche „---“ → 0 Treffer, kein Fehler");

        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);
        Check(_gallery.SearchTerm is null && _gallery.TotalCount == _mediaCount, "Suche geleert → alle Dateien");
    }

    private async Task FiltersAsync()
    {
        var videos = MediaFiles().Count(f => _formats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        var images = _mediaCount - videos;

        _gallery.Filter = GalleryFilter.Videos;
        await WaitForGalleryAsync("Filter Videos").ConfigureAwait(true);
        Check(_gallery.TotalCount == videos && _gallery.CurrentPage.All(i => i.IsVideo), $"Filter Videos: {_gallery.TotalCount} (erwartet {videos})");

        _gallery.Filter = GalleryFilter.Images;
        await WaitForGalleryAsync("Filter Bilder").ConfigureAwait(true);
        Check(_gallery.TotalCount == images && _gallery.CurrentPage.All(i => !i.IsVideo), $"Filter Bilder: {_gallery.TotalCount} (erwartet {images})");

        _gallery.Filter = GalleryFilter.Favorites;
        await WaitForGalleryAsync("Filter Favoriten").ConfigureAwait(true);
        Check(_gallery.TotalCount == 0 && _gallery.IsEmpty, $"Filter Favoriten (noch keine): {_gallery.EmptyMessage}");

        _gallery.Filter = GalleryFilter.All;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);
        Check(_gallery.TotalCount == _mediaCount, "Filter Alle");
    }

    private async Task FolderFilterAsync()
    {
        var leerId = await FolderIdAsync(Path.Combine(_root, "Leer")).ConfigureAwait(true);
        var screenshotsId = await FolderIdAsync(Path.Combine(_root, "2025", "Screenshots")).ConfigureAwait(true);
        var screenshotCount = Directory.EnumerateFiles(Path.Combine(_root, "2025", "Screenshots")).Count(f => _formats.IsSupported(Path.GetExtension(f)));

        _messenger.Send(new FolderSelectedMessage(_libraryId, screenshotsId, true));
        await WaitUntilAsync(
            () => _gallery.FolderId == screenshotsId && !_gallery.IsLoading && _gallery.TotalCount == screenshotCount,
            ShortWait,
            $"Ordnerfilter Screenshots ({screenshotCount} PNG)").ConfigureAwait(true);
        Check(_gallery.HasFolderFilter && _gallery.FolderName == "Screenshots", $"Chip: {_gallery.FolderChipText}");

        _messenger.Send(new FolderSelectedMessage(_libraryId, leerId, false));
        await WaitUntilAsync(
            () => _gallery.FolderId == leerId && _gallery.ShowSubfolders && !_gallery.IsLoading,
            ShortWait,
            "Container-State für „Leer“ (ohne Unterordner)").ConfigureAwait(true);
        var subfolders = _gallery.Subfolders.Select(s => s.Name).ToList();
        Check(subfolders.SequenceEqual(new[] { "Sub A", "Sub B" }), $"Unterordner-Kacheln: {string.Join(", ", subfolders)}");
        Check(_gallery.Subfolders.All(s => s.FileCount == 1), "Dateizähler je Unterordner-Kachel = 1");
        Check(!_gallery.ShowGrid && !_gallery.IsEmpty, "Grid ausgeblendet, kein Leerzustand");
        await SnapshotAsync("03-galerie-container-leer").ConfigureAwait(true);

        await _gallery.OpenSubfolderCommand.ExecuteAsync(_gallery.Subfolders[0]).ConfigureAwait(true);
        await WaitUntilAsync(
            () => !_gallery.ShowSubfolders && !_gallery.IsLoading && _gallery.TotalCount == 1,
            ShortWait,
            "Unterordner-Kachel „Sub A“ geöffnet").ConfigureAwait(true);
        Check(_gallery.FolderName == "Sub A" && !_gallery.IncludeSubfolders, $"Chip: {_gallery.FolderChipText}");

        _messenger.Send(new FolderSelectedMessage(_libraryId, leerId, true));
        await WaitUntilAsync(
            () => _gallery.FolderId == leerId && _gallery.IncludeSubfolders && !_gallery.IsLoading && _gallery.TotalCount == 2,
            ShortWait,
            "„Leer“ rekursiv → 2 Dateien").ConfigureAwait(true);

        await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Ordnerfilter aufgehoben").ConfigureAwait(true);
        Check(_gallery.FolderId is null && !_gallery.HasFolderFilter && _gallery.TotalCount == _mediaCount, "Ordnerfilter aufgehoben → alle Dateien");
    }

    private async Task FavoriteRatingAsync()
    {
        _gallery.ClearSelectionCommand.Execute(null);
        var targets = _gallery.CurrentPage.Take(3).ToList();
        foreach (var item in targets)
        {
            item.IsSelected = true;
        }

        Check(_gallery.SelectedCount == 3 && _gallery.HasSelection && _gallery.SelectionText == "3 ausgewählt", $"Auswahl: {_gallery.SelectionText}");

        await _gallery.FavoriteSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await _gallery.SetSelectionRatingCommand.ExecuteAsync(new RatingRequest(null, 4)).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true); // FilesChangedMessage(Metadata) → RefreshMetadata

        var ids = string.Join(",", targets.Select(t => t.Id.ToString(CultureInfo.InvariantCulture)));
        var rows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids}) AND IsFavorite = 1 AND Rating = 4").ConfigureAwait(true);
        Check(rows == 3, "DB: 3 Dateien mit IsFavorite=1 und Rating=4");
        Check(targets.All(t => t.IsFavorite && t.Rating == 4), "Kacheln zeigen Favorit + 4 Sterne");
        Check(_gallery.SelectionRating == 4, "Aktionsleiste: gemeinsame Bewertung 4");
        Check(_gallery.ErrorMessage is null, "keine Fehlermeldung");
        await SnapshotAsync("04-galerie-auswahl-aktionsleiste").ConfigureAwait(true);

        _gallery.ClearSelectionCommand.Execute(null);
        Check(_gallery.SelectedCount == 0 && !_gallery.HasSelection, "Auswahl aufgehoben (Esc)");

        _gallery.Filter = GalleryFilter.Favorites;
        await WaitForGalleryAsync("Filter Favoriten").ConfigureAwait(true);
        Check(_gallery.TotalCount == 3, $"Favoriten-Filter: {_gallery.TotalCount} Treffer");
        _gallery.Filter = GalleryFilter.All;
        await WaitForGalleryAsync("Filter Alle").ConfigureAwait(true);

        var single = _gallery.CurrentPage.First(i => targets.All(t => t.Id != i.Id));
        await _gallery.ToggleFavoriteCommand.ExecuteAsync(single).ConfigureAwait(true);
        await _gallery.SetItemRatingCommand.ExecuteAsync(new RatingRequest(single, 2)).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);
        var row = await DbRowsAsync("SELECT IsFavorite, Rating FROM Files WHERE Id = $id", ("$id", single.Id)).ConfigureAwait(true);
        Check(row.FirstOrDefault() == "1 | 2", $"DB Einzelkachel „{single.Name}“: IsFavorite | Rating = {row.FirstOrDefault()}");
        Check(single.IsFavorite && single.Rating == 2 && single.HasRating, "Einzelkachel zeigt Herz + 2 Sterne");
    }

    private async Task MoveUndoAsync()
    {
        _gallery.ClearSelectionCommand.Execute(null);
        var unsorted = Path.Combine(_root, "Unsortiert");
        var movers = _gallery.CurrentPage.Where(i => PathEquals(i.FolderPath, unsorted) && !i.IsVideo).Take(2).ToList();
        Check(movers.Count == 2, "2 Bilder aus „Unsortiert“ gewählt");
        var originalFolderId = movers[0].FolderId;
        foreach (var item in movers)
        {
            item.IsSelected = true;
            _movedIds.Add(item.Id);
        }

        var ids = string.Join(",", movers.Select(m => m.Id.ToString(CultureInfo.InvariantCulture)));
        var target = Path.Combine(unsorted, "Smoke-Verschoben");
        _picker.NextFolder = target;

        await _gallery.MoveSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Verschieben beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Verschieben").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Verschieben ohne Fehler ({_gallery.StatusText})");
        foreach (var item in movers)
        {
            Check(File.Exists(Path.Combine(target, item.Name)) && !File.Exists(item.SourcePath), $"Datei verschoben: {item.Name}");
        }

        var targetFolderId = await FolderIdAsync(target).ConfigureAwait(true);
        var movedRows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids}) AND FolderId = $f", ("$f", targetFolderId)).ConfigureAwait(true);
        Check(movedRows == 2, $"DB: Files.FolderId = {targetFolderId} (neuer Unterordner „Smoke-Verschoben“)");
        Check(_gallery.CurrentPage.Where(i => _movedIds.Contains(i.Id)).All(i => PathEquals(i.FolderPath, target)), "Kacheln zeigen den neuen Ordner");
        Check(_gallery.CanUndo, "Rückgängig verfügbar");

        await _gallery.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Rückgängig beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Rückgängig").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null, $"Rückgängig ohne Fehler ({_gallery.StatusText})");
        foreach (var item in movers)
        {
            Check(File.Exists(item.SourcePath) && !File.Exists(Path.Combine(target, item.Name)), $"Datei zurück: {item.Name}");
        }

        var backRows = await DbScalarAsync<int>($"SELECT COUNT(*) FROM Files WHERE Id IN ({ids}) AND FolderId = $f", ("$f", originalFolderId)).ConfigureAwait(true);
        Check(backRows == 2, $"DB: Files.FolderId wieder {originalFolderId}");
        Check(!_gallery.CanUndo, "nach Rückgängig nichts mehr rückgängig zu machen");
        Check(_statusBar.RecoveryText is null, "Statusleiste: kein Recovery-Hinweis nach erfolgreichen Vorgängen");

        // Fehlgeschlagener Batch provozieren (Zielname existiert bereits → Item scheitert, Batch Failed)
        // → Fehlerdialog (Fake) + Recovery-Hinweis in der Statusleiste (ADR-0013 Nr. 4).
        _gallery.ClearSelectionCommand.Execute(null);
        var blocked = movers[0];
        var blocker = Path.Combine(target, blocked.Name);
        File.WriteAllBytes(blocker, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        try
        {
            _gallery.CurrentPage.First(i => i.Id == blocked.Id).IsSelected = true;
            _picker.NextFolder = target;
            await _gallery.MoveSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
            await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "fehlgeschlagenes Verschieben beendet").ConfigureAwait(true);
            await WaitForGalleryAsync("Reload nach fehlgeschlagenem Verschieben").ConfigureAwait(true);
            Check(File.Exists(blocked.SourcePath), $"Quelle unverändert: {blocked.Name}");
            Check(_gallery.StatusText.Contains("fehlgeschlagen", StringComparison.Ordinal), $"Galerie-Status: {_gallery.StatusText}");
            // Komplett fehlgeschlagener Batch sendet keine FilesChangedMessage → Statusleiste pollt (10 s)
            await WaitUntilAsync(() => _statusBar.RecoveryText is not null, TimeSpan.FromSeconds(25), "Recovery-Hinweis in der Statusleiste").ConfigureAwait(true);
            Check(_statusBar.RecoveryText!.Contains("fehlgeschlagen", StringComparison.Ordinal), $"Statusleiste: {_statusBar.RecoveryText} — {_statusBar.RecoveryDetails}");
        }
        finally
        {
            File.Delete(blocker);
        }

        var batches = await DbRowsAsync("SELECT Id, OperationType, Status FROM OperationBatches ORDER BY Id").ConfigureAwait(true);
        _logger.LogInformation("Rauchtest   OperationBatches (Status 2=Applied, 4=Undone, 5=Failed): {Rows}", string.Join("; ", batches));
        Check(batches.Any(b => b.EndsWith("| 5", StringComparison.Ordinal)), "DB: ein OperationBatch mit Status Failed");
    }

    private async Task RecycleAsync()
    {
        _gallery.ClearSelectionCommand.Execute(null);
        var unsorted = Path.Combine(_root, "Unsortiert");
        var victim = _gallery.CurrentPage.First(i => PathEquals(i.FolderPath, unsorted) && !i.IsVideo && !_movedIds.Contains(i.Id));
        victim.IsSelected = true;
        var thumbPath = await DbScalarAsync<string>("SELECT CachePath FROM Thumbnails WHERE FileId = $id", ("$id", victim.Id)).ConfigureAwait(true);
        var token = Path.GetFileNameWithoutExtension(victim.Name);
        var ftsBefore = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH $t", ("$t", "\"" + token + "\"")).ConfigureAwait(true);

        await _gallery.RecycleSelectionCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsOperationRunning, ShortWait, "Papierkorb beendet").ConfigureAwait(true);
        await WaitForGalleryAsync("Reload nach Papierkorb").ConfigureAwait(true);

        Check(_gallery.ErrorMessage is null, $"Papierkorb ohne Fehler ({_gallery.StatusText})");
        Check(!File.Exists(victim.SourcePath), $"Datei im Papierkorb: {victim.Name}");
        var fileRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE Id = $id", ("$id", victim.Id)).ConfigureAwait(true);
        var thumbRows = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE FileId = $id", ("$id", victim.Id)).ConfigureAwait(true);
        var ftsAfter = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts WHERE FilesFts MATCH $t", ("$t", "\"" + token + "\"")).ConfigureAwait(true);
        Check(fileRows == 0, "DB: Files-Zeile entfernt");
        Check(thumbRows == 0, "DB: Thumbnails-Zeile entfernt");
        Check(ftsAfter == ftsBefore - 1, $"FTS-Eintrag entfernt ({ftsBefore} → {ftsAfter})");
        Check(string.IsNullOrEmpty(thumbPath) || !File.Exists(thumbPath), "Thumbnail-Cache-Datei entfernt");
        _mediaCount--;
        Check(_gallery.TotalCount == _mediaCount && _gallery.CurrentPage.All(i => i.Id != victim.Id), $"Galerie: {_gallery.TotalCount} Dateien, Kachel entfernt");
    }

    private async Task LightboxAsync()
    {
        var image = _gallery.CurrentPage.First(i => !i.IsVideo);
        var startIndex = _gallery.CurrentPage.IndexOf(image);
        _gallery.OpenInLightboxCommand.Execute(image);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault();
        Check(window is not null, "Lightbox-Fenster vorhanden");
        var vm = window!.ViewModel;
        await WaitUntilAsync(() => vm.IsImage && vm.ImageSource is not null && !vm.IsLoading, ShortWait, "Bild dekodiert").ConfigureAwait(true);
        Check(vm.CurrentItem?.FileId == image.Id && vm.CurrentIndex == startIndex, $"Lightbox zeigt „{vm.Title}“ ({vm.CounterText}, {vm.Subtitle})");
        Check(ReferenceEquals(window.Owner, Application.Current.MainWindow), "Lightbox-Owner = Hauptfenster");
        Check(vm.ErrorMessage is null, "kein Lightbox-Fehler");
        vm.ToggleInfoCommand.Execute(null);
        Check(vm.IsInfoVisible && vm.FileInfoRows.Count >= 5, $"Info-Panel: {vm.FileInfoRows.Count} Datei-Zeilen");
        await SnapshotAsync("05-lightbox-bild", window).ConfigureAwait(true);

        if (vm.CanGoNext)
        {
            vm.NextCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentIndex == startIndex + 1 && !vm.IsLoading, ShortWait, "Nächster Eintrag").ConfigureAwait(true);
            vm.PreviousCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentIndex == startIndex && !vm.IsLoading, ShortWait, "Vorheriger Eintrag").ConfigureAwait(true);
            Check(vm.CurrentItem?.FileId == image.Id, "Navigation ← → zurück beim Startbild");
        }

        vm.LastCommand.Execute(null);
        await WaitUntilAsync(() => vm.CurrentIndex == vm.Count - 1 && !vm.IsLoading, ShortWait, "Ende").ConfigureAwait(true);
        vm.FirstCommand.Execute(null);
        await WaitUntilAsync(() => vm.CurrentIndex == 0 && !vm.IsLoading, ShortWait, "Anfang").ConfigureAwait(true);

        foreach (var extension in new[] { ".mp4", ".mov" })
        {
            var video = _gallery.CurrentPage.FirstOrDefault(i => i.IsVideo && i.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));
            if (video is null)
            {
                continue;
            }

            _gallery.OpenInLightboxCommand.Execute(video);
            Check(ReferenceEquals(Application.Current.Windows.OfType<LightboxWindow>().FirstOrDefault(), window), "Lightbox-Fenster wiederverwendet");
            await WaitUntilAsync(() => vm.IsVideo && vm.CurrentItem?.FileId == video.Id && (vm.IsPlaying || vm.ErrorMessage is not null), TimeSpan.FromSeconds(30), $"Video {extension} spielt").ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);
            Check(vm.ErrorMessage is null && vm.Player is not null && vm.IsPlaying, $"Video {extension}: „{vm.Title}“ {vm.TimeText}");
            Check(vm.MediaInfoRows.Any(r => r.Label is "Dauer" or "Auflösung"), $"Video-Infos: {string.Join(", ", vm.MediaInfoRows.Select(r => r.Label + "=" + r.Value))}");
            if (extension == ".mp4")
            {
                await SnapshotAsync("06-lightbox-video", window).ConfigureAwait(true);
            }

            vm.PlayPauseCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsPlaying, ShortWait, "Pause").ConfigureAwait(true);
        }

        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        Check(!Application.Current.Windows.OfType<LightboxWindow>().Any(), "Lightbox-Fenster zerstört");
    }

    private async Task FolderPageAsync()
    {
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Folders && !_folders.IsLoading && _folders.Roots.Count > 0,
            ShortWait,
            "Ordnerbaum geladen").ConfigureAwait(true);
        Check(_folders.HasLibrary && _folders.HintText is null, $"Ordnerbaum für „{_folders.LibraryName}“ ohne Hinweis");

        var root = _folders.Roots[0];
        root.IsExpanded = true;
        var childNames = string.Join(", ", root.Children.Select(c => c.Name));
        var year = root.Children.FirstOrDefault(c => c.Name == "2024");
        Check(year is not null, $"Wurzel „{root.Name}“ ({root.TotalFileCount} Dateien) hat Kinder: {childNames}");
        Check(root.TotalFileCount == _mediaCount, $"Wurzel TotalFileCount = {root.TotalFileCount} (erwartet {_mediaCount})");

        year!.IsExpanded = true;
        var familie = year.Children.First(c => c.Name == "Familie");
        var familieCount = Directory.EnumerateFiles(familie.Path).Count(f => _formats.IsSupported(Path.GetExtension(f)));
        familie.IsSelected = true;
        await WaitUntilAsync(
            () => _gallery.FolderId == familie.Id && !_gallery.IsLoading && _gallery.TotalCount == familieCount,
            ShortWait,
            $"Baum-Auswahl „Familie“ → {familieCount} Dateien").ConfigureAwait(true);
        Check(_folders.SelectionText == familie.Path, $"SelectionText = {_folders.SelectionText}");
        Check(familie.FileCount == familieCount && familie.TotalFileCount == familieCount, $"Zähler im Baum: {familie.FileCount}/{familie.TotalFileCount}");
        await SnapshotAsync("07-ordnerbaum").ConfigureAwait(true);

        _folders.IncludeSubfolders = false;
        await WaitUntilAsync(() => _gallery.FolderId == familie.Id && !_gallery.IncludeSubfolders && !_gallery.IsLoading, ShortWait, "ohne Unterordner").ConfigureAwait(true);
        _folders.IncludeSubfolders = true;
        await WaitUntilAsync(() => _gallery.IncludeSubfolders && !_gallery.IsLoading, ShortWait, "mit Unterordnern").ConfigureAwait(true);

        _folders.SelectWholeLibraryCommand.Execute(null);
        await WaitUntilAsync(
            () => _gallery.FolderId is null && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount,
            ShortWait,
            "Gesamte Bibliothek").ConfigureAwait(true);
        Check(_folders.SelectionText == "Gesamte Bibliothek", "Baum: gesamte Bibliothek");
    }

    private async Task SettingsAsync()
    {
        _navigation.NavigateTo(NavigationTags.Settings);
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Settings && _settings.ThumbnailCacheText != "wird berechnet…" && _settings.AppVersionText.Length > 0,
            ShortWait,
            "Einstellungen geladen").ConfigureAwait(true);
        Check(_settings.ThumbnailCacheText.Contains("Dateien", StringComparison.Ordinal), $"Cache: {_settings.ThumbnailCacheText}");
        Check(_settings.AppVersionText.Contains("0.2.0", StringComparison.Ordinal), $"Version: {_settings.AppVersionText}");
        Check(_settings.IsFfmpegAvailable, $"FFmpeg: {_settings.FfmpegText}");
        Check(_settings.DatabasePath == _db.DatabasePath && _settings.DatabaseSizeText.Length > 0, $"DB: {_settings.DatabasePath} ({_settings.DatabaseSizeText})");
        Check(_settings.ImageFormatsText.Contains(".jpg", StringComparison.Ordinal) && _settings.VideoFormatsText.Contains(".mp4", StringComparison.Ordinal), "Formate angezeigt");

        _settings.IsThemeLight = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Light && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
        await SnapshotAsync("08-einstellungen-hell").ConfigureAwait(true);

        _settings.IsThemeDark = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.Dark && ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
        await SnapshotAsync("09-einstellungen-dunkel").ConfigureAwait(true);

        _settings.IsThemeSystem = true;
        await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        var stored = await DbScalarAsync<string>("SELECT Value FROM Settings WHERE Key = 'ui.theme'").ConfigureAwait(true);
        Check(stored == "System", $"Settings-Tabelle ui.theme = {stored}");
        Check(_settings.ErrorMessage is null, "Einstellungen ohne Fehler");
    }

    private async Task RescanAsync()
    {
        var previousMaxId = await DbScalarAsync<int>("SELECT COALESCE(MAX(Id), 0) FROM Files").ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Library);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Library, ShortWait, "Bibliotheksseite").ConfigureAwait(true);
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_library.ErrorMessage is null && _library.IsScanSummaryOpen && !_library.ScanSummaryIsWarning, $"ClearAndRescan: {_library.ScanSummary}");

        // ClearAndRescan vergibt neue FileIds (sqlite_sequence) — erst wenn die Galerie die neuen Ids zeigt, ist sie umgestellt.
        await WaitUntilAsync(
            () => _navigation.CurrentTag == NavigationTags.Gallery && !_gallery.IsLoading && _gallery.TotalCount == _mediaCount
                  && _gallery.CurrentPage.Count == _mediaCount && _gallery.CurrentPage.All(i => i.Id > previousMaxId),
            ShortWait,
            $"Galerie nach ClearAndRescan: {_mediaCount} Dateien mit neuen Ids (> {previousMaxId})").ConfigureAwait(true);
        var files = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        var fts = await DbScalarAsync<int>("SELECT COUNT(*) FROM FilesFts").ConfigureAwait(true);
        var favorites = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE IsFavorite = 1").ConfigureAwait(true);
        Check(files == _mediaCount && fts == files, $"DB nach ClearAndRescan: {files} Files, {fts} FTS-Zeilen");
        Check(favorites == 0, "ClearAndRescan hat Favoriten verworfen (erwartet in Welle 2)");

        await WaitUntilAsync(
            () => _gallery.CurrentPage.Count > 0 && _gallery.CurrentPage.All(i => i.Thumbnail is not null),
            LongWait,
            "Thumbnails nach ClearAndRescan (neue FileIds)").ConfigureAwait(true);
        var generated = await DbScalarAsync<int>("SELECT COUNT(*) FROM Thumbnails WHERE Status = 2").ConfigureAwait(true);
        Check(generated == _mediaCount, $"Thumbnails neu erzeugt: {generated}/{_mediaCount}");
    }

    private async Task CleanupAsync()
    {
        _navigation.NavigateTo(NavigationTags.Library);
        await _library.RunSmokeCleanupAsync(_root).ConfigureAwait(true);
        var libraries = await DbScalarAsync<int>("SELECT COUNT(*) FROM Libraries").ConfigureAwait(true);
        Check(libraries == 0, "Bibliothek entfernt");
    }

    // ---- Hilfen --------------------------------------------------------------------------------

    private async Task StepAsync(string name, Func<Task> action)
    {
        _logger.LogInformation("Rauchtest ▶ {Step}", name);
        try
        {
            await action().ConfigureAwait(true);
            _results.Add($"OK   {name}");
            _logger.LogInformation("Rauchtest ✔ {Step}", name);
        }
        catch (Exception ex)
        {
            Failed++;
            _results.Add($"FAIL {name}: {ex.Message}");
            _logger.LogError(ex, "Rauchtest ✘ {Step}", name);
        }
    }

    private void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new SmokeCheckException(what);
        }

        _logger.LogInformation("Rauchtest   ✓ {What}", what);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SmokeCheckException($"Timeout nach {timeout.TotalSeconds:N0} s: {what}");
            }

            await Task.Delay(100).ConfigureAwait(true);
        }
    }

    /// <summary>Wartet auf einen gerade angestoßenen (ggf. 300 ms gebündelten) Reload der Galerie.</summary>
    private async Task WaitForGalleryAsync(string what)
    {
        await Task.Delay(450).ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.IsLoading, ShortWait, what).ConfigureAwait(true);
        await Task.Delay(100).ConfigureAwait(true);
    }

    private async Task SetSortAsync(GallerySort sort)
    {
        _gallery.SelectedSort = _gallery.SortOptions.First(o => o.Sort == sort);
        await WaitForGalleryAsync($"Sortierung {sort}").ConfigureAwait(true);
        Check(_gallery.SelectedSort.Sort == sort && _gallery.ErrorMessage is null, $"Sortierung {sort} angewendet");
    }

    private async Task SnapshotAsync(string name, Window? window = null)
    {
        if (_shotsDir is null)
        {
            return;
        }

        window ??= Application.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        // Layout, Render und Bild-Bindings abwarten, dann das eigene Fenster-Handle abfotografieren.
        await Task.Delay(500).ConfigureAwait(true);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var path = Path.Combine(_shotsDir, name + ".png");
        try
        {
            // Lightbox im Video-Modus: VideoView (HwndHost) per PrintWindow + LibVLC-Overlay-Fenster einblenden.
            FrameworkElement? hwndHost = null;
            Window? overlay = null;
            if (window is LightboxWindow lightbox && lightbox.ViewModel.IsVideo)
            {
                hwndHost = lightbox.FindName("VideoView") as FrameworkElement;
                overlay = WindowCapture.FindVlcForegroundWindow(lightbox);
            }

            _logger.LogInformation(
                "Rauchtest   Fenster „{Title}“: sichtbar={Visible} aktiv={Active} Zustand={State} {W:N0}×{H:N0} @ {Left:N0},{Top:N0}",
                window.Title, window.IsVisible, window.IsActive, window.WindowState, window.ActualWidth, window.ActualHeight, window.Left, window.Top);
            var method = WindowCapture.TrySave(window, path, hwndHost, overlay);
            _logger.LogInformation("Rauchtest   Screenshot {State}: {Path}", method is null ? "FEHLGESCHLAGEN" : $"gespeichert ({method})", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rauchtest   Screenshot fehlgeschlagen: {Path}", path);
        }
    }

    private IEnumerable<string> MediaFiles()
        => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => _formats.IsSupported(Path.GetExtension(f)));

    private int CountMediaFiles() => MediaFiles().Count();

    private async Task<int> FolderIdAsync(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var id = await DbScalarAsync<int>(
            "SELECT Id FROM Folders WHERE LibraryId = $lib AND (Path = $p OR Path = $p2) LIMIT 1",
            ("$lib", _libraryId),
            ("$p", trimmed),
            ("$p2", trimmed + Path.DirectorySeparatorChar)).ConfigureAwait(true);
        if (id == 0)
        {
            throw new SmokeCheckException($"Ordner nicht im Katalog: {path}");
        }

        return id;
    }

    private Task<T> DbScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) => Task.Run(() =>
    {
        using var connection = _db.OpenSqliteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    });

    private Task<List<string>> DbRowsAsync(string sql, params (string Name, object Value)[] parameters) => Task.Run(() =>
    {
        using var connection = _db.OpenSqliteConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL";
            }

            rows.Add(string.Join(" | ", cells));
        }

        return rows;
    });

    private static bool PathEquals(string a, string b)
        => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSorted<T>(IReadOnlyList<T> values, Comparison<T> comparison)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (comparison(values[i - 1], values[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }
}

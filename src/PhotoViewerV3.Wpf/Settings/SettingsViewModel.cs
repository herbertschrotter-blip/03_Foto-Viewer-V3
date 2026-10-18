using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Settings;

/// <summary>
/// Einstellungen: Farbschema, Speicherorte (DB, Logs), Thumbnail-Cache (Größe, Leeren), FFmpeg-Status,
/// unterstützte Formate und App-Version.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string FfmpegInstallCommand = "winget install Gyan.FFmpeg";

    private readonly ILibraryContextFactory _factory;
    private readonly IAppDataPaths _paths;
    private readonly IVideoThumbnailRenderer _videoRenderer;
    private readonly IMediaFormatRegistry _formats;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ILibraryContextFactory factory,
        IAppDataPaths paths,
        IVideoThumbnailRenderer videoRenderer,
        IMediaFormatRegistry formats,
        IThemeService theme,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        _factory = factory;
        _paths = paths;
        _videoRenderer = videoRenderer;
        _formats = formats;
        _theme = theme;
        _dialogs = dialogs;
        _logger = logger;
    }

    [ObservableProperty] private string _databasePath = string.Empty;
    [ObservableProperty] private string _databaseSizeText = string.Empty;
    [ObservableProperty] private string _logsPath = string.Empty;
    [ObservableProperty] private string _thumbnailsPath = string.Empty;
    [ObservableProperty] private string _thumbnailCacheText = "wird berechnet…";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ClearThumbnailCacheCommand))] private bool _isCacheBusy;
    [ObservableProperty] private bool _isFfmpegAvailable;
    [ObservableProperty] private string _ffmpegText = string.Empty;
    [ObservableProperty] private string _imageFormatsText = string.Empty;
    [ObservableProperty] private string _videoFormatsText = string.Empty;
    [ObservableProperty] private string _appVersionText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isErrorOpen;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _isInfoOpen;

    public string FfmpegCommandText => FfmpegInstallCommand;

    // Theme-Auswahl als drei TwoWay-gebundene RadioButtons; der Setter mit true wendet den Modus an.
    public bool IsThemeSystem
    {
        get => _theme.Mode == AppThemeMode.System;
        set => OnThemeRadioChanged(value, AppThemeMode.System);
    }

    public bool IsThemeLight
    {
        get => _theme.Mode == AppThemeMode.Light;
        set => OnThemeRadioChanged(value, AppThemeMode.Light);
    }

    public bool IsThemeDark
    {
        get => _theme.Mode == AppThemeMode.Dark;
        set => OnThemeRadioChanged(value, AppThemeMode.Dark);
    }

    partial void OnErrorMessageChanged(string? value) => IsErrorOpen = !string.IsNullOrEmpty(value);

    partial void OnInfoMessageChanged(string? value) => IsInfoOpen = !string.IsNullOrEmpty(value);

    [RelayCommand]
    private async Task LoadAsync()
    {
        DatabasePath = _factory.DatabasePath;
        DatabaseSizeText = FileSizeText(_factory.DatabasePath);
        LogsPath = _paths.LogsRoot;
        ThumbnailsPath = _paths.ThumbnailsRoot;

        IsFfmpegAvailable = _videoRenderer.IsAvailable;
        FfmpegText = IsFfmpegAvailable
            ? "FFmpeg wurde gefunden — Vorschaubilder für Videos werden erzeugt."
            : "FFmpeg wurde nicht gefunden — Videos erhalten keine Vorschaubilder. Installation per winget (danach App neu starten):";

        ImageFormatsText = string.Join("  ", _formats.ImageExtensions.OrderBy(e => e, StringComparer.Ordinal));
        VideoFormatsText = string.Join("  ", _formats.VideoExtensions.OrderBy(e => e, StringComparer.Ordinal));
        AppVersionText = BuildVersionText();
        NotifyThemeChanged();

        await RefreshCacheStatsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(_paths.LogsRoot);

    [RelayCommand]
    private void OpenDatabaseFolder() => OpenFolder(Path.GetDirectoryName(_factory.DatabasePath) ?? _paths.AppDataRoot);

    [RelayCommand]
    private void OpenThumbnailsFolder() => OpenFolder(_paths.ThumbnailsRoot);

    [RelayCommand]
    private void CopyFfmpegCommand()
    {
        try
        {
            Clipboard.SetText(FfmpegInstallCommand);
            InfoMessage = "Befehl in die Zwischenablage kopiert.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Zwischenablage nicht verfügbar: {ex.Message}";
        }
    }

    private bool CanClearCache() => !IsCacheBusy;

    [RelayCommand(CanExecute = nameof(CanClearCache))]
    private async Task ClearThumbnailCacheAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "Vorschaubild-Cache leeren?",
            "Alle erzeugten Vorschaubilder werden gelöscht und bei Bedarf neu erzeugt. Für in dieser Sitzung bereits " +
            "angezeigte Bilder greift das erst nach dem nächsten App-Start.\n\nFotos und Videos selbst sind nicht betroffen.",
            "Cache leeren",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        IsCacheBusy = true;
        try
        {
            var (deleted, skipped, rows) = await Task.Run(ClearCache).ConfigureAwait(true);
            InfoMessage = $"{deleted:N0} Dateien und {rows:N0} Datenbankeinträge entfernt"
                          + (skipped > 0 ? $" ({skipped:N0} Dateien in Benutzung übersprungen)." : ".");
            _logger.LogInformation("Thumbnail-Cache geleert: {Deleted} Dateien, {Rows} Zeilen, {Skipped} übersprungen", deleted, rows, skipped);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Cache konnte nicht geleert werden: {ex.Message}";
            _logger.LogError(ex, "Thumbnail-Cache konnte nicht geleert werden");
        }
        finally
        {
            IsCacheBusy = false;
            await RefreshCacheStatsAsync().ConfigureAwait(true);
        }
    }

    private async void OnThemeRadioChanged(bool isChecked, AppThemeMode mode)
    {
        if (!isChecked || _theme.Mode == mode)
        {
            return;
        }

        try
        {
            await _theme.SetModeAsync(mode).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Farbschema konnte nicht gespeichert werden: {ex.Message}";
            _logger.LogError(ex, "Theme konnte nicht gesetzt werden: {Mode}", mode);
        }

        NotifyThemeChanged();
    }

    private void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    private async Task RefreshCacheStatsAsync()
    {
        ThumbnailCacheText = "wird berechnet…";
        try
        {
            var root = _paths.ThumbnailsRoot;
            var (count, bytes) = await Task.Run(() => MeasureDirectory(root)).ConfigureAwait(true);
            ThumbnailCacheText = count == 0 ? "leer" : $"{count:N0} Dateien · {Formatting.Bytes(bytes)}";
        }
        catch (Exception ex)
        {
            ThumbnailCacheText = "unbekannt";
            _logger.LogWarning(ex, "Thumbnail-Cache konnte nicht vermessen werden");
        }
    }

    private (int Deleted, int Skipped, int Rows) ClearCache()
    {
        int rows;
        using (var connection = _factory.OpenSqliteConnection())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Thumbnails;";
            rows = cmd.ExecuteNonQuery();
        }

        var deleted = 0;
        var skipped = 0;
        var root = _paths.ThumbnailsRoot;
        if (Directory.Exists(root))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }

        return (deleted, skipped, rows);
    }

    private static (int Count, long Bytes) MeasureDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var count = 0;
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            count++;
            bytes += file.Length;
        }

        return (count, bytes);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht geöffnet werden: {ex.Message}";
            _logger.LogWarning(ex, "Explorer konnte nicht geöffnet werden: {Path}", path);
        }
    }

    private static string FileSizeText(string path)
    {
        try
        {
            return File.Exists(path) ? Formatting.Bytes(new FileInfo(path).Length) : "noch nicht angelegt";
        }
        catch (IOException)
        {
            return "unbekannt";
        }
    }

    private static string BuildVersionText()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "?"
            : informational;

        var location = assembly.Location;
        var buildDate = !string.IsNullOrEmpty(location) && File.Exists(location)
            ? $" · Build vom {File.GetLastWriteTime(location):g}"
            : string.Empty;

        return $"PhotoViewer V3 {version}{buildDate}";
    }
}

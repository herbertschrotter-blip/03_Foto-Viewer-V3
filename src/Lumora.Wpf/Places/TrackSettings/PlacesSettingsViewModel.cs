using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Places.Styles;
using Lumora.Services.Library;
using Lumora.Services.Messaging;
using Lumora.Services.Places;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Lumora.Shell;

namespace Lumora.Places.TrackSettings;

/// <summary>Auswahl „Zeitzone der Fotos“ (null = Windows-Zeitzone mit Sommerzeit je Datum).</summary>
public sealed record TimeZoneOption(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Auswahl „Toleranz“ in Minuten.</summary>
public sealed record ToleranceOption(int Minutes)
{
    public string Label => Minutes switch
    {
        0 => "keine",
        1 => "1 Minute",
        _ => $"{Minutes} Minuten",
    };

    public override string ToString() => Label;
}

/// <summary>Eine Aktivität in der Liste „Aktivitäten anzeigen“.</summary>
public sealed record GpsActivityRow(GpsTrackInfo Info)
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public string Date => Info.StartUtc.ToLocalTime().ToString("ddd, dd.MM.yyyy HH:mm", De);

    public string Duration
    {
        get
        {
            var d = Info.EndUtc - Info.StartUtc;
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";
        }
    }

    public string Title => Info.Title;

    public string Device => Info.Device ?? "—";

    public string Distance => GeoMath.FormatDistance(Info.DistanceMeters);

    public string Points => Info.PointCount.ToString("N0", De);
}

/// <summary>Kamera-Abweichung je Modell (Einstellungen › Orte › Zeitabgleich).</summary>
public sealed partial class CameraOffsetRow : ObservableObject
{
    private readonly Action<CameraOffsetRow> _changed;

    public CameraOffsetRow(CameraTimeInfo info, int offsetSeconds, Action<CameraOffsetRow> changed)
    {
        Info = info;
        _offsetSeconds = offsetSeconds;
        _offsetText = CameraOffsetEstimator.Format(offsetSeconds);
        _changed = changed;
    }

    public CameraTimeInfo Info { get; }

    public string Camera => Info.Camera;

    public string CountText => $"{Info.PhotoCount.ToString("N0", CultureInfo.GetCultureInfo("de-DE"))} Fotos";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuggestionText), nameof(CanApplySuggestion))]
    private int _offsetSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalid))]
    private string _offsetText;

    public bool IsInvalid { get; private set; }

    /// <summary>„passt“, „aus GPS-Zeit ermittelt: + 3 min 12 s“, „von Hand (keine GPS-Zeit)“.</summary>
    public string SuggestionText => Info.SuggestedOffsetSeconds switch
    {
        null => "von Hand (Kamera ohne GPS-Zeit)",
        var s when s == OffsetSeconds => s == 0 ? "passt (aus GPS-Zeit geprüft)" : "aus GPS-Zeit ermittelt",
        var s => $"aus GPS-Zeit ermittelt: {CameraOffsetEstimator.Format(s!.Value)}",
    };

    public bool CanApplySuggestion => Info.SuggestedOffsetSeconds is { } s && s != OffsetSeconds;

    partial void OnOffsetTextChanged(string value)
    {
        if (CameraOffsetEstimator.Parse(value) is { } seconds)
        {
            IsInvalid = false;
            if (seconds != OffsetSeconds)
            {
                OffsetSeconds = seconds;
                _changed(this);
            }
        }
        else
        {
            IsInvalid = true;
        }

        OnPropertyChanged(nameof(IsInvalid));
    }

    [RelayCommand]
    private void ApplySuggestion()
    {
        if (Info.SuggestedOffsetSeconds is { } s)
        {
            OffsetText = CameraOffsetEstimator.Format(s);
            OffsetSeconds = s;
            _changed(this);
        }
    }
}

/// <summary>
/// Einstellungen › Orte (ADR-0062, Mockup 10): „Garmin-Aktivitäten (GPS-Spuren der Uhr)“ — kein API-Schlüssel nötig bzw.
/// möglich, Datenexport öffnen, Export-ZIP und einzelne Dateien einlesen (Hintergrund mit Fortschritt und Abbruch), Ordner
/// beobachten, USB-Erkennung, Stand, Aktivitäten anzeigen, Spuren entfernen — und „Zeitabgleich“ (Zeitzone, Toleranz,
/// Kamera-Abweichungen, Vorrang). Gespeichert in der Settings-Tabelle des Katalogs.
/// </summary>
public sealed partial class PlacesSettingsViewModel : ObservableObject
{
    /// <summary>Seite „Daten exportieren“ im Garmin-Konto (öffnet im Standardbrowser).</summary>
    public const string GarminExportUrl = "https://www.garmin.com/account/datamanagement/exportdata/";

    private readonly IGpsTrackImportService _import;
    private readonly IGpsTrackStore _store;
    private readonly IGeoTrackSettingsStore _settingsStore;
    private readonly IGpsTrackAutoImport _autoImport;
    private readonly IGeoSuggestionService _suggestions;
    private readonly ILibraryScope _scope;
    private readonly IDialogService _dialogs;
    private readonly IGpsTrackFilePicker _picker;
    private readonly IMapLinkOpener _links;
    private readonly ILogger<PlacesSettingsViewModel> _logger;
    private GeoTrackSettings _settings = GeoTrackSettings.Default;
    private bool _loading;
    private int _groupId;
    private Task _lastSave = Task.CompletedTask;

    public PlacesSettingsViewModel(
        IGpsTrackImportService import,
        IGpsTrackStore store,
        IGeoTrackSettingsStore settingsStore,
        IGpsTrackAutoImport autoImport,
        IGeoSuggestionService suggestions,
        ILibraryScope scope,
        IDialogService dialogs,
        IGpsTrackFilePicker picker,
        IMapLinkOpener links,
        ILogger<PlacesSettingsViewModel> logger,
        IMessenger? messenger = null)
    {
        _import = import;
        _store = store;
        _settingsStore = settingsStore;
        _autoImport = autoImport;
        _suggestions = suggestions;
        _scope = scope;
        _dialogs = dialogs;
        _picker = picker;
        _links = links;
        _logger = logger;
        var m = messenger ?? WeakReferenceMessenger.Default;
        m.Register<PlacesSettingsViewModel, GpsTrackImportStatusMessage>(this, static (r, msg) => UiThread.Post(() => r.ApplyImportStatus(msg.Status)));
        m.Register<PlacesSettingsViewModel, GpsTracksChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.RefreshSummaryAsync(); }));
        m.Register<PlacesSettingsViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.LoadAsync(); }));

        TimeZones = BuildTimeZones();
        _selectedTimeZone = TimeZones[0];
        _selectedTolerance = ToleranceOptions.First(o => o.Minutes == GeoTrackSettings.DefaultToleranceSeconds / 60);
    }

    /// <summary>Zuletzt gestartetes Speichern (Tests).</summary>
    public Task LastSave => _lastSave;

    // ---- Garmin-Aktivitäten --------------------------------------------------------------------------------------------

    [ObservableProperty] private string _summaryText = "Noch keine Aktivitäten eingelesen.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTracksCommand), nameof(ToggleActivitiesCommand))]
    private int _activityCount;

    public bool HasActivities => ActivityCount > 0;

    partial void OnActivityCountChanged(int value) => OnPropertyChanged(nameof(HasActivities));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ImportExportZipCommand), nameof(ImportFilesCommand), nameof(CancelImportCommand), nameof(RemoveTracksCommand))]
    private bool _isImporting;

    public bool IsIdle => !IsImporting;

    [ObservableProperty] private string _importProgressText = string.Empty;

    [ObservableProperty] private double _importProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportResult))]
    private string? _importResultText;

    public bool HasImportResult => !string.IsNullOrEmpty(ImportResultText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWatchFolder), nameof(WatchFolderText))]
    private string? _watchFolder;

    public bool HasWatchFolder => !string.IsNullOrWhiteSpace(WatchFolder);

    public string WatchFolderText => WatchFolder ?? "Kein Ordner gewählt";

    [ObservableProperty] private bool _usbDetection = true;

    [ObservableProperty] private bool _isActivitiesOpen;

    public ObservableCollection<GpsActivityRow> Activities { get; } = new();

    // ---- Zeitabgleich --------------------------------------------------------------------------------------------------

    public IReadOnlyList<TimeZoneOption> TimeZones { get; }

    [ObservableProperty] private TimeZoneOption _selectedTimeZone;

    public IReadOnlyList<ToleranceOption> ToleranceOptions { get; } = [new(0), new(1), new(2), new(3), new(5), new(10), new(15), new(30), new(60)];

    [ObservableProperty] private ToleranceOption _selectedTolerance;

    public ObservableCollection<CameraOffsetRow> Cameras { get; } = new();

    public bool HasCameras => Cameras.Count > 0;

    // ---- Laden und Speichern -------------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _settings = await _settingsStore.LoadAsync().ConfigureAwait(true);
            SelectedTimeZone = TimeZones.FirstOrDefault(z => z.Id == _settings.TimeZoneId) ?? TimeZones[0];
            SelectedTolerance = ToleranceOptions.MinBy(o => Math.Abs((o.Minutes * 60) - _settings.ToleranceSeconds)) ?? ToleranceOptions[2];
            WatchFolder = _settings.WatchFolder;
            UsbDetection = _settings.UsbDetection;
            var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
            _groupId = scope.GroupId;
            var cameras = await _suggestions.GetCamerasAsync(scope).ConfigureAwait(true);
            Cameras.Clear();
            foreach (var camera in cameras)
            {
                Cameras.Add(new CameraOffsetRow(camera, _settings.OffsetFor(camera.Camera), OnCameraChanged));
            }

            OnPropertyChanged(nameof(HasCameras));
            await RefreshSummaryAsync().ConfigureAwait(true);
            ApplyImportStatus(_import.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Orte nicht geladen");
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshSummaryAsync()
    {
        try
        {
            var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
            _groupId = scope.GroupId;
            var summary = await _store.GetSummaryAsync(scope.GroupId).ConfigureAwait(true);
            ActivityCount = summary.Count;
            SummaryText = GpsTrackStore.Describe(summary);
            if (IsActivitiesOpen)
            {
                await LoadActivitiesAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Einstellungen › Orte: Stand der Spuren nicht gelesen");
        }
    }

    partial void OnSelectedTimeZoneChanged(TimeZoneOption value) => SaveLater();

    partial void OnSelectedToleranceChanged(ToleranceOption value) => SaveLater();

    partial void OnUsbDetectionChanged(bool value) => SaveLater(reconfigure: true);

    partial void OnWatchFolderChanged(string? value) => SaveLater(reconfigure: true);

    private void OnCameraChanged(CameraOffsetRow row) => SaveLater();

    private void SaveLater(bool reconfigure = false)
    {
        if (_loading)
        {
            return;
        }

        _lastSave = SaveAsync(reconfigure);
    }

    private async Task SaveAsync(bool reconfigure)
    {
        try
        {
            var offsets = new Dictionary<string, int>(_settings.CameraOffsets, StringComparer.Ordinal);
            foreach (var camera in Cameras)
            {
                offsets[camera.Camera] = camera.OffsetSeconds;
            }

            var current = await _settingsStore.LoadAsync().ConfigureAwait(true); // „Speichern“ der Orte-Seite nicht überschreiben
            _settings = current with
            {
                TimeZoneId = SelectedTimeZone.Id,
                ToleranceSeconds = SelectedTolerance.Minutes * 60,
                CameraOffsets = offsets,
                WatchFolder = string.IsNullOrWhiteSpace(WatchFolder) ? null : WatchFolder,
                UsbDetection = UsbDetection,
            };
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
            if (reconfigure)
            {
                await _autoImport.ReconfigureAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einstellungen › Orte nicht gespeichert");
        }
    }

    // ---- Befehle -------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenGarminExport() => _links.Open(new Uri(GarminExportUrl));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ImportExportZipAsync()
    {
        var zip = _picker.PickExportZip();
        return zip is null ? Task.CompletedTask : ImportAsync([zip]);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ImportFilesAsync()
    {
        var files = _picker.PickTrackFiles();
        return files.Count == 0 ? Task.CompletedTask : ImportAsync(files);
    }

    /// <summary>Einlesen im Hintergrund (Tests und Rauchtest rufen das direkt).</summary>
    public async Task<GpsTrackImportReport> ImportAsync(IReadOnlyList<string> paths)
    {
        ImportResultText = null;
        IsImporting = true;
        try
        {
            var report = await Task.Run(() => _import.ImportAsync(paths)).ConfigureAwait(true);
            ImportResultText = report.Text;
            return report;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPS-Spuren nicht eingelesen");
            ImportResultText = "Einlesen fehlgeschlagen: " + ex.Message;
            return new GpsTrackImportReport(0, 0, 0, 0, 0, false, ImportResultText);
        }
        finally
        {
            IsImporting = false;
            await RefreshSummaryAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsImporting))]
    private void CancelImport() => _import.Cancel();

    private void ApplyImportStatus(GpsTrackImportStatus status)
    {
        if (status.IsRunning)
        {
            IsImporting = true;
            ImportProgress = status.FilesFound == 0 ? 0 : Math.Clamp(100.0 * status.FilesRead / status.FilesFound, 0, 100);
            ImportProgressText = $"{status.FilesRead:N0} von {status.FilesFound:N0} Dateien gelesen · {status.Imported:N0} neu · {status.Duplicates:N0} schon vorhanden";
        }
        else
        {
            ImportProgressText = string.Empty;
            ImportProgress = 0;
        }
    }

    [RelayCommand]
    private void ChooseWatchFolder()
    {
        if (_dialogs.PickFolder("Ordner mit FIT-/GPX-Dateien beobachten") is { } folder)
        {
            WatchFolder = folder;
            _ = ScanWatchFolderAsync();
        }
    }

    private async Task ScanWatchFolderAsync()
    {
        await _lastSave.ConfigureAwait(true);
        try
        {
            if (await _autoImport.ScanWatchFolderAsync().ConfigureAwait(true) is { } report)
            {
                ImportResultText = "Beobachteter Ordner: " + report.Text;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPS-Spuren: beobachteter Ordner nicht eingelesen");
        }

        await RefreshSummaryAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearWatchFolder() => WatchFolder = null;

    [RelayCommand(CanExecute = nameof(HasActivities))]
    private async Task ToggleActivitiesAsync()
    {
        IsActivitiesOpen = !IsActivitiesOpen;
        if (IsActivitiesOpen)
        {
            await LoadActivitiesAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadActivitiesAsync()
    {
        var list = await _store.ListAsync(_groupId).ConfigureAwait(true);
        Activities.Clear();
        foreach (var info in list)
        {
            Activities.Add(new GpsActivityRow(info));
        }
    }

    private bool CanRemoveTracks() => HasActivities && IsIdle;

    [RelayCommand(CanExecute = nameof(CanRemoveTracks))]
    private async Task RemoveTracksAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "GPS-Spuren entfernen",
            $"{ActivityCount:N0} Aktivitäten dieser Bibliothek (bzw. Gruppe) aus dem Katalog entfernen? Bereits übernommene Orte bleiben, neue Vorschläge aus Spuren gibt es danach erst nach erneutem Einlesen. Die Dateien bei Garmin bzw. auf der Platte bleiben unberührt.",
            "Entfernen",
            isDestructive: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        var removed = await _store.DeleteAllAsync(_groupId).ConfigureAwait(true);
        _logger.LogInformation("GPS-Spuren entfernt: {Count}", removed);
        ImportResultText = $"{removed:N0} Aktivitäten entfernt.";
        Activities.Clear();
        IsActivitiesOpen = false;
        WeakReferenceMessenger.Default.Send(new GpsTracksChangedMessage(_groupId));
        await RefreshSummaryAsync().ConfigureAwait(true);
    }

    private static IReadOnlyList<TimeZoneOption> BuildTimeZones()
    {
        var list = new List<TimeZoneOption> { new(null, $"Automatisch (Windows: {TimeZoneInfo.Local.DisplayName}, Sommerzeit je Datum)") };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            list.Add(new TimeZoneOption(zone.Id, zone.DisplayName));
        }

        return list;
    }
}

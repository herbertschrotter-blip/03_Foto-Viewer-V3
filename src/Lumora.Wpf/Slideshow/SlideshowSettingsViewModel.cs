using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Lumora.Slideshow;

/// <summary>Auswahl-Eintrag (Wert + Beschriftung) für die Diashow-Einstellungen.</summary>
public sealed record SlideshowOption<T>(T Value, string Label);

/// <summary>
/// Einstellungen › Darstellung › Diashow (ADR-0059; die Story-Seite verschiebt den Abschnitt später nach „Storys“):
/// Anzeigedauer, langsamer Zoom, Videos (an/aus, Höchstdauer), Datum und Ort, Reihenfolge. Jede Änderung wird sofort
/// gespeichert (<c>slideshow.*</c>); eine laufende Diashow übernimmt sie beim nächsten Start.
/// </summary>
public sealed partial class SlideshowSettingsViewModel : ObservableObject
{
    private readonly ISlideshowSettingsStore _store;
    private readonly ILogger<SlideshowSettingsViewModel> _logger;
    private bool _loading;

    public SlideshowSettingsViewModel(ISlideshowSettingsStore store, ILogger<SlideshowSettingsViewModel> logger)
    {
        _store = store;
        _logger = logger;
        _selectedDuration = DurationOptions.First(o => o.Value == SlideshowSettings.Default.SlideSeconds);
        _selectedMaxVideo = MaxVideoOptions.First(o => o.Value == SlideshowSettings.Default.MaxVideoSeconds);
        _selectedOrder = OrderOptions[0];
    }

    public IReadOnlyList<SlideshowOption<int>> DurationOptions { get; } =
        SlideshowSettings.DurationOptions.Select(s => new SlideshowOption<int>(s, $"{s} Sekunden")).ToList();

    public IReadOnlyList<SlideshowOption<int>> MaxVideoOptions { get; } =
    [
        new(10, "10 Sekunden"),
        new(30, "30 Sekunden"),
        new(60, "1 Minute"),
        new(120, "2 Minuten"),
        new(600, "ganz (bis 10 Minuten)"),
    ];

    public IReadOnlyList<SlideshowOption<SlideshowOrder>> OrderOptions { get; } =
    [
        new(SlideshowOrder.AsView, "Wie die Ansicht"),
        new(SlideshowOrder.Random, "Zufällig"),
    ];

    [ObservableProperty] private SlideshowOption<int> _selectedDuration;
    [ObservableProperty] private bool _kenBurns = true;
    [ObservableProperty] private bool _playVideos = true;
    [ObservableProperty] private SlideshowOption<int> _selectedMaxVideo;
    [ObservableProperty] private bool _showCaption = true;
    [ObservableProperty] private SlideshowOption<SlideshowOrder> _selectedOrder;
    [ObservableProperty] private string? _savedText;

    /// <summary>Gespeicherte Werte laden (beim Anzeigen des Reiters).</summary>
    public async Task LoadAsync()
    {
        var settings = await _store.LoadAsync().ConfigureAwait(true);
        _loading = true;
        try
        {
            SelectedDuration = DurationOptions.FirstOrDefault(o => o.Value == settings.SlideSeconds) ?? DurationOptions[1];
            KenBurns = settings.KenBurns;
            PlayVideos = settings.PlayVideos;
            SelectedMaxVideo = MaxVideoOptions.FirstOrDefault(o => o.Value == settings.MaxVideoSeconds)
                ?? MaxVideoOptions.OrderBy(o => Math.Abs(o.Value - settings.MaxVideoSeconds)).First();
            ShowCaption = settings.ShowCaption;
            SelectedOrder = OrderOptions.FirstOrDefault(o => o.Value == settings.Order) ?? OrderOptions[0];
            SavedText = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Aktueller Stand als Einstellungen.</summary>
    public SlideshowSettings ToSettings() => new SlideshowSettings
    {
        SlideSeconds = SelectedDuration.Value,
        KenBurns = KenBurns,
        PlayVideos = PlayVideos,
        MaxVideoSeconds = SelectedMaxVideo.Value,
        ShowCaption = ShowCaption,
        Order = SelectedOrder.Value,
    }.Normalized();

    partial void OnSelectedDurationChanged(SlideshowOption<int> value) => Save();

    partial void OnKenBurnsChanged(bool value) => Save();

    partial void OnPlayVideosChanged(bool value) => Save();

    partial void OnSelectedMaxVideoChanged(SlideshowOption<int> value) => Save();

    partial void OnShowCaptionChanged(bool value) => Save();

    partial void OnSelectedOrderChanged(SlideshowOption<SlideshowOrder> value) => Save();

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        _ = SaveAsync();
    }

    /// <summary>Speichert den aktuellen Stand (öffentlich für Tests).</summary>
    public async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(ToSettings()).ConfigureAwait(true);
            SavedText = "Gespeichert — gilt ab der nächsten Diashow.";
        }
        catch (Exception ex)
        {
            SavedText = $"Nicht gespeichert: {ex.Message}";
            _logger.LogWarning(ex, "Diashow-Einstellungen konnten nicht gespeichert werden");
        }
    }
}

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.ListView;

namespace Lumora.Slideshow;

/// <summary>Reihenfolge der Diashow.</summary>
public enum SlideshowOrder
{
    /// <summary>Wie die Ansicht (Galerie-Sortierung, Album-Reihenfolge, Lightbox-Sequenz).</summary>
    AsView = 0,

    /// <summary>Zufällig; das Startbild kommt zuerst.</summary>
    Random = 1,
}

/// <summary>Einstellungen der Diashow (<c>slideshow.*</c> in der Settings-Tabelle des Katalogs, ADR-0059).</summary>
public sealed record SlideshowSettings
{
    /// <summary>Erlaubte Anzeigedauern je Foto in Sekunden (Leiste und Einstellungen).</summary>
    public static readonly int[] DurationOptions = [3, 5, 8];

    public static readonly SlideshowSettings Default = new();

    /// <summary>Anzeigedauer je Foto (3, 5 oder 8 s).</summary>
    public int SlideSeconds { get; init; } = 5;

    /// <summary>Langsamer Zoom (Ken Burns) auf Gesichter bzw. Bildmitte.</summary>
    public bool KenBurns { get; init; } = true;

    /// <summary>Videos abspielen (mit Ton); aus = Videos überspringen.</summary>
    public bool PlayVideos { get; init; } = true;

    /// <summary>Höchstdauer je Video in Sekunden, danach weiter.</summary>
    public int MaxVideoSeconds { get; init; } = 30;

    /// <summary>Datum und Ort unten links einblenden.</summary>
    public bool ShowCaption { get; init; } = true;

    public SlideshowOrder Order { get; init; } = SlideshowOrder.AsView;

    /// <summary>Werte in gültige Bereiche bringen (Dauer auf die nächste Option, Video 5–600 s).</summary>
    public SlideshowSettings Normalized() => this with
    {
        SlideSeconds = NearestDuration(SlideSeconds),
        MaxVideoSeconds = Math.Clamp(MaxVideoSeconds, 5, 600),
        Order = Enum.IsDefined(Order) ? Order : SlideshowOrder.AsView,
    };

    public static int NearestDuration(int seconds)
    {
        var best = DurationOptions[0];
        foreach (var option in DurationOptions)
        {
            if (Math.Abs(option - seconds) < Math.Abs(best - seconds))
            {
                best = option;
            }
        }

        return best;
    }
}

/// <summary>Liest und schreibt die Diashow-Einstellungen.</summary>
public interface ISlideshowSettingsStore
{
    Task<SlideshowSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(SlideshowSettings settings, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ISlideshowSettingsStore"/> über die Key-Value-Settings-Tabelle (wie die Galerie-Ansicht). Fehlende oder
/// unlesbare Werte ergeben die Standardwerte; eine versteckte Bibliothek hat eigene Einstellungen (eigener Katalog).
/// </summary>
public sealed class SlideshowSettingsStore : ISlideshowSettingsStore
{
    public const string SlideSecondsKey = "slideshow.slideSeconds";
    public const string KenBurnsKey = "slideshow.kenBurns";
    public const string PlayVideosKey = "slideshow.playVideos";
    public const string MaxVideoSecondsKey = "slideshow.maxVideoSeconds";
    public const string ShowCaptionKey = "slideshow.showCaption";
    public const string OrderKey = "slideshow.order";

    private readonly IGalleryViewSettingsStore _store;
    private readonly ILogger<SlideshowSettingsStore> _logger;

    public SlideshowSettingsStore(IGalleryViewSettingsStore store, ILogger<SlideshowSettingsStore> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<SlideshowSettings> LoadAsync(CancellationToken ct = default)
    {
        var d = SlideshowSettings.Default;
        try
        {
            return new SlideshowSettings
            {
                SlideSeconds = ParseInt(await _store.GetAsync(SlideSecondsKey, ct).ConfigureAwait(false), d.SlideSeconds),
                KenBurns = ParseBool(await _store.GetAsync(KenBurnsKey, ct).ConfigureAwait(false), d.KenBurns),
                PlayVideos = ParseBool(await _store.GetAsync(PlayVideosKey, ct).ConfigureAwait(false), d.PlayVideos),
                MaxVideoSeconds = ParseInt(await _store.GetAsync(MaxVideoSecondsKey, ct).ConfigureAwait(false), d.MaxVideoSeconds),
                ShowCaption = ParseBool(await _store.GetAsync(ShowCaptionKey, ct).ConfigureAwait(false), d.ShowCaption),
                Order = Enum.TryParse<SlideshowOrder>(await _store.GetAsync(OrderKey, ct).ConfigureAwait(false), ignoreCase: true, out var order) ? order : d.Order,
            }.Normalized();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Diashow-Einstellungen nicht lesbar — Standardwerte");
            return d;
        }
    }

    public async Task SaveAsync(SlideshowSettings settings, CancellationToken ct = default)
    {
        var s = settings.Normalized();
        await _store.SetAsync(SlideSecondsKey, s.SlideSeconds.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _store.SetAsync(KenBurnsKey, s.KenBurns ? "true" : "false", ct).ConfigureAwait(false);
        await _store.SetAsync(PlayVideosKey, s.PlayVideos ? "true" : "false", ct).ConfigureAwait(false);
        await _store.SetAsync(MaxVideoSecondsKey, s.MaxVideoSeconds.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _store.SetAsync(ShowCaptionKey, s.ShowCaption ? "true" : "false", ct).ConfigureAwait(false);
        await _store.SetAsync(OrderKey, s.Order.ToString(), ct).ConfigureAwait(false);
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool ParseBool(string? text, bool fallback)
        => bool.TryParse(text, out var value) ? value : fallback;
}

using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>
/// Eine Datei auf der Zeitachse der Story-Erkennung (ADR-0058).
/// </summary>
/// <param name="FileId">Files.Id.</param>
/// <param name="Time">Aufnahme <c>COALESCE(CapturedAt, ModifiedAtUtc)</c> in Unix-Sekunden UTC (Pausen werden in echter Zeit gemessen).</param>
/// <param name="Day">Kalendertag in Ortszeit der Aufnahme als Tageszahl seit 1970-01-01 (Tagesabschnitte, Überschriften).</param>
/// <param name="Precision">Genauigkeit des Aufnahmedatums; Monat/Jahr bilden eigene Gruppen statt Ereignisse.</param>
/// <param name="IsVideo">Video (zählt zu <c>VideoCount</c>).</param>
public readonly record struct StoryTimelineItem(
    int FileId,
    long Time,
    int Day,
    CaptureDatePrecision Precision,
    double? Latitude,
    double? Longitude,
    int? PlaceId,
    bool IsVideo)
{
    public bool HasGeo => Latitude is not null && Longitude is not null;

    /// <summary>Ungefähres Datum (nur Monat bzw. Jahr bekannt).</summary>
    public bool IsApproximate => Precision is CaptureDatePrecision.Month or CaptureDatePrecision.Year;
}

/// <summary>„Zu Hause“ der Reise-Erkennung: Koordinaten und (falls bekannt) der Ort.</summary>
public sealed record StoryHome(double Latitude, double Longitude, int? PlaceId);

/// <summary>Schwellen der Erkennung (aus <see cref="StorySettings"/>, in Tests direkt gesetzt).</summary>
public sealed record StoryDetectionOptions
{
    /// <summary>Neues Ereignis nach einer Pause von mehr als …</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Mindestzahl Fotos/Videos eines Ereignisses, einer Reise und einer Orte-Story.</summary>
    public int MinFiles { get; init; } = 12;

    /// <summary>Neues Ereignis, sobald ein Foto weiter als das vom vorigen verorteten entfernt aufgenommen wurde.</summary>
    public double SplitDistanceKm { get; init; } = 30;

    /// <summary>Weiter als das vom Zuhause entfernt = „weg“ (Reise bzw. Ausflug); auch Orte näher als das sind keine Orte-Story.</summary>
    public double HomeRadiusKm { get; init; } = 30;

    /// <summary>Höchste Lücke zwischen zwei Ereignissen derselben Reise.</summary>
    public TimeSpan TripGap { get; init; } = TimeSpan.FromHours(48);

    /// <summary>Mindestzahl Fotos mit bestätigtem Gesicht je Personen-Story.</summary>
    public int MinPersonFiles { get; init; } = 30;

    /// <summary>Mindestzahl verschiedener Jahre mit Besuchen je Orte-Story.</summary>
    public int MinPlaceYears { get; init; } = 3;

    /// <summary>Ein Ort zählt zu den Orten eines Ereignisses ab diesem Anteil der verorteten Fotos (mindestens 2 Fotos).</summary>
    public double MinPlaceShare { get; init; } = 0.03;

    /// <summary>Größte Lücke (in Sekunden), die ein Ereignis bzw. eine Reise überbrücken kann — Grenze der inkrementellen Fenster.</summary>
    public long MaxBridgeSeconds => (long)Math.Max(Pause.TotalSeconds, TripGap.TotalSeconds);

    public static StoryDetectionOptions From(StorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new StoryDetectionOptions
        {
            Pause = TimeSpan.FromHours(settings.PauseHours),
            MinFiles = settings.MinFiles,
            MinPersonFiles = settings.MinPersonFiles,
        };
    }
}

/// <summary>Eine erkannte Story (noch ohne Id) — Ergebnis von <see cref="StoryDetector"/>.</summary>
public sealed class DetectedStory
{
    public required StoryKind Kind { get; init; }

    public required string Signature { get; init; }

    public required IReadOnlyList<StoryTimelineItem> Items { get; init; }

    public CaptureDatePrecision Precision { get; init; }

    public int? PersonId { get; init; }

    public int? PlaceId { get; init; }

    /// <summary>Orte in Reihenfolge des ersten Auftretens (nur Orte mit nennenswertem Anteil).</summary>
    public IReadOnlyList<int> PlaceIds { get; init; } = [];

    public int? VisitCount { get; init; }

    public bool IsAway { get; init; }

    public long StartAt => Bounds.StartAt;

    public long EndAt => Bounds.EndAt;

    public int FirstDay => Bounds.FirstDay;

    public int LastDay => Bounds.LastDay;

    public int VideoCount => Bounds.Videos;

    private (long StartAt, long EndAt, int FirstDay, int LastDay, int Videos) Bounds
    {
        get
        {
            if (_bounds is { } cached)
            {
                return cached;
            }

            long start = long.MaxValue, end = long.MinValue;
            int first = int.MaxValue, last = int.MinValue, videos = 0;
            foreach (var item in Items)
            {
                start = Math.Min(start, item.Time);
                end = Math.Max(end, item.Time);
                first = Math.Min(first, item.Day);
                last = Math.Max(last, item.Day);
                videos += item.IsVideo ? 1 : 0;
            }

            var bounds = Items.Count == 0 ? (0L, 0L, 0, 0, 0) : (start, end, first, last, videos);
            _bounds = bounds;
            return bounds;
        }
    }

    private (long, long, int, int, int)? _bounds;

    public override string ToString() => $"{Kind} {Signature} ({Items.Count})";
}

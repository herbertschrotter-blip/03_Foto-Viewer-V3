using System.Globalization;
using Lumora.Domain.Enums;

namespace Lumora.Services.Metadata;

/// <summary>
/// Aufnahmedatum einer Datei (ADR-0046): Zeitpunkt in UTC (Sekunden), Abstand der Ortszeit der Aufnahme zu UTC, Herkunft
/// und Genauigkeit. <see cref="Local"/> ist die Wanduhrzeit „wie fotografiert“ (Anzeige). Ist der Abstand in der Datei
/// nicht vermerkt (EXIF ohne <c>OffsetTimeOriginal</c>, Dateiname), gilt die Zeitzone dieses PCs zum Aufnahmedatum.
/// </summary>
public readonly record struct CaptureDateValue(DateTime Utc, int OffsetMinutes, CaptureDateSource Source, CaptureDatePrecision Precision)
{
    /// <summary>Kleinster plausibler Zeitpunkt (ältere Werte gelten als „nicht gesetzt“, z. B. QuickTime-Nullwerte 1904).</summary>
    public static readonly DateTime MinPlausible = new(1826, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Wanduhrzeit der Aufnahme (Kind = Unspecified).</summary>
    public DateTime Local => DateTime.SpecifyKind(Utc.AddMinutes(OffsetMinutes), DateTimeKind.Unspecified);

    /// <summary>
    /// Aus einer Wanduhrzeit: mit bekanntem Abstand (<paramref name="offsetMinutes"/>) direkt, sonst über die Zeitzone
    /// (<paramref name="zone"/>, Standard: dieser PC) zu diesem Datum — auch für Zeiten in der Sommerzeit-Lücke.
    /// Sekundenbruchteile entfallen (der Katalog speichert Sekunden).
    /// </summary>
    public static CaptureDateValue FromWallClock(
        DateTime wallClock,
        int? offsetMinutes,
        CaptureDateSource source,
        CaptureDatePrecision precision,
        TimeZoneInfo? zone = null)
    {
        var wall = DateTime.SpecifyKind(TruncateToSeconds(wallClock), DateTimeKind.Unspecified);
        var offset = offsetMinutes ?? (int)Math.Round((zone ?? TimeZoneInfo.Local).GetUtcOffset(wall).TotalMinutes);
        var utc = DateTime.SpecifyKind(wall.AddMinutes(-offset), DateTimeKind.Utc);
        return new CaptureDateValue(utc, offset, source, precision);
    }

    /// <summary>Aus einem UTC-Zeitpunkt (Video-Container): Anzeige in der Zeitzone dieses PCs zu diesem Zeitpunkt.</summary>
    public static CaptureDateValue FromUtc(DateTime utc, CaptureDateSource source, TimeZoneInfo? zone = null)
    {
        var u = DateTime.SpecifyKind(TruncateToSeconds(utc), DateTimeKind.Utc);
        var offset = (int)Math.Round((zone ?? TimeZoneInfo.Local).GetUtcOffset(u).TotalMinutes);
        return new CaptureDateValue(u, offset, source, CaptureDatePrecision.Exact);
    }

    /// <summary>Plausibel: nicht vor <see cref="MinPlausible"/> und höchstens einen Tag in der Zukunft.</summary>
    public static bool IsPlausible(DateTime utc) => utc >= MinPlausible && utc <= DateTime.UtcNow.AddDays(1);

    private static DateTime TruncateToSeconds(DateTime value) => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Local:yyyy-MM-dd HH:mm:ss} ({OffsetMinutes:+0;-0} min, {Source}, {Precision})");
}

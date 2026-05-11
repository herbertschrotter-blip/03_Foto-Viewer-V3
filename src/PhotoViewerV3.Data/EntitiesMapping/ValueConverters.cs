using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhotoViewerV3.Data.EntitiesMapping;

/// <summary>
/// Zentrale ValueConverter: DateTime ↔ Unix-Sekunden (INTEGER in SQLite), analog zum
/// Pattern aus Spike #1. Kompakter Index, schneller als ISO8601-String-Vergleiche.
/// </summary>
internal static class ValueConverters
{
    public static readonly ValueConverter<DateTime, long> DateTimeToUnixSeconds = new(
        dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        s => DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, long?> NullableDateTimeToUnixSeconds = new(
        dt => dt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds() : null,
        s => s.HasValue ? DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(s.Value).UtcDateTime, DateTimeKind.Utc) : null);
}

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Lumora.Domain.Entities;
using Lumora.Services.Database;

namespace Lumora.Services.Places.Tracks;

/// <summary>
/// Einstellungen „Orte“ (ADR-0062) in der Settings-Tabelle des Katalogs (je Katalog — eine versteckte Bibliothek hat
/// ihre eigenen): Zeitzone der Fotos (null = Windows-Zeitzone mit Sommerzeit je Datum), Toleranz vor/nach einer
/// Aktivität, Kamera-Abweichungen (Sekunden, + = Kamera geht vor), beobachteter Ordner, USB-Erkennung und die zuletzt
/// gewählte Speicherart der Vorschläge.
/// </summary>
public sealed record GeoTrackSettings(
    string? TimeZoneId,
    int ToleranceSeconds,
    IReadOnlyDictionary<string, int> CameraOffsets,
    string? WatchFolder,
    bool UsbDetection,
    bool SaveToFile)
{
    public const int DefaultToleranceSeconds = 120;
    public const int MaxToleranceSeconds = 60 * 60;

    public const string TimeZoneKey = "places.tracks.timeZone";
    public const string ToleranceKey = "places.tracks.toleranceSeconds";
    public const string CameraOffsetsKey = "places.tracks.cameraOffsets";
    public const string WatchFolderKey = "places.tracks.watchFolder";
    public const string UsbKey = "places.tracks.usb";
    public const string SaveToFileKey = "places.suggestions.saveToFile";

    /// <summary>Merker: Dateien im beobachteten Ordner bis zu diesem Zeitpunkt (UTC, Unix-Sekunden) gesehen.</summary>
    public const string WatchLastScanKey = "places.tracks.watchLastScan";

    /// <summary>Merker: Aktivitäten der Uhr bis zu diesem Zeitpunkt (UTC, Unix-Sekunden) eingelesen.</summary>
    public const string UsbLastImportKey = "places.tracks.usbLastImport";

    public static GeoTrackSettings Default { get; } = new(null, DefaultToleranceSeconds, new Dictionary<string, int>(), null, true, false);

    /// <summary>Zeitzone der Fotos ohne Zonenangabe: gewählte bzw. die dieses PCs (unbekannte Id → PC).</summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>Kamera-Abweichung in Sekunden (0 ohne Eintrag).</summary>
    public int OffsetFor(string? camera)
        => camera is not null && CameraOffsets.TryGetValue(camera, out var seconds) ? seconds : 0;
}

/// <summary>Lesen/Speichern von <see cref="GeoTrackSettings"/> und der Merker des Einlesens.</summary>
public interface IGeoTrackSettingsStore
{
    Task<GeoTrackSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(GeoTrackSettings settings, CancellationToken ct = default);

    Task<long?> GetMarkerAsync(string key, CancellationToken ct = default);

    Task SetMarkerAsync(string key, long value, CancellationToken ct = default);
}

/// <inheritdoc cref="IGeoTrackSettingsStore"/>
public sealed class GeoTrackSettingsStore(ILibraryContextFactory factory) : IGeoTrackSettingsStore
{
    private static readonly string[] Keys =
    [
        GeoTrackSettings.TimeZoneKey, GeoTrackSettings.ToleranceKey, GeoTrackSettings.CameraOffsetsKey, GeoTrackSettings.WatchFolderKey,
        GeoTrackSettings.UsbKey, GeoTrackSettings.SaveToFileKey,
    ];

    public async Task<GeoTrackSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var rows = await ctx.Settings.AsNoTracking().Where(s => Keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct).ConfigureAwait(false);
        var d = GeoTrackSettings.Default;
        var tolerance = rows.TryGetValue(GeoTrackSettings.ToleranceKey, out var t) && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 0, GeoTrackSettings.MaxToleranceSeconds)
            : d.ToleranceSeconds;
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rows.TryGetValue(GeoTrackSettings.CameraOffsetsKey, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                foreach (var (camera, value) in JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(camera) && value != 0)
                    {
                        offsets[camera] = value;
                    }
                }
            }
            catch (JsonException)
            {
                // kaputter Wert: ohne Abweichungen weiter
            }
        }

        return new GeoTrackSettings(
            rows.TryGetValue(GeoTrackSettings.TimeZoneKey, out var zone) && !string.IsNullOrWhiteSpace(zone) ? zone : null,
            tolerance,
            offsets,
            rows.TryGetValue(GeoTrackSettings.WatchFolderKey, out var folder) && !string.IsNullOrWhiteSpace(folder) ? folder : null,
            rows.TryGetValue(GeoTrackSettings.UsbKey, out var usb) && bool.TryParse(usb, out var u) ? u : d.UsbDetection,
            rows.TryGetValue(GeoTrackSettings.SaveToFileKey, out var save) && bool.TryParse(save, out var s) ? s : d.SaveToFile);
    }

    public async Task SaveAsync(GeoTrackSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, GeoTrackSettings.TimeZoneKey, settings.TimeZoneId ?? string.Empty, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, GeoTrackSettings.ToleranceKey, Math.Clamp(settings.ToleranceSeconds, 0, GeoTrackSettings.MaxToleranceSeconds).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        var offsets = settings.CameraOffsets.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        await UpsertAsync(ctx, GeoTrackSettings.CameraOffsetsKey, JsonSerializer.Serialize(offsets), ct).ConfigureAwait(false);
        await UpsertAsync(ctx, GeoTrackSettings.WatchFolderKey, settings.WatchFolder ?? string.Empty, ct).ConfigureAwait(false);
        await UpsertAsync(ctx, GeoTrackSettings.UsbKey, settings.UsbDetection ? "true" : "false", ct).ConfigureAwait(false);
        await UpsertAsync(ctx, GeoTrackSettings.SaveToFileKey, settings.SaveToFile ? "true" : "false", ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<long?> GetMarkerAsync(string key, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        var value = await ctx.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public async Task SetMarkerAsync(string key, long value, CancellationToken ct = default)
    {
        await using var ctx = factory.Create();
        await UpsertAsync(ctx, key, value.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(Data.LibraryContext ctx, string key, string value, CancellationToken ct)
    {
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}

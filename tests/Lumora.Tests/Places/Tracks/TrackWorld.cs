using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Services.Infrastructure;
using Lumora.Services.Metadata;
using Lumora.Services.Library;
using Lumora.Services.Places;
using Lumora.Services.Places.Accuracy;
using Lumora.Services.Places.Suggestions;
using Lumora.Services.Places.Tracks;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Places.Tracks;

/// <summary>Ablage außerhalb der DB für die Tests: Temp-Ordner des Katalogs (wie im Container einer versteckten Bibliothek).</summary>
internal sealed class TestStoragePaths(string root, bool hidden = false) : ICatalogStoragePaths
{
    public bool IsHidden { get; } = hidden;

    public string CacheRoot { get; } = Path.Combine(root, "cache");

    public string TempRoot { get; } = Path.Combine(root, "temp");
}

/// <summary>Bibliothek mit Diensten für Spuren, Genauigkeit und Vorschläge (ADR-0062).</summary>
internal sealed class TrackWorld : IDisposable
{
    public static readonly ReverseGeocoder Geocoder = new();

    private TrackWorld(TempLibraryFixture fx, Library library)
    {
        Fx = fx;
        Library = library;
        Storage = new TestStoragePaths(Path.Combine(fx.RootDir, "katalog"));
        Scope = new LibraryScope(fx.ContextFactory, null, fx.Messenger);
        Store = new GpsTrackStore(fx.ContextFactory);
        Settings = new GeoTrackSettingsStore(fx.ContextFactory);
        Import = new GpsTrackImportService(Store, Scope, Storage, NullLogger<GpsTrackImportService>.Instance, fx.Messenger);
        Geo = new GeoLocationService(fx.ContextFactory, new GeoLocationReader(), Geocoder, NullLogger<GeoLocationService>.Instance, messenger: fx.Messenger)
        {
            StartDelay = Timeout.InfiniteTimeSpan,
        };
        Accuracy = new GeoAccuracyService(fx.ContextFactory, NullLogger<GeoAccuracyService>.Instance, messenger: fx.Messenger) { StartDelay = Timeout.InfiniteTimeSpan };
        Suggestions = new GeoSuggestionService(fx.ContextFactory, Store, Settings, Geo, Scope, NullLogger<GeoSuggestionService>.Instance, fx.Messenger);
    }

    public TempLibraryFixture Fx { get; }

    public Library Library { get; }

    public TestStoragePaths Storage { get; }

    public LibraryScope Scope { get; }

    public GpsTrackStore Store { get; }

    public GeoTrackSettingsStore Settings { get; }

    public GpsTrackImportService Import { get; }

    public GeoLocationService Geo { get; }

    public GeoAccuracyService Accuracy { get; }

    public GeoSuggestionService Suggestions { get; }

    public static async Task<TrackWorld> CreateAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        return new TrackWorld(fx, library);
    }

    public async Task<int> FileIdAsync(string name) => (await Fx.FindFileAsync(name))!.Id;

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>Aufnahme (Wanduhrzeit, Abstand in Minuten wie das Nachtragen ihn speichert) und Ort per SQL setzen.</summary>
    public void SetPhoto(int fileId, DateTime wall, int offsetMinutes, double? lat, double? lon, int geoSource, int? accuracy, int? reason = null, DateTime? gpsUtc = null)
    {
        var utc = new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Utc)).ToUnixTimeSeconds() - (offsetMinutes * 60L);
        Execute("""
            UPDATE Files SET CapturedAt = $at, CapturedAtSource = 1, CapturedAtPrecision = 0, CapturedAtOffset = $off,
                             Latitude = $lat, Longitude = $lon, GeoSource = $src, GeoAccuracy = $acc, GeoAccuracyReason = $reason,
                             GpsTimeUtc = $gps, ExifTimeOffset = $off
            WHERE Id = $id;
            """,
            ("$at", utc), ("$off", offsetMinutes), ("$lat", (object?)lat ?? DBNull.Value), ("$lon", (object?)lon ?? DBNull.Value),
            ("$src", geoSource), ("$acc", (object?)accuracy ?? DBNull.Value), ("$reason", (object?)reason ?? DBNull.Value),
            ("$gps", gpsUtc is { } g ? new DateTimeOffset(DateTime.SpecifyKind(g, DateTimeKind.Utc)).ToUnixTimeSeconds() : DBNull.Value),
            ("$id", fileId));
    }

    public async Task<int> GroupAsync() => (await Scope.GetCurrentAsync()).GroupId;

    public void Dispose()
    {
        Geo.Dispose();
        Accuracy.Dispose();
        Fx.Dispose();
    }
}

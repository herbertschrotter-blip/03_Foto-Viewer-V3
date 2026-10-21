using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Lumora.Services.Database;
using Lumora.Services.Messaging;

namespace Lumora.Services.Stories;

/// <summary>Wo „zu Hause“ ist: automatisch (häufigster Ort), ein bestimmter Ort oder Koordinaten.</summary>
/// <param name="PlaceId">Gewählter Ort (GeoNames-Id); mit <see cref="Latitude"/>/<see cref="Longitude"/> genau dort.</param>
public sealed record StoryHomeSetting(int? PlaceId = null, double? Latitude = null, double? Longitude = null)
{
    public static StoryHomeSetting Automatic { get; } = new();

    public bool IsAutomatic => PlaceId is null && (Latitude is null || Longitude is null);

    /// <summary>Wert in der Settings-Tabelle: „auto“, „place:&lt;id&gt;“, „geo:&lt;lat&gt;,&lt;lon&gt;“ bzw. „place:&lt;id&gt;@&lt;lat&gt;,&lt;lon&gt;“.</summary>
    public string ToSettingValue()
    {
        var geo = Latitude is { } lat && Longitude is { } lon
            ? lat.ToString("R", CultureInfo.InvariantCulture) + "," + lon.ToString("R", CultureInfo.InvariantCulture)
            : null;
        return (PlaceId, geo) switch
        {
            (null, null) => "auto",
            ({ } p, null) => "place:" + p.ToString(CultureInfo.InvariantCulture),
            (null, { } g) => "geo:" + g,
            ({ } p, { } g) => "place:" + p.ToString(CultureInfo.InvariantCulture) + "@" + g,
        };
    }

    public static StoryHomeSetting Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "auto")
        {
            return Automatic;
        }

        int? place = null;
        string? geo = null;
        if (value.StartsWith("place:", StringComparison.Ordinal))
        {
            var rest = value["place:".Length..];
            var at = rest.IndexOf('@', StringComparison.Ordinal);
            if (int.TryParse(at < 0 ? rest : rest[..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                place = p;
            }

            geo = at < 0 ? null : rest[(at + 1)..];
        }
        else if (value.StartsWith("geo:", StringComparison.Ordinal))
        {
            geo = value["geo:".Length..];
        }

        double? lat = null, lon = null;
        if (geo?.Split(',') is [var a, var b]
            && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
        {
            lat = la;
            lon = lo;
        }

        return new StoryHomeSetting(place, lat, lon);
    }
}

/// <summary>
/// Einstellungen › Storys (ADR-0058), Schlüssel <c>stories.*</c> in der Settings-Tabelle des Katalogs (je Katalog, also
/// auch eigen in einer versteckten Bibliothek).
/// </summary>
/// <param name="EventsEnabled">Ereignisse &amp; Reisen erkennen.</param>
/// <param name="MemoriesEnabled">Rückblicke anbieten.</param>
/// <param name="PersonsEnabled">Personen-Storys erkennen.</param>
/// <param name="PlacesEnabled">Orte-Storys erkennen.</param>
/// <param name="PauseHours">Neues Ereignis nach einer Pause von … Stunden.</param>
/// <param name="MinFiles">Mindestgröße einer Story (Fotos und Videos).</param>
/// <param name="Home">Zu Hause (Reisen, Orte-Storys).</param>
/// <param name="MinPersonFiles">Mindestzahl Fotos mit bestätigtem Gesicht je Personen-Story.</param>
/// <param name="HighlightShare">Anteil der Highlights (0..1).</param>
public sealed record StorySettings(
    bool EventsEnabled = true,
    bool MemoriesEnabled = true,
    bool PersonsEnabled = true,
    bool PlacesEnabled = true,
    int PauseHours = 6,
    int MinFiles = 12,
    StoryHomeSetting? Home = null,
    int MinPersonFiles = 30,
    double HighlightShare = 0.15)
{
    public const string EventsKey = "stories.events";
    public const string MemoriesKey = "stories.memories";
    public const string PersonsKey = "stories.persons";
    public const string PlacesKey = "stories.places";
    public const string PauseKey = "stories.pauseHours";
    public const string MinFilesKey = "stories.minFiles";
    public const string HomeKey = "stories.home";
    public const string MinPersonFilesKey = "stories.minPersonFiles";
    public const string HighlightShareKey = "stories.highlightShare";

    public static StorySettings Default { get; } = new();

    public StoryHomeSetting HomeOrAutomatic => Home ?? StoryHomeSetting.Automatic;

    /// <summary>Werte in sinnvolle Grenzen bringen (Pause 1–48 h, Mindestgröße 2–500, Person 5–1000, Anteil 2–60 %).</summary>
    public StorySettings Normalize() => this with
    {
        PauseHours = Math.Clamp(PauseHours, 1, 48),
        MinFiles = Math.Clamp(MinFiles, 2, 500),
        MinPersonFiles = Math.Clamp(MinPersonFiles, 5, 1000),
        HighlightShare = double.IsFinite(HighlightShare) ? Math.Clamp(HighlightShare, 0.02, 0.6) : 0.15,
        Home = Home ?? StoryHomeSetting.Automatic,
    };
}

/// <summary>Lesen und Speichern der Story-Einstellungen; Speichern sendet <see cref="StorySettingsChangedMessage"/> (neu berechnen).</summary>
public interface IStorySettingsStore
{
    Task<StorySettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(StorySettings settings, CancellationToken ct = default);
}

/// <inheritdoc cref="IStorySettingsStore"/>
public sealed class StorySettingsStore(ILibraryContextFactory factory, IMessenger? messenger = null) : IStorySettingsStore
{
    private static readonly string[] Keys =
    [
        StorySettings.EventsKey, StorySettings.MemoriesKey, StorySettings.PersonsKey, StorySettings.PlacesKey, StorySettings.PauseKey,
        StorySettings.MinFilesKey, StorySettings.HomeKey, StorySettings.MinPersonFilesKey, StorySettings.HighlightShareKey,
    ];

    private readonly IMessenger _messenger = messenger ?? WeakReferenceMessenger.Default;

    public Task<StorySettings> LoadAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            return Load(connection);
        }, ct);

    /// <summary>Liest die Einstellungen über eine offene Verbindung (Hintergrund-Index).</summary>
    internal static StorySettings Load(SqliteConnection connection)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Key, Value FROM Settings WHERE Key LIKE 'stories.%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        var d = StorySettings.Default;
        return new StorySettings(
            Bool(values, StorySettings.EventsKey, d.EventsEnabled),
            Bool(values, StorySettings.MemoriesKey, d.MemoriesEnabled),
            Bool(values, StorySettings.PersonsKey, d.PersonsEnabled),
            Bool(values, StorySettings.PlacesKey, d.PlacesEnabled),
            Int(values, StorySettings.PauseKey, d.PauseHours),
            Int(values, StorySettings.MinFilesKey, d.MinFiles),
            StoryHomeSetting.Parse(values.GetValueOrDefault(StorySettings.HomeKey)),
            Int(values, StorySettings.MinPersonFilesKey, d.MinPersonFiles),
            values.TryGetValue(StorySettings.HighlightShareKey, out var share)
                && double.TryParse(share, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : d.HighlightShare).Normalize();
    }

    public async Task SaveAsync(StorySettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var values = new Dictionary<string, string>
        {
            [StorySettings.EventsKey] = Flag(normalized.EventsEnabled),
            [StorySettings.MemoriesKey] = Flag(normalized.MemoriesEnabled),
            [StorySettings.PersonsKey] = Flag(normalized.PersonsEnabled),
            [StorySettings.PlacesKey] = Flag(normalized.PlacesEnabled),
            [StorySettings.PauseKey] = normalized.PauseHours.ToString(CultureInfo.InvariantCulture),
            [StorySettings.MinFilesKey] = normalized.MinFiles.ToString(CultureInfo.InvariantCulture),
            [StorySettings.HomeKey] = normalized.HomeOrAutomatic.ToSettingValue(),
            [StorySettings.MinPersonFilesKey] = normalized.MinPersonFiles.ToString(CultureInfo.InvariantCulture),
            [StorySettings.HighlightShareKey] = normalized.HighlightShare.ToString("R", CultureInfo.InvariantCulture),
        };
        await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            foreach (var key in Keys)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", values[key]);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }, ct).ConfigureAwait(false);
        _messenger.Send(new StorySettingsChangedMessage(normalized));
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static bool Bool(Dictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var text) && bool.TryParse(text, out var b) ? b : fallback;

    private static int Int(Dictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

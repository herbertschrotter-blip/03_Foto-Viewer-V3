using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Albums;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Services.Places;
using Lumora.Services.Stories;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Stories;

/// <summary>Synthetische Datei für einen Story-Katalog.</summary>
internal sealed record StoryFile(
    DateTime Utc,
    double? Lat = null,
    double? Lon = null,
    int? PlaceId = null,
    bool Video = false,
    CaptureDatePrecision Precision = CaptureDatePrecision.Exact,
    int? OffsetMinutes = null,
    bool Favorite = false);

/// <summary>Orte der Tests (ausgedachte Ids, echte Koordinaten).</summary>
internal static class TestPlaces
{
    public const int Graz = 9001;
    public const int Grado = 9002;
    public const int Triest = 9003;
    public const int Aquileia = 9004;
    public const int Wien = 9005;
    public const int Rovinj = 9006;

    public static readonly Dictionary<int, (double Lat, double Lon, string Name, string Country)> All = new()
    {
        [Graz] = (47.0707, 15.4395, "Graz", "Österreich"),
        [Grado] = (45.6779, 13.3953, "Grado", "Italien"),
        [Triest] = (45.6495, 13.7768, "Triest", "Italien"),
        [Aquileia] = (45.7690, 13.3700, "Aquileia", "Italien"),
        [Wien] = (48.2082, 16.3738, "Wien", "Österreich"),
        [Rovinj] = (45.0812, 13.6387, "Rovinj", "Kroatien"),
    };

    public static StoryFile At(int place, DateTime utc, bool video = false) => new(utc, All[place].Lat, All[place].Lon, place, video);
}

/// <summary>Ortsnamen ohne GeoNames-Daten (Tests).</summary>
internal sealed class FakePlaceLookup : IPlaceLookup
{
    public PlaceInfo? GetPlace(int placeId)
        => TestPlaces.All.TryGetValue(placeId, out var p) ? new PlaceInfo(placeId, p.Name, null, p.Country[..2].ToUpperInvariant(), p.Country) : null;

    public string CountryName(string countryCode) => countryCode;
}

/// <summary>
/// Story-Katalog für Tests (ADR-0058): eine Bibliothek, Dateien direkt per SQL mit Aufnahmedatum/Ort (synthetisch, ohne
/// echte Fotos), Erkennung, Story-Dienst, Alben. Uhr fest auf den 21.10.2026 in UTC.
/// </summary>
internal sealed class StoryWorld : IDisposable
{
    public static readonly DateOnly Today = new(2026, 10, 21);
    private int _nameCounter;

    private StoryWorld(TempLibraryFixture fx, int libraryId)
    {
        Fx = fx;
        LibraryId = libraryId;
        Catalog = new TestCatalogContext();
        Scope = new LibraryScope(fx.ContextFactory, Catalog, fx.Messenger);
        Clock = new FixedStoryClock(Today);
        Selector = new BasicHighlightSelector(fx.ContextFactory);
        Homes = new StoryHomeCache();
        Places = new FakePlaceLookup();
        Settings = new StorySettingsStore(fx.ContextFactory, fx.Messenger);
        Index = new StoryIndexService(fx.ContextFactory, NullLogger<StoryIndexService>.Instance, Selector, messenger: fx.Messenger, clock: Clock, homes: Homes);
        Albums = new AlbumService(fx.ContextFactory, NullLogger<AlbumService>.Instance, fx.Messenger, Scope, catalog: Catalog);
        Stories = new StoryService(fx.ContextFactory, NullLogger<StoryService>.Instance, Selector, fx.Messenger, Scope, Places, Albums, Index, Clock, Homes);
    }

    public TempLibraryFixture Fx { get; }

    public int LibraryId { get; }

    public TestCatalogContext Catalog { get; }

    public LibraryScope Scope { get; }

    public FixedStoryClock Clock { get; }

    public BasicHighlightSelector Selector { get; }

    public StoryHomeCache Homes { get; }

    public FakePlaceLookup Places { get; }

    public StorySettingsStore Settings { get; }

    public StoryIndexService Index { get; }

    public AlbumService Albums { get; }

    public StoryService Stories { get; }

    public static async Task<StoryWorld> CreateAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        var world = new StoryWorld(fx, library.Id);
        _ = await world.Scope.GetForLibraryAsync(library.Id); // eigene Gruppe anlegen
        return world;
    }

    public async Task<int> GroupIdAsync(int? libraryId = null) => (await Scope.GetForLibraryAsync(libraryId ?? LibraryId)).GroupId;

    /// <summary>Legt Dateien direkt im Katalog an (Aufnahmedatum gelesen, Quelle EXIF); liefert ihre Ids in Reihenfolge.</summary>
    public List<int> Add(IEnumerable<StoryFile> files, int? libraryId = null, string folder = "Fotos")
    {
        var lib = libraryId ?? LibraryId;
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var tx = connection.BeginTransaction();
        var folderPath = Path.Combine(Fx.RootDir, "lib" + lib.ToString(CultureInfo.InvariantCulture), folder);
        long folderId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id FROM Folders WHERE LibraryId = $l AND Path = $p;";
            cmd.Parameters.AddWithValue("$l", lib);
            cmd.Parameters.AddWithValue("$p", folderPath);
            folderId = cmd.ExecuteScalar() is long id ? id : 0;
        }

        if (folderId == 0)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Folders (LibraryId, Path, Name, ScannedAt) VALUES ($l, $p, $n, 0); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$l", lib);
            cmd.Parameters.AddWithValue("$p", folderPath);
            cmd.Parameters.AddWithValue("$n", folder);
            folderId = (long)cmd.ExecuteScalar()!;
        }

        var ids = new List<int>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Files (LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, Width, Height, IsFavorite, Missing,
                                   CapturedAt, CapturedAtSource, CapturedAtPrecision, CapturedAtOffset, Latitude, Longitude, GeoSource, PlaceId)
                VALUES ($l, $d, $n, $e, 1000, $t, $t, $m, 4000, 3000, $fav, 0, $t, 1, $prec, $off, $lat, $lon, $geo, $place);
                SELECT last_insert_rowid();
                """;
            foreach (var f in files)
            {
                cmd.Parameters.Clear();
                var video = f.Video;
                cmd.Parameters.AddWithValue("$l", lib);
                cmd.Parameters.AddWithValue("$d", folderId);
                cmd.Parameters.AddWithValue("$n", "f" + (++_nameCounter).ToString("D6", CultureInfo.InvariantCulture) + (video ? ".mp4" : ".jpg"));
                cmd.Parameters.AddWithValue("$e", video ? ".mp4" : ".jpg");
                cmd.Parameters.AddWithValue("$t", new DateTimeOffset(DateTime.SpecifyKind(f.Utc, DateTimeKind.Utc)).ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$m", video ? 1 : 0);
                cmd.Parameters.AddWithValue("$fav", f.Favorite);
                cmd.Parameters.AddWithValue("$prec", (int)f.Precision);
                cmd.Parameters.AddWithValue("$off", (object?)f.OffsetMinutes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lat", (object?)f.Lat ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lon", (object?)f.Lon ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$geo", f.Lat is null ? 0 : 1);
                cmd.Parameters.AddWithValue("$place", (object?)f.PlaceId ?? DBNull.Value);
                ids.Add(Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
            }
        }

        tx.Commit();
        return ids;
    }

    /// <summary><paramref name="count"/> Fotos ab <paramref name="start"/> im Abstand <paramref name="step"/> (Standard 5 min).</summary>
    public static IEnumerable<StoryFile> Burst(DateTime start, int count, int? place = null, TimeSpan? step = null, int videos = 0)
    {
        var s = step ?? TimeSpan.FromMinutes(5);
        for (var i = 0; i < count; i++)
        {
            var t = start + s * i;
            var video = i >= count - videos;
            yield return place is { } p ? TestPlaces.At(p, t, video) : new StoryFile(t, Video: video);
        }
    }

    /// <summary>Person der Gruppe mit bestätigten Gesichtern auf den Dateien.</summary>
    public async Task<int> AddPersonAsync(string name, IEnumerable<int> fileIds, bool hidden = false, int? groupId = null)
    {
        var group = groupId ?? await GroupIdAsync();
        await using var ctx = Fx.CreateContext();
        var person = new Person { Name = name, FirstName = name, CreatedAtUtc = DateTime.UtcNow, IsHidden = hidden, GroupId = group };
        ctx.Persons.Add(person);
        await ctx.SaveChangesAsync();
        foreach (var fileId in fileIds)
        {
            ctx.Faces.Add(new Face
            {
                FileId = fileId,
                BoxX = 0.4f, BoxY = 0.4f, BoxW = 0.2f, BoxH = 0.2f,
                Landmarks = new byte[40],
                Embedding = new byte[2048],
                ModelVersion = "test",
                DetScore = 0.9f,
                Size = 100,
                Quality = 0.9f,
                PersonId = person.Id,
                State = FaceState.Confirmed,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await ctx.SaveChangesAsync();
        return person.Id;
    }

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
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

    /// <summary>Dateien einer Story (zählende Einträge).</summary>
    public List<int> Items(int storyId)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FileId FROM StoryItems WHERE StoryId = $s AND Origin >= 0 ORDER BY FileId;";
        cmd.Parameters.AddWithValue("$s", storyId);
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    /// <summary>Alle Storys der Gruppe als (Id, Art, Signatur).</summary>
    public List<(int Id, StoryKind Kind, string Signature)> All(int? groupId = null)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = groupId is null ? "SELECT Id, Kind, Signature FROM Stories ORDER BY StartAt, Id;" : "SELECT Id, Kind, Signature FROM Stories WHERE GroupId = $g ORDER BY StartAt, Id;";
        if (groupId is { } g)
        {
            cmd.Parameters.AddWithValue("$g", g);
        }

        var list = new List<(int, StoryKind, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), (StoryKind)reader.GetInt32(1), reader.GetString(2)));
        }

        return list;
    }

    public FileOperationService CreateFileOperations(FakeRecycleBin? bin = null) => Fx.CreateFileOperationService(bin ?? new FakeRecycleBin());

    public static DateTime Utc(int year, int month, int day, int hour = 10, int minute = 0)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Stories.Dispose();
        Index.Dispose();
        Albums.Dispose();
        Fx.Dispose();
    }
}

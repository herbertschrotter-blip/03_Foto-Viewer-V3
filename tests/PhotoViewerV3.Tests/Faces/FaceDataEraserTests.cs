using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Database;
using System.Reflection;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Crops;
using PhotoViewerV3.Services.Faces.Erase;
using PhotoViewerV3.Services.Faces.Indexing;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Faces;

/// <summary>
/// „Gesichtsdaten löschen“ (0.2.38): nur die gewählte Bibliothek verliert Gesichter, FaceScans, Ablehnungen und den
/// Gesichter-Cache; Personen nur, wenn sie danach nirgends mehr vorkommen; Fotos bleiben; der Index wird ausgeschaltet.
/// Zwei Bibliotheken: 1 (Dateien 100, 101) und 2 (Datei 200). Anna nur in 1, Bert in 1 und 2, Carla nur als Vorschlag in 1, Dora ohne Gesicht.
/// </summary>
public sealed class FaceDataEraserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestAppDataPaths _paths;
    private readonly LibraryContextFactory _factory;

    public FaceDataEraserTests()
    {
        _paths = new TestAppDataPaths(Path.Combine(_root, "appdata"));
        _paths.EnsureDirectoriesExist();
        _factory = new LibraryContextFactory(_paths, new SqlitePragmaInterceptor());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Loescht_nur_die_Gesichtsdaten_der_Bibliothek_und_schaltet_den_Index_aus()
    {
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        using (var connection = _factory.OpenSqliteConnection())
        {
            Exec(connection, """
                INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'Eins', 'D:\Eins', 1, 1), (2, 'Zwei', 'D:\Zwei', 1, 1);
                INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt, RootId) VALUES (10, 1, 'D:\Eins', NULL, 'Eins', 1, NULL), (20, 2, 'D:\Zwei', NULL, 'Zwei', 1, NULL);
                INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Rating, Missing)
                VALUES (100, 1, 10, 'a.jpg', '.jpg', 1, 1, 1, 0, 1, 4, 0), (101, 1, 10, 'b.jpg', '.jpg', 1, 1, 1, 0, 0, NULL, 0), (200, 2, 20, 'c.jpg', '.jpg', 1, 1, 1, 0, 0, NULL, 0);
                INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES (1, 'Anna', 1, 1, 0), (2, 'Bert', 1, 2, 0), (3, 'Carla', 1, NULL, 0), (4, 'Dora', 1, NULL, 0);
                """);
            InsertFace(connection, 1, 100, state: 2, personId: 1);
            InsertFace(connection, 2, 101, state: 2, personId: 2);
            InsertFace(connection, 3, 101, state: 1, suggested: 3);
            InsertFace(connection, 4, 200, state: 2, personId: 2);
            InsertFace(connection, 5, 200, state: 0);
            Exec(connection, """
                INSERT INTO FaceRejections (FaceId, PersonId) VALUES (3, 1), (5, 1);
                INSERT INTO FaceScans (FileId, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode) VALUES (100, 'v', 1, 1, NULL), (101, 'v', 1, 2, NULL), (200, 'v', 1, 2, NULL);
                """);
        }

        var cache1 = FaceDataCache.GetFolder(_paths, 1);
        var cache2 = FaceDataCache.GetFolder(_paths, 2);
        Directory.CreateDirectory(Path.Combine(cache1, "sub"));
        File.WriteAllBytes(Path.Combine(cache1, "1.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(cache1, "sub", "2.jpg"), [2]);
        Directory.CreateDirectory(cache2);
        File.WriteAllBytes(Path.Combine(cache2, "4.jpg"), [4]);
        var thumbnail = Path.Combine(_paths.GetLibraryThumbnailsPath(1), "100_256_still.jpg");
        File.WriteAllBytes(thumbnail, [9]);

        var index = new RecordingIndex();
        var messenger = new WeakReferenceMessenger();
        var faces = new List<FacesChangedMessage>();
        var persons = new List<PersonsChangedMessage>();
        messenger.Register<FacesChangedMessage>(faces, static (r, m) => ((List<FacesChangedMessage>)r).Add(m));
        messenger.Register<PersonsChangedMessage>(persons, static (r, m) => ((List<PersonsChangedMessage>)r).Add(m));

        var recognition = RecordingProxy.Create<IFaceRecognitionService>(out var recognitionCalls);
        var crops = RecordingProxy.Create<IFaceCropService>(out var cropCalls);
        var eraser = new FaceDataEraser(_factory, _paths, index, NullLogger<FaceDataEraser>.Instance, messenger, TimeSpan.Zero, recognition, crops);
        var result = await eraser.EraseLibraryAsync(1);

        result.Should().Be(new FaceDataEraseResult(Faces: 3, Scans: 2, Rejections: 1, Persons: 2, CacheFiles: 2));
        index.EnabledCalls.Should().Equal(false);
        index.Cancels.Should().BeGreaterThan(0);

        using (var connection = _factory.OpenSqliteConnection())
        {
            Scalar(connection, "SELECT COUNT(*) FROM Faces WHERE FileId IN (100, 101)").Should().Be(0);
            Scalar(connection, "SELECT COUNT(*) FROM Faces WHERE FileId = 200").Should().Be(2, "Bibliothek 2 bleibt");
            Scalar(connection, "SELECT COUNT(*) FROM FaceScans WHERE FileId = 200").Should().Be(1);
            Scalar(connection, "SELECT COUNT(*) FROM FaceScans").Should().Be(1);
            Scalar(connection, "SELECT COUNT(*) FROM FaceRejections WHERE FaceId = 5").Should().Be(0, "Anna ist weg — ihre Ablehnung geht mit");
            Scalar(connection, "SELECT group_concat(Name) FROM (SELECT Name FROM Persons ORDER BY Id)").Should().Be(
                "Bert,Dora", "Anna und Carla kommen nirgends mehr vor; Dora hatte in dieser Bibliothek nie ein Gesicht");
            Scalar(connection, "SELECT COUNT(*) FROM Persons WHERE Name = 'Bert' AND CoverFaceId IS NULL").Should().Be(1, "Titelbild-Gesicht 2 ist gelöscht");
            Scalar(connection, "SELECT COUNT(*) FROM Files").Should().Be(3, "Fotos bleiben");
            Scalar(connection, "SELECT Rating FROM Files WHERE Id = 100").Should().Be(4);
            Scalar(connection, "PRAGMA foreign_key_check;").Should().Be(0);
        }

        Directory.Exists(cache1).Should().BeFalse();
        File.Exists(Path.Combine(cache2, "4.jpg")).Should().BeTrue();
        File.Exists(thumbnail).Should().BeTrue("Vorschaubilder der Fotos bleiben");
        faces.Should().ContainSingle().Which.Should().Match<FacesChangedMessage>(m => m.LibraryId == 1 && m.FileIds.OrderBy(i => i).SequenceEqual(new[] { 100, 101 }));
        persons.Should().ContainSingle().Which.LibraryId.Should().Be(1);
        recognitionCalls.Should().Equal(nameof(IFaceRecognitionService.InvalidateAll));
        cropCalls.Should().Equal(nameof(IFaceCropService.DeleteAll));
    }

    [Fact]
    public async Task Leere_Bibliothek_ohne_Cache_meldet_null_und_wirft_nicht()
    {
        await new DatabaseInitializer(_factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var eraser = new FaceDataEraser(_factory, _paths, new RecordingIndex(), NullLogger<FaceDataEraser>.Instance, new WeakReferenceMessenger(), TimeSpan.Zero);
        (await eraser.EraseLibraryAsync(42)).Should().Be(new FaceDataEraseResult(0, 0, 0, 0, 0));
    }

    private static void InsertFace(SqliteConnection connection, int id, int fileId, int state, int? personId = null, int? suggested = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Faces (Id, FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality,
                Embedding, ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
            VALUES ($id, $file, 0.1, 0.1, 0.2, 0.2, $lm, 0.9, 100, 50, 0.8, $emb, 'v', $p, $s, $sp, NULL, 1);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$lm", new byte[40]);
        cmd.Parameters.AddWithValue("$emb", new byte[2048]);
        cmd.Parameters.AddWithValue("$p", (object?)personId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$sp", (object?)suggested ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value switch
        {
            null or DBNull => 0,
            long l => (int)l,
            _ => value,
        };
    }

    /// <summary>
    /// Schreibt aufgerufene Methoden mit (DispatchProxy statt Implementierung — bleibt gültig, wenn P1s Interfaces beim
    /// Merge weitere Mitglieder haben).
    /// </summary>
    public class RecordingProxy : DispatchProxy
    {
        private List<string> _calls = new();

        public static T Create<T>(out List<string> calls)
            where T : class
        {
            var proxy = Create<T, RecordingProxy>();
            calls = ((RecordingProxy)(object)proxy)._calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _calls.Add(targetMethod!.Name);
            var type = targetMethod.ReturnType;
            return type == typeof(void) ? null
                : type == typeof(Task) ? Task.CompletedTask
                : type.IsValueType ? Activator.CreateInstance(type)
                : null;
        }
    }

    /// <summary>Gesichts-Index, der nur mitschreibt (ausschalten, abbrechen).</summary>
    private sealed class RecordingIndex : IFaceIndexService
    {
        public List<bool> EnabledCalls { get; } = new();

        public int Cancels { get; private set; }

        public FaceIndexStatus Status => FaceIndexStatus.Disabled;

        public FaceIndexSettings Settings => FaceIndexSettings.Default;

        public bool IsPaused => false;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            EnabledCalls.Add(enabled);
            Cancels++;
            return Task.CompletedTask;
        }

        public Task SetUseGpuAsync(bool useGpu, CancellationToken ct = default) => Task.CompletedTask;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void CancelRun() => Cancels++;

        public void RequestRun()
        {
        }

        public Task<int> RebuildAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}

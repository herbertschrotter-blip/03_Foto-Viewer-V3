using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Faces;

/// <summary>
/// Migration AddFaces (ADR-0035) auf einer Bestands-DB, Kaskaden (Datei weg → Gesichter, Scans, Ablehnungen weg;
/// Person weg → Zuordnung NULL), Abgleich bei erneuter Analyse und „Neu aufbauen“ (Gesichter wandern mit).
/// </summary>
public sealed class FaceSchemaTests
{
    [Fact]
    public async Task Bestands_DB_bekommt_die_Gesichtstabellen_und_behaelt_ihre_Daten()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        try
        {
            var paths = new TestAppDataPaths(Path.Combine(rootDir, "appdata"));
            paths.EnsureDirectoriesExist();
            var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
            await using (var ctx = factory.Create())
            {
                await ctx.GetService<IMigrator>().MigrateAsync("AddCategorySortIndex");
            }

            using (var connection = factory.OpenSqliteConnection())
            {
                Exec(connection, "INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'Fotos', 'D:\\Fotos', 1, 1);");
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt, RootId) VALUES (10, 1, 'D:\\Fotos', NULL, 'Fotos', 1, NULL);");
                Exec(connection, "INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Rating, Missing) VALUES (100, 1, 10, 'a.jpg', '.jpg', 1, 1, 1, 0, 1, 4, 0);");
                Tables(connection).Should().NotContain("Faces");
            }

            var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            result.NewlyMigrated.Should().BeGreaterThan(0);

            using (var connection = factory.OpenSqliteConnection())
            {
                Tables(connection).Should().Contain(["Persons", "Faces", "FaceRejections", "FaceScans"]);
                Indexes(connection, "Faces").Should().Contain(["IX_Faces_File", "IX_Faces_Person", "IX_Faces_State"]);
                Scalar(connection, "SELECT Rating FROM Files WHERE Id = 100").Should().Be(4, "Bestand bleibt");
                Scalar(connection, "PRAGMA foreign_key_check;").Should().Be(0);
            }

            await using var verify = factory.Create();
            (await verify.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(rootDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Kaskaden_Datei_weg_nimmt_Gesichter_Scans_Ablehnungen_mit_Person_weg_setzt_NULL()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("a.jpg");
        fx.WriteLibraryFile("b.jpg");
        await fx.ScanAsync(lib.Id);
        var a = (await fx.FindFileAsync("a.jpg"))!.Id;
        var b = (await fx.FindFileAsync("b.jpg"))!.Id;

        await using (var ctx = fx.CreateContext())
        {
            var anna = new Person { Name = "Anna", CreatedAtUtc = DateTime.UtcNow };
            var ben = new Person { Name = "Ben", CreatedAtUtc = DateTime.UtcNow };
            ctx.Persons.AddRange(anna, ben);
            await ctx.SaveChangesAsync();
            var faceA = NewFace(a, anna.Id, FaceState.Confirmed);
            var faceB = NewFace(b, null, FaceState.Suggested);
            faceB.SuggestedPersonId = ben.Id;
            ctx.Faces.AddRange(faceA, faceB);
            ctx.FaceScans.AddRange(
                new FaceScan { FileId = a, ModelVersion = "v1", ScannedAtUtc = DateTime.UtcNow, FaceCount = 1 },
                new FaceScan { FileId = b, ModelVersion = "v1", ScannedAtUtc = DateTime.UtcNow, FaceCount = 1 });
            await ctx.SaveChangesAsync();
            ctx.FaceRejections.Add(new FaceRejection { FaceId = faceA.Id, PersonId = ben.Id });
            await ctx.SaveChangesAsync();

            var loaded = await ctx.Faces.AsNoTracking().SingleAsync(f => f.Id == faceA.Id);
            FaceEmbeddingCodec.Decode(loaded.Embedding)[3].Should().Be(1f, "Blob unverändert über EF");
        }

        FaceIndexServiceTests.Exec(fx, $"DELETE FROM Files WHERE Id = {a};");
        FaceIndexServiceTests.Scalar(fx, $"SELECT COUNT(*) FROM Faces WHERE FileId = {a}").Should().Be(0);
        FaceIndexServiceTests.Scalar(fx, $"SELECT COUNT(*) FROM FaceScans WHERE FileId = {a}").Should().Be(0);
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM FaceRejections").Should().Be(0, "Ablehnung hängt am Gesicht");

        FaceIndexServiceTests.Exec(fx, "DELETE FROM Persons WHERE Name = 'Ben';");
        FaceIndexServiceTests.Scalar(fx, $"SELECT COUNT(*) FROM Faces WHERE FileId = {b} AND SuggestedPersonId IS NULL").Should().Be(1, "Person weg → Vorschlag NULL, Gesicht bleibt");
        FaceIndexServiceTests.Scalar(fx, "PRAGMA foreign_key_check;").Should().Be(0);
    }

    [Fact]
    public void Abgleich_ueber_die_Box_behaelt_Bestaetigte_setzt_Vorschlaege_zurueck_und_loescht_Verschwundene()
    {
        var existing = new[]
        {
            new ExistingFace(1, new RelativeBox(0.10f, 0.10f, 0.20f, 0.20f), FaceState.Confirmed, 7, null, null),
            new ExistingFace(2, new RelativeBox(0.50f, 0.10f, 0.20f, 0.20f), FaceState.Suggested, null, 8, 0.6f),
            new ExistingFace(3, new RelativeBox(0.80f, 0.70f, 0.10f, 0.10f), FaceState.Ignored, null, null, null),
            new ExistingFace(4, new RelativeBox(0.80f, 0.10f, 0.10f, 0.10f), FaceState.Unconfirmed, null, null, null),
            new ExistingFace(5, new RelativeBox(0.05f, 0.80f, 0.10f, 0.10f), FaceState.Confirmed, 9, null, null),
        };
        var detected = new[]
        {
            new RelativeBox(0.52f, 0.11f, 0.20f, 0.20f), // ≈ 2
            new RelativeBox(0.11f, 0.10f, 0.19f, 0.21f), // ≈ 1
            new RelativeBox(0.30f, 0.60f, 0.10f, 0.10f), // neu
        };

        var plan = FaceRowMerger.Plan(existing, detected);

        plan.Updates.Select(u => (u.NewIndex, u.Existing.Id)).Should().Equal((0, 2), (1, 1));
        plan.Inserts.Should().Equal(2);
        plan.DeleteIds.Should().Equal(new[] { 4 }, "nur unbestätigte verschwinden; ignorierte (3) und bestätigte (5) bleiben ungefunden stehen");
        FaceRowMerger.Plan(existing, []).DeleteIds.Should().Equal(2, 4);
        FaceRowMerger.CarryState(existing[0]).Should().Be((FaceState.Confirmed, (int?)null, (float?)null));
        FaceRowMerger.CarryState(existing[1]).Should().Be((FaceState.Unconfirmed, (int?)null, (float?)null));
        RelativeBox.IoU(new RelativeBox(0, 0, 0.5f, 0.5f), new RelativeBox(0.25f, 0, 0.5f, 0.5f)).Should().BeApproximately(1f / 3f, 1e-6f);
    }

    [Fact]
    public async Task Neu_aufbauen_traegt_Gesichter_Scans_Zuordnungen_und_Ablehnungen_auf_die_neuen_FileIds()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var lib = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile("2024\\p2_paar.jpg");
        fx.WriteLibraryFile("2025\\p2_paar.jpg"); // gleicher Name, anderer Ordner
        fx.WriteLibraryFile("solo.jpg");
        var gone = fx.WriteLibraryFile("weg\\p1_weg.jpg");
        fx.WriteLibraryFile("p0_ohne.jpg");
        await fx.ScanAsync(lib.Id);
        var factory = new FakeFaceAnalyzerFactory();
        using (var service = new FaceIndexService(
                   fx.ContextFactory, fx.CreateLibraryService(), factory, new FakeFaceImageLoader(),
                   new MemoryFaceSettingsStore(new FaceIndexSettings(true, true)),
                   Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceIndexService>.Instance, fx.Messenger,
                   new FaceIndexOptions { StartDelay = TimeSpan.Zero }))
        {
            await service.StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(service.Status is { State: FaceIndexState.Idle, Done: 5, Total: 5 }) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            service.Status.Done.Should().Be(5);
        }

        var faceIdsBefore = Ids(fx, "SELECT Id FROM Faces ORDER BY Id");
        faceIdsBefore.Should().HaveCount(6);
        var confirmed = FaceIndexServiceTests.Scalar(fx,
            "SELECT MIN(x.Id) FROM Faces x JOIN Files f ON f.Id = x.FileId JOIN Folders d ON d.Id = f.FolderId WHERE d.Path LIKE '%2025'");
        FaceIndexServiceTests.Exec(fx, "INSERT INTO Persons (Id, Name, CreatedAtUtc, IsHidden) VALUES (1, 'Anna', 1, 0), (2, 'Ben', 1, 0);");
        FaceIndexServiceTests.Exec(fx, $"UPDATE Faces SET PersonId = 1, State = 2 WHERE Id = {confirmed};");
        FaceIndexServiceTests.Exec(fx, $"INSERT INTO FaceRejections (FaceId, PersonId) VALUES ({confirmed}, 2);");
        FaceIndexServiceTests.Exec(fx, $"UPDATE Persons SET CoverFaceId = {confirmed} WHERE Id = 1;");
        var goneFaceId = FaceIndexServiceTests.Scalar(fx, "SELECT x.Id FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.Name = 'p1_weg.jpg'");
        var oldMaxFileId = FaceIndexServiceTests.Scalar(fx, "SELECT MAX(Id) FROM Files");
        File.Delete(gone);

        var result = await fx.CreateScanService().ScanAsync(lib.Id, ScanMode.ClearAndRescan, null, CancellationToken.None);

        result.Outcome.Should().Be(ScanOutcome.Completed);
        FaceIndexServiceTests.Scalar(fx, "SELECT MIN(Id) FROM Files").Should().BeGreaterThan(oldMaxFileId, "neue FileIds");
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM Faces WHERE FileId < 0").Should().Be(0);
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM FaceScans WHERE FileId < 0").Should().Be(0);
        Ids(fx, "SELECT Id FROM Faces ORDER BY Id").Should().Equal(faceIdsBefore.Where(id => id != goneFaceId), "Face-Ids bleiben, nur das Gesicht der entfernten Datei fehlt");
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM Faces").Should().Be(5, "das Gesicht der entfernten Datei ist weg");
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM Faces x JOIN Files f ON f.Id = x.FileId").Should().Be(5, "alle zeigen auf neue Files-Zeilen");
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId").Should().Be(4, "Scans mitgewandert, keine Neuanalyse nötig");
        FaceIndexServiceTests.Scalar(fx,
            $"SELECT COUNT(*) FROM Faces x JOIN Files f ON f.Id = x.FileId JOIN Folders d ON d.Id = f.FolderId WHERE x.Id = {confirmed} AND x.PersonId = 1 AND x.State = 2 AND d.Path LIKE '%2025'")
            .Should().Be(1, "Bestätigung bleibt am richtigen Foto (gleicher Name im anderen Ordner bekommt sie nicht)");
        FaceIndexServiceTests.Scalar(fx, "SELECT COUNT(*) FROM FaceRejections").Should().Be(1);
        FaceIndexServiceTests.Scalar(fx, "SELECT CoverFaceId FROM Persons WHERE Id = 1").Should().Be(confirmed);
        FaceIndexServiceTests.Scalar(fx, "PRAGMA foreign_key_check;").Should().Be(0);

        // Ein weiterer Durchgang hat nichts zu tun.
        var again = new FakeFaceAnalyzerFactory();
        using var after = new FaceIndexService(
            fx.ContextFactory, fx.CreateLibraryService(), again, new FakeFaceImageLoader(),
            new MemoryFaceSettingsStore(new FaceIndexSettings(true, true)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceIndexService>.Instance, fx.Messenger,
            new FaceIndexOptions { StartDelay = TimeSpan.Zero });
        await after.StartAsync();
        var until = DateTime.UtcNow.AddSeconds(20);
        while (after.CompletedRuns < 1 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        again.Calls.Should().Be(0);
        after.Status.Done.Should().Be(4);
        after.Status.Total.Should().Be(4);
    }

    private static Face NewFace(int fileId, int? personId, FaceState state)
    {
        var embedding = new float[512];
        embedding[3] = 1f;
        return new Face
        {
            FileId = fileId,
            BoxX = 0.1f,
            BoxY = 0.1f,
            BoxW = 0.2f,
            BoxH = 0.2f,
            Landmarks = FaceEmbeddingCodec.EncodeFloats(new float[10]),
            DetScore = 0.9f,
            Size = 50,
            Sharpness = 100,
            Quality = 0.5f,
            Embedding = FaceEmbeddingCodec.Encode(embedding),
            ModelVersion = "v1",
            PersonId = personId,
            State = state,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    private static List<long> Ids(TempLibraryFixture fx, string sql)
    {
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static List<string> Tables(SqliteConnection connection) => Names(connection, "SELECT name FROM sqlite_master WHERE type = 'table';");

    private static List<string> Indexes(SqliteConnection connection, string table)
        => Names(connection, $"SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = '{table}';");

    private static List<string> Names(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

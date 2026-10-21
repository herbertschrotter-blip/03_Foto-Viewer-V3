using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Libraries;

/// <summary>
/// Migration AddLibraryScoping (0.2.51, ADR-0048) auf einem Bestands-Katalog mit ZWEI Bibliotheken: jede bekommt ihre
/// Gruppe; Begriffe gehören danach der Gruppe jeder Bibliothek, die sie benutzt (Kopie je weiterer Bibliothek,
/// Zuordnungen umgehängt), ungenutzte bekommt jede Bibliothek. Nichts geht verloren, kein Fremdschlüssel bricht, keine
/// Tabelle wird neu aufgebaut (Zuordnungen in Faces und FaceRejections bleiben).
/// </summary>
public sealed class LibraryScopingMigrationTests
{
    [Fact]
    public async Task Bestand_mit_zwei_Bibliotheken_wird_verlustfrei_je_Bibliothek_getrennt()
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
                await ctx.GetService<IMigrator>().MigrateAsync("AddCapturedAt");
            }

            using (var c = factory.OpenSqliteConnection())
            {
                Exec(c, """
                    INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'Familie', 'D:\A', 1, 2), (2, 'Arbeit', 'D:\B', 1, 1);
                    INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt, RootId) VALUES (10, 1, 'D:\A', NULL, 'A', 1, NULL), (20, 2, 'D:\B', NULL, 'B', 1, NULL);
                    INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Rating, Missing)
                    VALUES (100, 1, 10, 'a1.jpg', '.jpg', 1, 1, 1, 0, 0, NULL, 0),
                           (101, 1, 10, 'a2.jpg', '.jpg', 1, 1, 1, 0, 0, NULL, 0),
                           (200, 2, 20, 'b1.jpg', '.jpg', 1, 1, 1, 0, 0, NULL, 0);
                    INSERT INTO Tags (Id, Name, Color, SortOrder, CreatedAtUtc) VALUES (1, 'Urlaub', '#E53935', 0, 1), (2, 'Projekt X', '#FB8C00', 1, 1), (3, 'Leer', '#FDD835', 2, 1);
                    INSERT INTO FileTags (FileId, TagId) VALUES (100, 1), (200, 1), (200, 2);
                    INSERT INTO Categories (Id, Name, SortOrder, CreatedAtUtc) VALUES (1, 'Ort', 0, 1), (2, 'Ungenutzt', 1, 1);
                    INSERT INTO CategoryValues (Id, CategoryId, Name, SortOrder) VALUES (10, 1, 'Nord', 0), (11, 1, 'Süd', 1), (20, 2, 'X', 0);
                    INSERT INTO FileCategoryValues (FileId, CategoryId, ValueId) VALUES (100, 1, 10), (200, 1, 11);
                    INSERT INTO PersonRelations (Id, Name) VALUES (7, 'Schwester'), (8, 'Freund');
                    INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden, FirstName, LastName, Nickname, RelationId, IsMe)
                    VALUES (1, 'Anna Muster', 1, 600, 0, 'Anna', 'Muster', NULL, 7, 1),
                           (2, 'Bob', 1, NULL, 0, 'Bob', '', NULL, NULL, 0),
                           (3, 'Carl', 1, NULL, 1, 'Carl', '', NULL, 8, 0);
                    INSERT INTO Faces (Id, FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality, Embedding,
                                       ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
                    VALUES (500, 100, 0.1, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', 1, 2, NULL, NULL, 1),
                           (600, 200, 0.1, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', 1, 2, NULL, NULL, 1),
                           (601, 200, 0.5, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', NULL, 1, 2, 0.7, 1),
                           (501, 101, 0.5, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', NULL, 0, NULL, NULL, 1);
                    INSERT INTO FaceRejections (FaceId, PersonId) VALUES (501, 2);
                    """);
            }

            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

            using var connection = factory.OpenSqliteConnection();
            long S(string sql) => Scalar(connection, sql);

            // Gruppen: eine je Bibliothek (Id = Bibliotheks-Id)
            S("SELECT COUNT(*) FROM LibraryGroups").Should().Be(2);
            S("SELECT COUNT(*) FROM Libraries WHERE GroupId = Id").Should().Be(2);
            S("SELECT COUNT(*) FROM LibraryGroups WHERE Id = 1 AND Name = 'Familie'").Should().Be(1);

            // Tags: Urlaub in beiden (benutzt), Projekt X nur in 2, Leer (ungenutzt) in beiden
            Names(connection, "Tags", 1).Should().BeEquivalentTo(["Urlaub", "Leer"]);
            Names(connection, "Tags", 2).Should().BeEquivalentTo(["Urlaub", "Projekt X", "Leer"]);
            S("SELECT COUNT(*) FROM FileTags").Should().Be(3, "keine Zuordnung verloren");
            S("SELECT TagId FROM FileTags WHERE FileId = 100").Should().Be(1, "kleinste Bibliothek behält das Original");
            S("SELECT COUNT(*) FROM FileTags ft JOIN Tags t ON t.Id = ft.TagId JOIN Files f ON f.Id = ft.FileId JOIN Libraries l ON l.Id = f.LibraryId WHERE t.GroupId <> l.GroupId")
                .Should().Be(0, "jede Zuordnung zeigt auf ein Tag der eigenen Gruppe");
            Text(connection, "SELECT Color FROM Tags WHERE GroupId = 2 AND Name = 'Urlaub'").Should().Be("#E53935", "Kopie behält Farbe");

            // Kategorien: Ort in beiden mit allen Werten, Ungenutzt in beiden
            Names(connection, "Categories", 1).Should().BeEquivalentTo(["Ort", "Ungenutzt"]);
            Names(connection, "Categories", 2).Should().BeEquivalentTo(["Ort", "Ungenutzt"]);
            S("SELECT COUNT(*) FROM CategoryValues v JOIN Categories c ON c.Id = v.CategoryId WHERE c.GroupId = 2 AND c.Name = 'Ort'").Should().Be(2);
            S("SELECT COUNT(*) FROM FileCategoryValues").Should().Be(2);
            Text(connection, "SELECT v.Name FROM FileCategoryValues x JOIN CategoryValues v ON v.Id = x.ValueId WHERE x.FileId = 200").Should().Be("Süd");
            S("SELECT COUNT(*) FROM FileCategoryValues x JOIN Categories c ON c.Id = x.CategoryId JOIN CategoryValues v ON v.Id = x.ValueId JOIN Files f ON f.Id = x.FileId WHERE c.GroupId <> f.LibraryId OR v.CategoryId <> c.Id")
                .Should().Be(0, "Wert und Kategorie gehören zur Gruppe der Datei");

            // Personen: Anna (Gesichter in beiden) → je Bibliothek; Bob (Vorschlag in 2, Ablehnung in 1) → beide; Carl (ohne Gesicht) → beide
            Names(connection, "Persons", 1).Should().BeEquivalentTo(["Anna Muster", "Bob", "Carl"]);
            Names(connection, "Persons", 2).Should().BeEquivalentTo(["Anna Muster", "Bob", "Carl"]);
            S("SELECT PersonId FROM Faces WHERE Id = 500").Should().Be(1);
            var annaCopy = S("SELECT Id FROM Persons WHERE GroupId = 2 AND Name = 'Anna Muster'");
            S("SELECT PersonId FROM Faces WHERE Id = 600").Should().Be(annaCopy, "Gesicht in Bibliothek 2 zeigt auf die Kopie");
            S("SELECT SuggestedPersonId FROM Faces WHERE Id = 601").Should().Be(S("SELECT Id FROM Persons WHERE GroupId = 2 AND Name = 'Bob'"));
            S("SELECT PersonId FROM FaceRejections WHERE FaceId = 501").Should().Be(S("SELECT Id FROM Persons WHERE GroupId = 1 AND Name = 'Bob'"));
            S("SELECT COUNT(*) FROM FaceRejections").Should().Be(1, "Ablehnungen bleiben (kein Neuaufbau)");
            S("SELECT COUNT(*) FROM Faces WHERE PersonId IS NOT NULL").Should().Be(2);
            S("SELECT COALESCE(CoverFaceId, -1) FROM Persons WHERE Id = 1").Should().Be(-1, "Titelbild lag in Bibliothek 2");
            S($"SELECT CoverFaceId FROM Persons WHERE Id = {annaCopy}").Should().Be(600, "die Kopie behält das Titelbild");
            S("SELECT COUNT(*) FROM Persons WHERE IsMe = 1").Should().Be(2, "„Das bin ich“ je Gruppe");
            S("SELECT COUNT(*) FROM Persons WHERE Name = 'Carl' AND IsHidden = 1").Should().Be(2, "Details wandern mit");

            // Beziehungen: je Gruppe, Personen zeigen auf die Beziehung ihrer Gruppe
            Names(connection, "PersonRelations", 1).Should().BeEquivalentTo(["Schwester", "Freund"]);
            Names(connection, "PersonRelations", 2).Should().BeEquivalentTo(["Schwester", "Freund"]);
            S("SELECT COUNT(*) FROM Persons p JOIN PersonRelations r ON r.Id = p.RelationId WHERE r.GroupId <> p.GroupId").Should().Be(0);
            S("SELECT COUNT(*) FROM Persons WHERE RelationId IS NOT NULL").Should().Be(4);

            S("PRAGMA foreign_key_check;").Should().Be(0);
            S("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('UX_Tags_Group_Name', 'UX_Categories_Group_Name', 'IX_Persons_Group_Name', 'IX_Persons_Group_IsMe', 'IX_PersonRelations_Group_Name', 'IX_Libraries_Group')")
                .Should().Be(6);
            S("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('UX_Tags_Name', 'UX_Categories_Name', 'IX_Persons_IsMe', 'IX_PersonRelations_Name')")
                .Should().Be(0, "globale Eindeutigkeiten sind weg");
            FluentActions.Invoking(() => Exec(connection, "INSERT INTO Tags (Name, Color, SortOrder, CreatedAtUtc, GroupId) VALUES ('URLAUB', '#000000', 9, 1, 1);"))
                .Should().Throw<SqliteException>("je Gruppe eindeutig ohne Groß-/Kleinschreibung");
            Exec(connection, "INSERT INTO Tags (Name, Color, SortOrder, CreatedAtUtc, GroupId) VALUES ('Nur in drei', '#000000', 9, 1, 3);");

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
    public async Task Bestand_ohne_Bibliothek_behaelt_Begriffe_mit_Gruppe_0()
    {
        using var fx = await TempLibraryFixture.CreateAsync(); // schon migriert: prüft nur den leeren Fall der Daten-SQL
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        Exec(connection, "INSERT INTO Tags (Name, Color, SortOrder, CreatedAtUtc) VALUES ('Vorab', '#000000', 0, 1);");
        Scalar(connection, "SELECT GroupId FROM Tags WHERE Name = 'Vorab'").Should().Be(0, "ohne Bibliothek: Gruppe 0, die erste Bibliothek übernimmt sie");
    }

    private static List<string> Names(SqliteConnection connection, string table, int groupId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT Name FROM {table} WHERE GroupId = $g ORDER BY Id;";
        cmd.Parameters.AddWithValue("$g", groupId);
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static string? Text(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
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

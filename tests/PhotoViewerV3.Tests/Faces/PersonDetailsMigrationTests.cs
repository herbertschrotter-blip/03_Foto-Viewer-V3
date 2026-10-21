using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Faces;

/// <summary>
/// Migration AddPersonDetails (0.2.47, ADR-0045) auf einer Bestands-DB: Namen am ersten Leerzeichen geteilt (verlustfrei,
/// Name bleibt), Zuordnungen und Ablehnungen bleiben (kein Neuaufbau der Tabelle Persons), neue Tabelle und Indizes.
/// Dazu die Namens- und Datumsregeln der Domäne.
/// </summary>
public sealed class PersonDetailsMigrationTests
{
    [Fact]
    public async Task Bestands_Personen_werden_am_ersten_Leerzeichen_geteilt_ohne_Datenverlust()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        try
        {
            var paths = new TestAppDataPaths(Path.Combine(rootDir, "appdata"));
            paths.EnsureDirectoriesExist();
            var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
            await using (var ctx = factory.Create())
            {
                await ctx.GetService<IMigrator>().MigrateAsync("AddFacePersonIndex");
            }

            var names = new[] { "Anna", "Anna Muster", "Opa Franz Huber", "Jean-Luc  Picard", "x y" };
            using (var connection = factory.OpenSqliteConnection())
            {
                Exec(connection, "INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'Fotos', 'D:\\Fotos', 1, 1);");
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt, RootId) VALUES (10, 1, 'D:\\Fotos', NULL, 'Fotos', 1, NULL);");
                Exec(connection, "INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Rating, Missing) VALUES (100, 1, 10, 'a.jpg', '.jpg', 1, 1, 1, 0, 0, NULL, 0);");
                for (var i = 0; i < names.Length; i++)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "INSERT INTO Persons (Id, Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES ($id, $n, 1, NULL, 0);";
                    cmd.Parameters.AddWithValue("$id", i + 1);
                    cmd.Parameters.AddWithValue("$n", names[i]);
                    cmd.ExecuteNonQuery();
                }

                Exec(connection, """
                    INSERT INTO Faces (Id, FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality, Embedding,
                                       ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
                    VALUES (500, 100, 0.1, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', 2, 2, NULL, NULL, 1),
                           (501, 100, 0.5, 0.1, 0.2, 0.2, x'00', 0.9, 100, 10, 0.8, x'00', 'v1', NULL, 1, 3, 0.7, 1);
                    """);
                Exec(connection, "INSERT INTO FaceRejections (FaceId, PersonId) VALUES (501, 1);");
            }

            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

            using (var connection = factory.OpenSqliteConnection())
            {
                var rows = new List<(string Name, string First, string Last)>();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Name, FirstName, LastName, Nickname, RelationId, IsMe FROM Persons ORDER BY Id;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        reader.IsDBNull(3).Should().BeTrue("kein Rufname aus der Migration");
                        reader.IsDBNull(4).Should().BeTrue();
                        reader.GetInt32(5).Should().Be(0);
                        rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                    }
                }

                rows.Select(r => (r.First, r.Last)).Should().Equal(
                    ("Anna", ""), ("Anna", "Muster"), ("Opa", "Franz Huber"), ("Jean-Luc", " Picard"), ("x", "y"));
                rows.Select(r => r.Name).Should().Equal(names, "der alte Name bleibt als Anzeigename stehen");
                rows.Should().OnlyContain(r => (r.Last.Length == 0 ? r.First : r.First + " " + r.Last) == r.Name, "verlustfrei rekonstruierbar");
                rows.Select(r => PersonNames.Split(r.Name)).Should().Equal(rows.Select(r => (r.First, r.Last)), "Domäne teilt genauso");

                Scalar(connection, "SELECT PersonId FROM Faces WHERE Id = 500").Should().Be(2, "Zuordnung bleibt (kein Neuaufbau von Persons)");
                Scalar(connection, "SELECT SuggestedPersonId FROM Faces WHERE Id = 501").Should().Be(3);
                Scalar(connection, "SELECT COUNT(*) FROM FaceRejections").Should().Be(1, "Ablehnungen bleiben");
                Scalar(connection, "PRAGMA foreign_key_check;").Should().Be(0);
                // Seit 0.2.51 (ADR-0048, AddLibraryScoping) gelten „Das bin ich“ und Beziehungsnamen je Bibliotheks-Gruppe.
                Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('IX_Persons_Relation', 'IX_Persons_Group_IsMe', 'IX_PersonRelations_Group_Name')")
                    .Should().Be(3);

                // Fremdschlüssel der Beziehung: Löschen setzt NULL
                Exec(connection, "INSERT INTO PersonRelations (Id, Name) VALUES (7, 'Schwester'); UPDATE Persons SET RelationId = 7 WHERE Id = 2;");
                FluentActions.Invoking(() => Exec(connection, "INSERT INTO PersonRelations (Name) VALUES ('SCHWESTER');"))
                    .Should().Throw<SqliteException>("Beziehungsnamen sind ohne Groß-/Kleinschreibung eindeutig");
                Exec(connection, "DELETE FROM PersonRelations WHERE Id = 7;");
                Scalar(connection, "SELECT COUNT(*) FROM Persons WHERE RelationId IS NULL").Should().Be(5);
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

    [Theory]
    [InlineData("Anna", null, null, "Anna")]
    [InlineData("Anna", "Muster", null, "Anna Muster")]
    [InlineData(" Anna ", " Muster ", "  ", "Anna Muster")]
    [InlineData("Franz", "Huber", "Opa Franz", "Opa Franz")]
    [InlineData("", "Muster", null, "Muster")]
    [InlineData(null, null, null, "")]
    public void Anzeigename_ist_Rufname_sonst_Vor_und_Nachname(string? first, string? last, string? nick, string expected)
        => PersonNames.DisplayName(first, last, nick).Should().Be(expected);

    [Fact]
    public void Geburtsdatum_ungefaehr_mit_Altersspanne()
    {
        var today = new DateOnly(2026, 10, 21);
        PersonBirthDate.Create(1980, 3, 12, 2026)!.Value.Format().Should().Be("12.03.1980");
        PersonBirthDate.Create(1980, 3, 12, 2026)!.Value.AgeOn(today).Should().Be((46, 46));
        PersonBirthDate.Create(1980, 12, 24, 2026)!.Value.AgeOn(today).Should().Be((45, 45));
        PersonBirthDate.Create(1980, 10, null, 2026)!.Value.AgeOn(today).Should().Be((45, 46), "im Geburtsmonat unklar");
        PersonBirthDate.Create(1980, 10, null, 2026)!.Value.Format().Should().Be("Oktober 1980");
        PersonBirthDate.Create(1980, null, null, 2026)!.Value.FormatAgeOn(today).Should().Be("etwa 45–46 Jahre");
        PersonBirthDate.Create(2026, null, null, 2026)!.Value.AgeOn(today).Should().Be((0, 0));
        PersonBirthDate.Create(2025, 10, 21, 2026)!.Value.FormatAgeOn(today).Should().Be("1 Jahr");
        PersonBirthDate.Create(1980, 3, 12, 2026)!.Value.AgeOn(new DateOnly(1979, 1, 1)).Should().BeNull("vor der Geburt");
        PersonBirthDate.Create(null, null, null, 2026).Should().BeNull();

        PersonBirthDate.TryCreate(1980, null, 5, 2026, out _, out var dayWithoutMonth).Should().BeFalse();
        dayWithoutMonth.Should().Contain("Monat");
        PersonBirthDate.TryCreate(2023, 2, 29, 2026, out _, out var noLeap).Should().BeFalse();
        noLeap.Should().Contain("29.").And.Contain("Februar");
        PersonBirthDate.TryCreate(2027, null, null, 2026, out _, out _).Should().BeFalse("Zukunft");
        PersonBirthDate.TryCreate(1799, null, null, 2026, out _, out _).Should().BeFalse();
        PersonBirthDate.TryCreate(null, 5, null, 2026, out _, out _).Should().BeFalse("Monat ohne Jahr");
        PersonBirthDate.FromStored(1980, 13, null).Should().BeNull("kaputte Katalogwerte werden ignoriert");
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

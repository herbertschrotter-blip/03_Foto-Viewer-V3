using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Categories;

/// <summary>
/// Migration AddCategorySortIndex (ADR-0031, ADR-0005: Bestand bleibt): eine DB auf Stand AddCategories mit
/// Kategorien und Zuweisungen wird migriert — IX_FileCategoryValues_Category weicht dem abdeckenden Index
/// (CategoryId, ValueId, FileId), alle Zuweisungen bleiben.
/// </summary>
public sealed class CategorySortIndexMigrationTests
{
    [Fact]
    public async Task Bestands_DB_bekommt_den_Sortier_Index_und_behaelt_alle_Zuweisungen()
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
                await ctx.GetService<IMigrator>().MigrateAsync("AddCategories");
            }

            using (var connection = factory.OpenSqliteConnection())
            {
                Indexes(connection).Should().Contain("IX_FileCategoryValues_Category", "Ausgangsstand vor der Migration");
                Exec(connection, "INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'Fotos', 'D:\\Fotos', 1, 1);");
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt, RootId) VALUES (10, 1, 'D:\\Fotos', NULL, 'Fotos', 1, NULL);");
                for (var id = 100; id < 110; id++)
                {
                    Exec(connection,
                        "INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Missing) " +
                        $"VALUES ({id}, 1, 10, 'f{id}.jpg', '.jpg', 1, 1, {id}, 0, 0, 0);");
                }

                Exec(connection, "INSERT INTO Categories (Id, Name, SortOrder, CreatedAtUtc) VALUES (1, 'Projekt', 0, 1), (2, 'Kunde', 1, 1);");
                Exec(connection, "INSERT INTO CategoryValues (Id, CategoryId, Name, SortOrder) VALUES (1, 1, 'Nord', 0), (2, 1, 'Süd', 1), (3, 2, 'Meier', 0);");
                Exec(connection, "INSERT INTO FileCategoryValues (FileId, CategoryId, ValueId) SELECT Id, 1, 1 + Id % 2 FROM Files;");
                Exec(connection, "INSERT INTO FileCategoryValues (FileId, CategoryId, ValueId) SELECT Id, 2, 3 FROM Files WHERE Id % 3 = 0;");
            }

            var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            result.NewlyMigrated.Should().BeGreaterThan(0);

            using (var connection = factory.OpenSqliteConnection())
            {
                var indexes = Indexes(connection);
                indexes.Should().Contain("IX_FileCategoryValues_Category_Value_File");
                indexes.Should().NotContain("IX_FileCategoryValues_Category", "der abdeckende Index ersetzt ihn (gleiches Präfix)");
                indexes.Should().Contain("IX_FileCategoryValues_Value", "Fremdschlüssel ValueId braucht ihn weiter");
                Scalar(connection, "SELECT COUNT(*) FROM FileCategoryValues WHERE CategoryId = 1").Should().Be(10);
                Scalar(connection, "SELECT COUNT(*) FROM FileCategoryValues WHERE CategoryId = 2").Should().Be(3);
                Scalar(connection, "SELECT COUNT(*) FROM FileCategoryValues WHERE CategoryId = 1 AND ValueId = 2").Should().Be(5);
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

    private static List<string> Indexes(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'FileCategoryValues';";
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

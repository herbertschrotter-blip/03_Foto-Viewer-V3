using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Libraries;

/// <summary>
/// Migration AddLibraryRoots (ADR-0020, ADR-0005: Bestand bleibt): eine Alt-DB auf Stand InitialV01 mit Bibliothek,
/// Ordnern und Dateien wird migriert — RootPath wird zur Archiv-Wurzel, Folders.RootId per Pfad-Präfix gesetzt.
/// </summary>
public sealed class LibraryRootsMigrationTests
{
    [Fact]
    public async Task Alt_DB_bekommt_Archiv_Wurzel_und_RootId_fuer_bestehende_Ordner()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        try
        {
            var paths = new TestAppDataPaths(Path.Combine(rootDir, "appdata"));
            paths.EnsureDirectoriesExist();
            var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());

            // 1) Nur die erste Migration anwenden = Schema vor AddLibraryRoots
            await using (var ctx = factory.Create())
            {
                await ctx.GetService<IMigrator>().MigrateAsync("InitialV01");
            }

            // 2) Alt-Daten roh einfügen (LibraryRoots/RootId existieren noch nicht)
            var libRoot = @"D:\Fotos\Archiv";
            using (var connection = factory.OpenSqliteConnection())
            {
                Exec(connection, "INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (1, 'Fotos', $root, 1, 1);", ("$root", libRoot));
                Exec(connection, "INSERT INTO Libraries (Id, Name, RootPath, CreatedAt, LastOpenedAt) VALUES (2, 'Laufwerk', 'E:\\', 1, NULL);");
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (10, 1, $root, NULL, 'Archiv', 1);", ("$root", libRoot));
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (11, 1, $p, 10, '2024', 1);", ("$p", libRoot + @"\2024"));
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (12, 1, $p, 11, 'Familie', 1);", ("$p", libRoot + @"\2024\Familie"));
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (13, 1, $p, NULL, 'Archivx', 1);", ("$p", @"D:\Fotos\Archivx"));
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (20, 2, 'E:\\', NULL, 'E:\\', 1);");
                Exec(connection, "INSERT INTO Folders (Id, LibraryId, Path, ParentId, Name, ScannedAt) VALUES (21, 2, 'E:\\DCIM', 20, 'DCIM', 1);");
                Exec(connection,
                    "INSERT INTO Files (Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, IsFavorite, Missing) " +
                    "VALUES (100, 1, 12, 'a.jpg', '.jpg', 1, 1, 1, 0, 1, 0);");
            }

            // 3) Restliche Migrationen über den produktiven Initializer
            var result = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            result.NewlyMigrated.Should().BeGreaterThan(0);

            await using var verify = factory.Create();
            var roots = await verify.LibraryRoots.AsNoTracking().OrderBy(r => r.LibraryId).ToListAsync();
            roots.Should().HaveCount(2);
            roots[0].Should().BeEquivalentTo(new { LibraryId = 1, Path = libRoot, Role = LibraryRootRole.Archive, Name = "Archiv", SortOrder = 0 });
            roots[1].Should().BeEquivalentTo(new { LibraryId = 2, Path = @"E:\", Role = LibraryRootRole.Archive, Name = "E:" });

            var folders = await verify.Folders.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.RootId);
            folders[10].Should().Be(roots[0].Id, "Wurzelordner selbst");
            folders[11].Should().Be(roots[0].Id);
            folders[12].Should().Be(roots[0].Id);
            folders[13].Should().BeNull("„Archivx“ ist nur ein Präfix-Treffer ohne Trennzeichen, kein Unterordner");
            folders[20].Should().Be(roots[1].Id, "Laufwerkswurzel mit Trennzeichen");
            folders[21].Should().Be(roots[1].Id);

            (await verify.Files.CountAsync(f => f.IsFavorite)).Should().Be(1, "Bestandsdaten bleiben erhalten");
            (await verify.Libraries.SingleAsync(l => l.Id == 1)).RootPath.Should().Be(libRoot, "RootPath bleibt als Anzeigefeld");
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

    private static void Exec(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }
}

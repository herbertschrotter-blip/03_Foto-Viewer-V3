using Microsoft.Data.Sqlite;
using Lumora.Services.Infrastructure;

namespace Lumora.Tests.Database;

/// <summary>Einmaliger Umzug der App-Wurzel PhotoViewerV3 → Lumora beim Start (ADR-0056).</summary>
public sealed class LegacyAppDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));

    public LegacyAppDataMigrationTests() => Directory.CreateDirectory(_root);

    private string Legacy => Path.Combine(_root, "PhotoViewerV3");
    private string New => Path.Combine(_root, "Lumora");

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
    public void Ohne_alten_Ordner_passiert_nichts()
    {
        var result = LegacyAppDataMigration.Run(Legacy, New);

        result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.NothingToDo);
        result.Root.Should().Be(New);
        Directory.Exists(New).Should().BeFalse();
    }

    [Fact]
    public void Alter_Ordner_wird_samt_Inhalt_umbenannt()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "logs"));
        File.WriteAllText(Path.Combine(Legacy, "logs", "photoviewer-20261021.log"), "alt");
        Directory.CreateDirectory(Path.Combine(Legacy, "thumbs", "1"));
        File.WriteAllText(Path.Combine(Legacy, "thumbs", "1", "7_400_still.jpg"), "jpg");

        var result = LegacyAppDataMigration.Run(Legacy, New);

        result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.Moved);
        result.Root.Should().Be(New);
        Directory.Exists(Legacy).Should().BeFalse();
        File.ReadAllText(Path.Combine(New, "logs", "photoviewer-20261021.log")).Should().Be("alt");
        File.Exists(Path.Combine(New, "thumbs", "1", "7_400_still.jpg")).Should().BeTrue();
    }

    [Fact]
    public void Absolute_Vorschaubild_Pfade_zeigen_danach_in_den_neuen_Ordner()
    {
        Directory.CreateDirectory(Legacy);
        var db = Path.Combine(Legacy, AppDataPaths.DatabaseFileName);
        var inside = Path.Combine(Legacy, "thumbs", "1", "7_400_still.jpg");
        var outside = Path.Combine(_root, "Anderswo", "thumbs", "1", "8_400_still.jpg");
        Execute(db, "CREATE TABLE Thumbnails (FileId INTEGER, CachePath TEXT NOT NULL);");
        Execute(db, $"INSERT INTO Thumbnails VALUES (7, '{inside}'), (8, '{outside}');");

        var result = LegacyAppDataMigration.Run(Legacy, New);

        result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.Moved);
        result.RebasedThumbnails.Should().Be(1);
        ReadPaths(Path.Combine(New, AppDataPaths.DatabaseFileName)).Should().Equal(
            Path.Combine(New, "thumbs", "1", "7_400_still.jpg"),
            outside);
    }

    [Fact]
    public void Katalog_ohne_Thumbnails_Tabelle_wird_nicht_angefasst()
    {
        Directory.CreateDirectory(Legacy);
        Execute(Path.Combine(Legacy, AppDataPaths.DatabaseFileName), "CREATE TABLE Settings (Key TEXT, Value TEXT);");

        var result = LegacyAppDataMigration.Run(Legacy, New);

        result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.Moved);
        result.RebasedThumbnails.Should().Be(0);
        result.Detail.Should().BeNull();
    }

    [Fact]
    public void Speicherort_Datei_folgt_dem_neuen_Ordner_fremder_Datenordner_bleibt()
    {
        var custom = Path.Combine(_root, "Daten", "Katalog");
        Directory.CreateDirectory(custom);
        Directory.CreateDirectory(Legacy);
        DatabaseLocationStore.Save(Legacy, new DatabaseLocation(custom, [Path.Combine(Legacy, "library.db"), Path.Combine(_root, "alt.db")]));

        LegacyAppDataMigration.Run(Legacy, New).Outcome.Should().Be(LegacyAppDataMigrationOutcome.Moved);

        var location = DatabaseLocationStore.Load(New);
        location.DatabaseFolder.Should().Be(custom);
        location.PendingDeletes.Should().Equal(Path.Combine(New, "library.db"), Path.Combine(_root, "alt.db"));
    }

    [Fact]
    public void Datenordner_im_alten_Ordner_wird_umgeschrieben()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "Katalog"));
        DatabaseLocationStore.Save(Legacy, new DatabaseLocation(Path.Combine(Legacy, "Katalog"), []));

        LegacyAppDataMigration.Run(Legacy, New);

        DatabaseLocationStore.Load(New).DatabaseFolder.Should().Be(Path.Combine(New, "Katalog"));
    }

    [Fact]
    public void Gesperrte_Datei_laesst_den_alten_Ordner_aktiv()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "logs"));
        using (new FileStream(Path.Combine(Legacy, "logs", "offen.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var result = LegacyAppDataMigration.Run(Legacy, New);

            result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.KeptLegacy);
            result.Root.Should().Be(Legacy);
            result.Detail.Should().NotBeNullOrWhiteSpace();
        }

        Directory.Exists(Legacy).Should().BeTrue();
        Directory.Exists(New).Should().BeFalse();
    }

    [Fact]
    public void Leerer_neuer_Ordner_verhindert_den_Umzug_nicht()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "library.db"), "alt");
        Directory.CreateDirectory(New);

        var result = LegacyAppDataMigration.Run(Legacy, New);

        result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.Moved);
        File.ReadAllText(Path.Combine(New, "library.db")).Should().Be("alt");
        Directory.Exists(Legacy).Should().BeFalse();
        // Keine echte SQLite-Datei: das Umschreiben scheitert, der Umzug gilt trotzdem (Hinweis fürs Protokoll)
        result.Detail.Should().Contain("Vorschaubild-Pfade nicht umgeschrieben");
    }

    [Fact]
    public void Gibt_es_beide_Ordner_gilt_der_neue_und_der_alte_bleibt()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "library.db"), "alt");
        Directory.CreateDirectory(Path.Combine(New, "logs"));

        var result = LegacyAppDataMigration.Run(Legacy, New);

        result.Outcome.Should().Be(LegacyAppDataMigrationOutcome.BothExist);
        result.Root.Should().Be(New);
        File.ReadAllText(Path.Combine(Legacy, "library.db")).Should().Be("alt");
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> ReadPaths(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CachePath FROM Thumbnails ORDER BY FileId;";
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }
}

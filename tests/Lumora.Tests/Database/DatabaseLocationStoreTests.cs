using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Infrastructure;

namespace Lumora.Tests.Database;

/// <summary>Persistenz des gewählten Speicherorts (database-location.json, ADR-0026) und Aufräumen beim Start.</summary>
public sealed class DatabaseLocationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumora.Tests", Guid.NewGuid().ToString("N"));

    public DatabaseLocationStoreTests() => Directory.CreateDirectory(_root);

    private string AppRoot => Path.Combine(_root, "app");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Ohne_Datei_gilt_der_Standardort_in_der_App_Wurzel()
    {
        var paths = AppDataPaths.Resolve(AppRoot);

        paths.UsesCustomDataFolder.Should().BeFalse();
        paths.LibraryDatabasePath.Should().Be(Path.Combine(AppRoot, "library.db"));
        paths.ThumbnailsRoot.Should().Be(Path.Combine(AppRoot, "thumbs"));
        paths.LogsRoot.Should().Be(Path.Combine(AppRoot, "logs"));
    }

    [Fact]
    public void Gespeicherter_Ordner_bestimmt_DB_und_Thumbnails_Logs_bleiben_in_der_App_Wurzel()
    {
        var data = Path.Combine(_root, "Katalog");
        DatabaseLocationStore.Save(AppRoot, new DatabaseLocation(data, [Path.Combine(AppRoot, "library.db")]));

        var loaded = DatabaseLocationStore.Load(AppRoot);
        loaded.DatabaseFolder.Should().Be(data);
        loaded.PendingDeletes.Should().Equal(Path.Combine(AppRoot, "library.db"));

        var paths = AppDataPaths.Resolve(AppRoot);
        paths.UsesCustomDataFolder.Should().BeTrue();
        paths.LibraryDatabasePath.Should().Be(Path.Combine(data, "library.db"));
        paths.ThumbnailsRoot.Should().Be(Path.Combine(data, "thumbs"));
        paths.GetLibraryThumbnailsPath(3).Should().Be(Path.Combine(data, "thumbs", "3"));
        paths.LogsRoot.Should().Be(Path.Combine(AppRoot, "logs"));
    }

    [Fact]
    public void Standardort_ohne_offene_Loeschungen_entfernt_die_Datei()
    {
        DatabaseLocationStore.Save(AppRoot, new DatabaseLocation(Path.Combine(_root, "x"), []));
        File.Exists(DatabaseLocationStore.GetFilePath(AppRoot)).Should().BeTrue();

        DatabaseLocationStore.Save(AppRoot, DatabaseLocation.Default);

        File.Exists(DatabaseLocationStore.GetFilePath(AppRoot)).Should().BeFalse();
        DatabaseLocationStore.Load(AppRoot).Should().BeEquivalentTo(DatabaseLocation.Default);
    }

    [Fact]
    public void Beschaedigte_Datei_wirft_statt_still_auf_den_Standardort_zu_fallen()
    {
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(DatabaseLocationStore.GetFilePath(AppRoot), "{ kein json");

        var act = () => AppDataPaths.Resolve(AppRoot);

        act.Should().Throw<InvalidDataException>().WithMessage("*beschädigt*");
    }

    [Fact]
    public void Relativer_Ordner_in_der_Datei_wird_abgelehnt()
    {
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(DatabaseLocationStore.GetFilePath(AppRoot), """{ "DatabaseFolder": "Katalog" }""");

        var act = () => DatabaseLocationStore.Load(AppRoot);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Gewaehlter_Ordner_auf_fehlendem_Laufwerk_wird_beim_Start_nicht_angelegt()
    {
        var missing = Path.Combine(_root, "abgezogen", "Katalog");
        DatabaseLocationStore.Save(AppRoot, new DatabaseLocation(missing, []));

        var paths = AppDataPaths.Resolve(AppRoot);
        paths.EnsureDirectoriesExist();

        Directory.Exists(missing).Should().BeFalse("ein leerer Katalog am falschen Ort wäre schlimmer als ein Startfehler");
        Directory.Exists(paths.LogsRoot).Should().BeTrue();
    }

    [Fact]
    public async Task Offene_Loeschungen_entfernen_den_alten_Ort_aber_nie_den_aktiven()
    {
        var data = Path.Combine(_root, "neu");
        Directory.CreateDirectory(data);
        var paths = new AppDataPaths(AppRoot, data);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.LibraryDatabasePath, "aktiv");
        File.WriteAllText(Path.Combine(paths.ThumbnailsRoot, "a.jpg"), "aktiv");

        var oldDb = Path.Combine(AppRoot, "library.db");
        var oldWal = oldDb + "-wal";
        var oldThumbs = Path.Combine(AppRoot, "thumbs");
        File.WriteAllText(oldDb, "alt");
        File.WriteAllText(oldWal, "alt");
        Directory.CreateDirectory(Path.Combine(oldThumbs, "1"));
        File.WriteAllText(Path.Combine(oldThumbs, "1", "5_320_still.jpg"), "alt");

        DatabaseLocationStore.Save(AppRoot, new DatabaseLocation(data,
        [
            oldDb, oldWal, oldDb + "-shm", oldThumbs,
            paths.LibraryDatabasePath,      // aktive DB — darf nie gelöscht werden
            paths.ThumbnailsRoot,           // aktiver Cache
            data,                           // aktiver Datenordner
            "relativ\\pfad",               // nie anfassen
        ]));

        var removed = await DatabaseLocationStore.RunPendingDeletesAsync(paths, NullLogger.Instance, attempts: 1);

        removed.Should().Be(4); // db, wal, shm (fehlt → zählt als erledigt), thumbs
        File.Exists(oldDb).Should().BeFalse();
        File.Exists(oldWal).Should().BeFalse();
        Directory.Exists(oldThumbs).Should().BeFalse();
        File.Exists(paths.LibraryDatabasePath).Should().BeTrue();
        File.Exists(Path.Combine(paths.ThumbnailsRoot, "a.jpg")).Should().BeTrue();

        var after = DatabaseLocationStore.Load(AppRoot);
        after.DatabaseFolder.Should().Be(data);
        after.PendingDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Gesperrte_Datei_bleibt_vorgemerkt_fuer_den_naechsten_Start()
    {
        var paths = new AppDataPaths(AppRoot, Path.Combine(_root, "neu"));
        paths.EnsureDirectoriesExist();
        var oldDb = Path.Combine(AppRoot, "library.db");
        File.WriteAllText(oldDb, "alt");
        DatabaseLocationStore.Save(AppRoot, new DatabaseLocation(Path.Combine(_root, "neu"), [oldDb]));

        int removed;
        using (new FileStream(oldDb, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            removed = await DatabaseLocationStore.RunPendingDeletesAsync(paths, NullLogger.Instance, attempts: 2, retryDelay: TimeSpan.FromMilliseconds(10));
        }

        removed.Should().Be(0);
        DatabaseLocationStore.Load(AppRoot).PendingDeletes.Should().Equal(oldDb);

        (await DatabaseLocationStore.RunPendingDeletesAsync(paths, NullLogger.Instance, attempts: 1)).Should().Be(1);
        File.Exists(oldDb).Should().BeFalse();
    }
}

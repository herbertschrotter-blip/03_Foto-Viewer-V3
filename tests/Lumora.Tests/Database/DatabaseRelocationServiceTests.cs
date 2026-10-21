using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Database.Relocation;
using Lumora.Services.Infrastructure;
using Lumora.Services.Scanning;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Database;

/// <summary>
/// Umzug der Katalog-DB samt Thumbnail-Cache (ADR-0026): Kopie per Backup-API mit Prüfung, Umschalten erst danach,
/// Rücknahme bei Fehlern, vorhandener Katalog im Ziel (übernehmen / ersetzen), ungeeignete Ziele.
/// </summary>
public sealed class DatabaseRelocationServiceTests
{
    private sealed class Env : IDisposable
    {
        private Env(TempLibraryFixture fx, int libraryId)
        {
            Fx = fx;
            LibraryId = libraryId;
            Service = new DatabaseRelocationService(
                fx.Paths, fx.ContextFactory, Gate, Thumbnails, Watch, Storage, NullLogger<DatabaseRelocationService>.Instance);
        }

        public TempLibraryFixture Fx { get; }

        public int LibraryId { get; }

        public ScanGate Gate { get; } = new();

        public PausableThumbnailServiceFake Thumbnails { get; } = new();

        public WatchServiceFake Watch { get; } = new();

        public StorageProbeFake Storage { get; } = new();

        public DatabaseRelocationService Service { get; }

        public string Target => Path.Combine(Fx.RootDir, "neuer Ort");

        public string AppRoot => Fx.Paths.AppDataRoot;

        /// <summary>Bibliothek mit drei gescannten Dateien und zwei Vorschaubildern im Cache.</summary>
        public static async Task<Env> CreateAsync()
        {
            var fx = await TempLibraryFixture.CreateAsync();
            var lib = await fx.CreateLibraryAsync();
            fx.WriteLibraryFile("2024\\a.jpg");
            fx.WriteLibraryFile("2024\\b.jpg");
            fx.WriteLibraryFile("2025\\c.jpg");
            (await fx.ScanAsync(lib.Id)).FilesInserted.Should().Be(3);
            TempLibraryFixture.WriteFile(Path.Combine(fx.Paths.GetLibraryThumbnailsPath(lib.Id), "1_320_still.jpg"), 300);
            TempLibraryFixture.WriteFile(Path.Combine(fx.Paths.GetLibraryThumbnailsPath(lib.Id), "2_320_still.jpg"), 400);
            TempLibraryFixture.WriteFile(Path.Combine(fx.Paths.GetLibraryThumbnailsPath(lib.Id), "3_320_still.jpg.tmp"), 10);
            await using (var ctx = fx.CreateContext())
            {
                var fileId = await ctx.Files.OrderBy(f => f.Id).Select(f => f.Id).FirstAsync();
                ctx.Thumbnails.Add(new Lumora.Domain.Entities.Thumbnail
                {
                    FileId = fileId,
                    Size = 320,
                    Kind = Lumora.Domain.Enums.ThumbnailKind.Still,
                    Format = "jpg",
                    CachePath = Path.Combine(fx.Paths.GetLibraryThumbnailsPath(lib.Id), $"{fileId}_320_still.jpg"),
                    SourceModifiedAtUtc = DateTime.UtcNow,
                    GeneratedAtUtc = DateTime.UtcNow,
                    ByteSize = 300,
                    Status = Lumora.Domain.Enums.ThumbnailStatus.Generated,
                    AttemptCount = 1,
                });
                await ctx.SaveChangesAsync();
            }

            return new Env(fx, lib.Id);
        }

        /// <summary>„Prozessende“: gibt die nach Erfolg gehaltene Schreibsperre der alten DB frei.</summary>
        public void EndProcess() => Service.Dispose();

        public void Dispose()
        {
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            Fx.Dispose();
        }
    }

    private static async Task<long> CountFilesAsync(string databasePath)
    {
        var factory = new LibraryContextFactory(new AppDataPaths(Path.GetDirectoryName(databasePath)!), new SqlitePragmaInterceptor());
        await using var ctx = factory.Create();
        return await ctx.Files.LongCountAsync();
    }

    /// <summary>Fremder, gültiger Katalog (echte Migration) mit einer Bibliothek im Ordner <paramref name="folder"/>.</summary>
    private static async Task CreateForeignCatalogAsync(string folder)
    {
        var paths = new AppDataPaths(folder);
        paths.EnsureDirectoriesExist();
        var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await using (var ctx = factory.Create())
        {
            ctx.Libraries.Add(new Lumora.Domain.Entities.Library { Name = "Fremd", RootPath = Path.Combine(folder, "fotos"), CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        File.WriteAllText(Path.Combine(paths.ThumbnailsRoot, "fremd.jpg"), "fremd");
        SqliteConnection.ClearAllPools();
    }

    // ---- Umzug ---------------------------------------------------------------------------------

    [Fact]
    public async Task Umzug_kopiert_DB_und_Thumbnails_prueft_und_vermerkt_den_neuen_Ort()
    {
        using var env = await Env.CreateAsync();
        var oldDb = env.Fx.Paths.LibraryDatabasePath;
        var stages = new List<string>();

        var check = await env.Service.CheckTargetAsync(env.Target);
        check.State.Should().Be(RelocationTargetState.Ready);
        check.ThumbnailFiles.Should().Be(2, "zu kopieren sind nur fertige Vorschaubilder, keine .tmp-Dateien");

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move, new SyncProgress(p => stages.Add(p.Stage)));

        result.Success.Should().BeTrue(result.Error);
        result.DatabasePath.Should().Be(Path.Combine(env.Target, "library.db"));
        result.ThumbnailsCopied.Should().Be(2, ".tmp-Dateien (halb geschrieben) werden nicht kopiert");
        stages.Should().ContainInOrder("Datenbank kopieren", "Kopie prüfen", "Vorschaubilder kopieren", "Neuen Speicherort speichern");

        // Kopie vollständig und unabhängig vom Original lesbar
        (await CountFilesAsync(result.DatabasePath)).Should().Be(3);
        File.ReadAllBytes(Path.Combine(env.Target, "thumbs", env.LibraryId.ToString(), "2_320_still.jpg")).Should().HaveCount(400);
        File.Exists(Path.Combine(env.Target, "library.db.part")).Should().BeFalse();

        // Thumbnails.CachePath ist absolut — die Kopie zeigt auf den neuen Ordner
        await using (var conn = new SqliteConnection($"Data Source={result.DatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CachePath FROM Thumbnails";
            var cachePath = (string)(await cmd.ExecuteScalarAsync())!;
            cachePath.Should().StartWith(Path.Combine(env.Target, "thumbs") + Path.DirectorySeparatorChar);
            File.Exists(cachePath).Should().BeTrue();
        }

        // Neuer Ort gilt ab dem nächsten Start; alte Dateien sind zum Löschen vorgemerkt, aber noch da
        var location = DatabaseLocationStore.Load(env.AppRoot);
        location.DatabaseFolder.Should().Be(env.Target);
        location.PendingDeletes.Should().Contain([oldDb, oldDb + "-wal", oldDb + "-shm", env.Fx.Paths.ThumbnailsRoot]);
        File.Exists(oldDb).Should().BeTrue();

        // Bis zum Neustart bleibt alles ruhig: DB gesperrt, Bibliothek belegt, Überwachung und Thumbnails angehalten
        env.Fx.ContextFactory.IsSuspended.Should().BeTrue();
        var act = () => env.Fx.ContextFactory.Create();
        act.Should().Throw<DatabaseSuspendedException>();
        env.Gate.IsBusy(env.LibraryId).Should().BeTrue();
        env.Watch.IsRunning.Should().BeFalse();
        env.Thumbnails.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task Nach_dem_Neustart_arbeitet_die_App_mit_dem_neuen_Ort_und_raeumt_den_alten_auf()
    {
        using var env = await Env.CreateAsync();
        var oldDb = env.Fx.Paths.LibraryDatabasePath;
        var oldThumbs = env.Fx.Paths.ThumbnailsRoot;
        (await env.Service.RelocateAsync(env.Target, RelocationMode.Move)).Success.Should().BeTrue();
        env.EndProcess();
        SqliteConnection.ClearAllPools();

        // „Neustart“: Pfade neu auflösen wie App.xaml.cs
        var paths = AppDataPaths.Resolve(env.AppRoot);
        paths.LibraryDatabasePath.Should().Be(Path.Combine(env.Target, "library.db"));
        paths.ThumbnailsRoot.Should().Be(Path.Combine(env.Target, "thumbs"));

        var factory = new LibraryContextFactory(paths, new SqlitePragmaInterceptor());
        var init = await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        init.NewlyMigrated.Should().Be(0);
        await using (var ctx = factory.Create())
        {
            (await ctx.Files.CountAsync()).Should().Be(3);
            (await ctx.Libraries.SingleAsync()).Id.Should().Be(env.LibraryId);
        }

        SqliteConnection.ClearAllPools();
        var removed = await DatabaseLocationStore.RunPendingDeletesAsync(paths, NullLogger.Instance, attempts: 1);

        removed.Should().BeGreaterThanOrEqualTo(2);
        File.Exists(oldDb).Should().BeFalse();
        Directory.Exists(oldThumbs).Should().BeFalse();
        File.Exists(paths.LibraryDatabasePath).Should().BeTrue();
        DatabaseLocationStore.Load(env.AppRoot).PendingDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Zurueck_zum_Standardort_entfernt_den_eigenen_Ort_aus_der_Datei()
    {
        using var env = await Env.CreateAsync();
        (await env.Service.RelocateAsync(env.Target, RelocationMode.Move)).Success.Should().BeTrue();
        env.EndProcess();
        SqliteConnection.ClearAllPools();
        var moved = AppDataPaths.Resolve(env.AppRoot);
        await DatabaseLocationStore.RunPendingDeletesAsync(moved, NullLogger.Instance, attempts: 1);

        var factory = new LibraryContextFactory(moved, new SqlitePragmaInterceptor());
        using var back = new DatabaseRelocationService(
            moved, factory, new ScanGate(), new PausableThumbnailServiceFake(), new WatchServiceFake(), new StorageProbeFake(),
            NullLogger<DatabaseRelocationService>.Instance);
        back.DefaultFolder.Should().Be(env.AppRoot);

        var result = await back.RelocateAsync(back.DefaultFolder, RelocationMode.Move);

        result.Success.Should().BeTrue(result.Error);
        var location = DatabaseLocationStore.Load(env.AppRoot);
        location.DatabaseFolder.Should().BeNull("Standardort = App-Wurzel");
        location.PendingDeletes.Should().Contain(Path.Combine(env.Target, "library.db"));
        AppDataPaths.Resolve(env.AppRoot).UsesCustomDataFolder.Should().BeFalse();
        (await CountFilesAsync(Path.Combine(env.AppRoot, "library.db"))).Should().Be(3);
    }

    // ---- Fehler und Rücknahme -------------------------------------------------------------------

    [Fact]
    public async Task Abbruch_beim_Kopieren_der_Thumbnails_nimmt_alles_zurueck()
    {
        using var env = await Env.CreateAsync();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Stage == "Vorschaubilder kopieren")
            {
                cts.Cancel();
            }
        });

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move, progress, cts.Token);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("abgebrochen");
        result.DatabasePath.Should().Be(env.Fx.Paths.LibraryDatabasePath);
        AssertRolledBack(env);
    }

    [Fact]
    public async Task Fehler_beim_Speichern_des_Orts_nimmt_die_Kopie_zurueck_und_der_alte_Ort_bleibt_aktiv()
    {
        using var env = await Env.CreateAsync();
        // Die Temp-Datei der atomaren Speicherung ist ein Ordner → Schreiben scheitert als letzter Schritt
        Directory.CreateDirectory(DatabaseLocationStore.GetFilePath(env.AppRoot) + ".tmp");

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("nicht gespeichert");
        AssertRolledBack(env);
        Directory.Delete(DatabaseLocationStore.GetFilePath(env.AppRoot) + ".tmp");

        // Danach weiter normal nutzbar — auch ein zweiter Versuch gelingt
        await using (var ctx = env.Fx.CreateContext())
        {
            (await ctx.Files.CountAsync()).Should().Be(3);
        }

        (await env.Service.RelocateAsync(env.Target, RelocationMode.Move)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task Laufender_Scan_ueber_die_Wartezeit_hinaus_bricht_ohne_Spuren_ab()
    {
        using var env = await Env.CreateAsync();
        env.Service.QuiesceTimeout = TimeSpan.FromMilliseconds(200);
        using var runningScan = await env.Gate.AcquireAsync(env.LibraryId, CancellationToken.None);

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Scan");
        Directory.Exists(Path.Combine(env.Target, "thumbs")).Should().BeFalse();
        File.Exists(Path.Combine(env.Target, "library.db")).Should().BeFalse();
        env.Watch.IsRunning.Should().BeTrue("die Überwachung läuft nach der Rücknahme wieder");
        env.Thumbnails.PauseCalls.Should().Be(0);
        env.Fx.ContextFactory.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Umzug_wartet_auf_einen_kurzen_Datei_Batch()
    {
        using var env = await Env.CreateAsync();
        var batch = await env.Gate.AcquireForFileOperationAsync(env.LibraryId, CancellationToken.None);

        var relocation = env.Service.RelocateAsync(env.Target, RelocationMode.Move);
        await Task.Delay(150);
        relocation.IsCompleted.Should().BeFalse("der Batch hält die Bibliothek noch");
        File.Exists(Path.Combine(env.Target, "library.db")).Should().BeFalse();

        batch.Dispose();
        (await relocation).Success.Should().BeTrue();
    }

    // ---- Schreiber, die vor der Sperre angefangen haben ------------------------------------------

    private static long CountFavorites(string databasePath)
    {
        using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM Files WHERE IsFavorite = 1 OR Rating IS NOT NULL;";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Kontext_von_vor_der_Sperre_schreibt_waehrend_und_nach_dem_Umzug_nicht_in_die_alte_DB()
    {
        using var env = await Env.CreateAsync();
        var oldDb = env.Fx.Paths.LibraryDatabasePath;

        // Favorit „im selben Augenblick“: Kontext und Entität geladen, bevor der Umzug sperrt, gespeichert erst danach
        await using var favorite = env.Fx.CreateContext();
        var file = await favorite.Files.OrderBy(f => f.Id).FirstAsync();
        file.IsFavorite = true;

        // Kontext mit schon offener Verbindung (Non-Query) und rohe Verbindung von vor der Sperre
        await using var openCtx = env.Fx.CreateContext();
        await openCtx.Database.OpenConnectionAsync();
        using var raw = env.Fx.ContextFactory.OpenSqliteConnection();
        using (var fast = raw.CreateCommand())
        {
            fast.CommandText = "PRAGMA busy_timeout=100;";
            fast.ExecuteNonQuery();
        }

        Exception? saveDuring = null;
        Exception? nonQueryDuring = null;
        Exception? rawDuring = null;
        var progress = new SyncProgress(p =>
        {
            if (p.Stage != "Datenbank kopieren")
            {
                return;
            }

            saveDuring = Record.Exception(() => favorite.SaveChanges());
            nonQueryDuring = Record.Exception(() => openCtx.Database.ExecuteSqlRaw("UPDATE Files SET Rating = 5;"));
            rawDuring = Record.Exception(() =>
            {
                using var cmd = raw.CreateCommand();
                cmd.CommandTimeout = 1;
                cmd.CommandText = "UPDATE Files SET Rating = 4;";
                cmd.ExecuteNonQuery();
            });
        });

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move, progress);

        result.Success.Should().BeTrue(result.Error);
        saveDuring.Should().BeOfType<DatabaseSuspendedException>("SaveChanges eines älteren Kontexts wird abgewiesen");
        nonQueryDuring.Should().BeOfType<DatabaseSuspendedException>();
        rawDuring.Should().BeOfType<SqliteException>("die gehaltene Schreibsperre hält auch rohe Verbindungen fern")
            .Which.SqliteErrorCode.Should().Be(5, "SQLITE_BUSY");

        // Nach dem Umschalten (bis zum Neustart) ebenso
        var saveAfter = async () => await favorite.SaveChangesAsync();
        await saveAfter.Should().ThrowAsync<DatabaseSuspendedException>();
        var rawAfter = () =>
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandTimeout = 1;
            cmd.CommandText = "UPDATE Files SET IsFavorite = 1;";
            cmd.ExecuteNonQuery();
        };
        rawAfter.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(5);

        // Lesen bleibt erlaubt — es verändert die alte DB nicht
        (await favorite.Files.AsNoTracking().CountAsync()).Should().Be(3);

        CountFavorites(oldDb).Should().Be(0, "die alte DB bleibt unverändert");
        CountFavorites(result.DatabasePath).Should().Be(0, "die Kopie entspricht dem Stand beim Sperren");
    }

    [Fact]
    public async Task Nach_der_Ruecknahme_speichert_ein_Kontext_von_vor_der_Sperre_wieder_normal()
    {
        using var env = await Env.CreateAsync();
        await using var favorite = env.Fx.CreateContext();
        var file = await favorite.Files.OrderBy(f => f.Id).FirstAsync();
        file.IsFavorite = true;
        using var cts = new CancellationTokenSource();
        Exception? saveDuring = null;
        var progress = new SyncProgress(p =>
        {
            if (p.Stage == "Kopie prüfen")
            {
                saveDuring = Record.Exception(() => favorite.SaveChanges());
                cts.Cancel();
            }
        });

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move, progress, cts.Token);

        result.Success.Should().BeFalse();
        saveDuring.Should().BeOfType<DatabaseSuspendedException>();
        AssertRolledBack(env);

        // Sperre und Schreibsperre sind aufgehoben: der geänderte Favorit landet in der (weiter aktiven) alten DB
        (await favorite.SaveChangesAsync()).Should().Be(1);
        CountFavorites(env.Fx.Paths.LibraryDatabasePath).Should().Be(1);
    }

    // ---- Thumbnail-Ordner: ein Durchlauf -----------------------------------------------------------

    /// <summary>Legt ein Vorschaubild samt Thumbnails-Zeile an, wie es der Thumbnail-Dienst nach der Prüfung täte.</summary>
    private static async Task<string> AddGeneratedThumbnailAsync(Env env, int fileId, int bytes)
    {
        var path = Path.Combine(env.Fx.Paths.GetLibraryThumbnailsPath(env.LibraryId), $"{fileId}_320_still.jpg");
        TempLibraryFixture.WriteFile(path, bytes);
        await using var ctx = env.Fx.CreateContext();
        ctx.Thumbnails.Add(new Lumora.Domain.Entities.Thumbnail
        {
            FileId = fileId,
            Size = 320,
            Kind = Lumora.Domain.Enums.ThumbnailKind.Still,
            Format = "jpg",
            CachePath = path,
            SourceModifiedAtUtc = DateTime.UtcNow,
            GeneratedAtUtc = DateTime.UtcNow,
            ByteSize = bytes,
            Status = Lumora.Domain.Enums.ThumbnailStatus.Generated,
            AttemptCount = 1,
        });
        await ctx.SaveChangesAsync();
        return path;
    }

    [Fact]
    public async Task Umzug_kopiert_nach_der_Dateiliste_der_Pruefung_und_ergaenzt_seither_erzeugte_Vorschaubilder()
    {
        using var env = await Env.CreateAsync();
        var check = await env.Service.CheckTargetAsync(env.Target);
        check.ThumbnailFiles.Should().Be(2);

        // Nach der Prüfung: ein neues Vorschaubild des Dienstes (mit DB-Zeile) und eine fremde Datei ohne Zeile
        int thirdId;
        await using (var ctx = env.Fx.CreateContext())
        {
            thirdId = await ctx.Files.OrderBy(f => f.Id).Skip(2).Select(f => f.Id).FirstAsync();
        }

        await AddGeneratedThumbnailAsync(env, thirdId, 500);
        var stray = Path.Combine(env.Fx.Paths.GetLibraryThumbnailsPath(env.LibraryId), "fremd.jpg");
        TempLibraryFixture.WriteFile(stray, 50);
        long total = 0;

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move, new SyncProgress(p =>
        {
            if (p.Stage == "Vorschaubilder kopieren")
            {
                total = p.Total;
            }
        }));

        result.Success.Should().BeTrue(result.Error);
        result.ThumbnailsCopied.Should().Be(3, "Liste der Prüfung plus das seither erzeugte Vorschaubild");
        total.Should().Be(3);
        File.ReadAllBytes(Path.Combine(env.Target, "thumbs", env.LibraryId.ToString(), $"{thirdId}_320_still.jpg")).Should().HaveCount(500);
        File.Exists(Path.Combine(env.Target, "thumbs", env.LibraryId.ToString(), "fremd.jpg")).Should()
            .BeFalse("kein zweiter Durchlauf durch den Ordner — die Liste der Prüfung gilt");
    }

    [Fact]
    public async Task Platz_wird_nach_dem_Abgleich_mit_der_endgueltigen_Liste_erneut_geprueft()
    {
        using var env = await Env.CreateAsync();
        var check = await env.Service.CheckTargetAsync(env.Target);
        check.State.Should().Be(RelocationTargetState.Ready);
        env.Storage.AvailableBytes = check.RequiredBytes + 1024 * 1024; // reicht für die Liste der Prüfung …

        int thirdId;
        await using (var ctx = env.Fx.CreateContext())
        {
            thirdId = await ctx.Files.OrderBy(f => f.Id).Skip(2).Select(f => f.Id).FirstAsync();
        }

        await AddGeneratedThumbnailAsync(env, thirdId, 4 * 1024 * 1024); // … aber nicht mehr für das neue Vorschaubild

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Nicht genug Platz");
        AssertRolledBack(env);
    }

    [Fact]
    public async Task Umzug_ohne_vorherige_Pruefung_durchlaeuft_den_Ordner_selbst()
    {
        using var env = await Env.CreateAsync();

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move);

        result.Success.Should().BeTrue(result.Error);
        result.ThumbnailsCopied.Should().Be(2);
    }

    private static void AssertRolledBack(Env env)
    {
        File.Exists(Path.Combine(env.Target, "library.db")).Should().BeFalse();
        File.Exists(Path.Combine(env.Target, "library.db.part")).Should().BeFalse();
        Directory.Exists(Path.Combine(env.Target, "thumbs")).Should().BeFalse();
        File.Exists(DatabaseLocationStore.GetFilePath(env.AppRoot)).Should().BeFalse();
        AppDataPaths.Resolve(env.AppRoot).LibraryDatabasePath.Should().Be(env.Fx.Paths.LibraryDatabasePath);

        env.Fx.ContextFactory.IsSuspended.Should().BeFalse();
        env.Gate.IsBusy(env.LibraryId).Should().BeFalse();
        env.Thumbnails.IsPaused.Should().BeFalse();
        env.Watch.IsRunning.Should().BeTrue();
        File.Exists(env.Fx.Paths.LibraryDatabasePath).Should().BeTrue();
    }

    // ---- Belegtes Ziel ---------------------------------------------------------------------------

    [Fact]
    public async Task Vorhandener_Katalog_im_Ziel_wird_erkannt_und_blockiert_den_einfachen_Umzug()
    {
        using var env = await Env.CreateAsync();
        await CreateForeignCatalogAsync(env.Target);

        var check = await env.Service.CheckTargetAsync(env.Target);

        check.State.Should().Be(RelocationTargetState.ExistingData);
        check.Existing.Should().NotBeNull();
        check.Existing!.HasDatabase.Should().BeTrue();
        check.Existing.IsValidCatalog.Should().BeTrue();
        check.Existing.Libraries.Should().Be(1);
        check.Existing.Files.Should().Be(0);

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("bereits ein Katalog");
        (await CountFilesAsync(Path.Combine(env.Target, "library.db"))).Should().Be(0, "der fremde Katalog bleibt unverändert");
    }

    [Fact]
    public async Task Uebernehmen_schaltet_auf_den_vorhandenen_Katalog_ohne_den_bisherigen_zu_loeschen()
    {
        using var env = await Env.CreateAsync();
        await CreateForeignCatalogAsync(env.Target);
        var oldDb = env.Fx.Paths.LibraryDatabasePath;

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.AdoptExisting);

        result.Success.Should().BeTrue(result.Error);
        result.ThumbnailsCopied.Should().Be(0);
        var location = DatabaseLocationStore.Load(env.AppRoot);
        location.DatabaseFolder.Should().Be(env.Target);
        location.PendingDeletes.Should().NotContain(oldDb, "der bisherige Katalog wird nicht kopiert und bleibt erhalten");
        location.PendingDeletes.Should().NotContain(env.Fx.Paths.ThumbnailsRoot);
        File.Exists(Path.Combine(env.Target, "thumbs", "fremd.jpg")).Should().BeTrue();
        SqliteConnection.ClearAllPools();
        (await CountFilesAsync(Path.Combine(env.Target, "library.db"))).Should().Be(0);
    }

    [Fact]
    public async Task Ersetzen_legt_den_vorhandenen_Katalog_beiseite_und_kopiert_den_aktuellen()
    {
        using var env = await Env.CreateAsync();
        await CreateForeignCatalogAsync(env.Target);

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.ReplaceExisting);

        result.Success.Should().BeTrue(result.Error);
        SqliteConnection.ClearAllPools();
        (await CountFilesAsync(Path.Combine(env.Target, "library.db"))).Should().Be(3);
        File.Exists(Path.Combine(env.Target, "thumbs", "fremd.jpg")).Should().BeFalse("fremde Vorschaubilder gehören zu anderen FileIds");

        var aside = Directory.GetFiles(env.Target, "library.db.ersetzt-*");
        aside.Should().ContainSingle();
        var asideThumbs = Directory.GetDirectories(env.Target, "thumbs.ersetzt-*");
        asideThumbs.Should().ContainSingle();
        File.Exists(Path.Combine(asideThumbs[0], "fremd.jpg")).Should().BeTrue();
        DatabaseLocationStore.Load(env.AppRoot).PendingDeletes.Should().Contain([aside[0], asideThumbs[0]]);
    }

    [Fact]
    public async Task Fehler_nach_dem_Beiseitelegen_stellt_den_fremden_Katalog_wieder_her()
    {
        using var env = await Env.CreateAsync();
        await CreateForeignCatalogAsync(env.Target);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Stage == "Kopie prüfen")
            {
                cts.Cancel();
            }
        });

        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.ReplaceExisting, progress, cts.Token);

        result.Success.Should().BeFalse();
        Directory.GetFiles(env.Target, "*.ersetzt-*").Should().BeEmpty();
        Directory.GetDirectories(env.Target, "*.ersetzt-*").Should().BeEmpty();
        File.Exists(Path.Combine(env.Target, "thumbs", "fremd.jpg")).Should().BeTrue();
        SqliteConnection.ClearAllPools();
        (await CountFilesAsync(Path.Combine(env.Target, "library.db"))).Should().Be(0, "der fremde Katalog ist wieder an seinem Platz");
        env.Fx.ContextFactory.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Kaputte_Datei_im_Ziel_kann_nicht_uebernommen_aber_ersetzt_werden()
    {
        using var env = await Env.CreateAsync();
        Directory.CreateDirectory(env.Target);
        File.WriteAllText(Path.Combine(env.Target, "library.db"), "das ist keine SQLite-Datei, sondern Text mit genug Länge für den Header-Test");

        var check = await env.Service.CheckTargetAsync(env.Target);
        check.State.Should().Be(RelocationTargetState.ExistingData);
        check.Existing!.IsValidCatalog.Should().BeFalse();
        check.Existing.Problem.Should().NotBeNullOrEmpty();

        var adopt = await env.Service.RelocateAsync(env.Target, RelocationMode.AdoptExisting);
        adopt.Success.Should().BeFalse();
        env.Fx.ContextFactory.IsSuspended.Should().BeFalse();

        (await env.Service.RelocateAsync(env.Target, RelocationMode.ReplaceExisting)).Success.Should().BeTrue();
    }

    // ---- Ungeeignete Ziele ------------------------------------------------------------------------

    [Fact]
    public async Task Ungeeignete_Ziele_werden_mit_Begruendung_abgelehnt()
    {
        using var env = await Env.CreateAsync();

        (await env.Service.CheckTargetAsync(env.Fx.Paths.AppDataRoot)).Problem.Should().Contain("bereits in diesem Ordner");
        (await env.Service.CheckTargetAsync(Path.Combine(env.Fx.Paths.ThumbnailsRoot, "x"))).State.Should().Be(RelocationTargetState.Invalid);
        (await env.Service.CheckTargetAsync(Path.Combine(env.Fx.LibraryRoot, "Katalog"))).Problem.Should().Contain("Bibliothekswurzel");
        (await env.Service.CheckTargetAsync("relativ")).State.Should().Be(RelocationTargetState.Invalid);

        env.Storage.IsNetwork = true;
        (await env.Service.CheckTargetAsync(env.Target)).Problem.Should().Contain("Netzlaufwerk");
        env.Storage.IsNetwork = false;

        env.Storage.AvailableBytes = 1024;
        var full = await env.Service.CheckTargetAsync(env.Target);
        full.State.Should().Be(RelocationTargetState.Invalid);
        full.Problem.Should().Contain("Nicht genug Platz");
        var result = await env.Service.RelocateAsync(env.Target, RelocationMode.Move);
        result.Success.Should().BeFalse();
        env.Watch.StopCalls.Should().Be(0, "ein ungeeignetes Ziel hält nichts an");
    }

    [Fact]
    public async Task OneDrive_Ordner_ist_erlaubt_aber_markiert()
    {
        using var env = await Env.CreateAsync();
        env.Storage.IsCloud = true;

        var check = await env.Service.CheckTargetAsync(env.Target);

        check.State.Should().Be(RelocationTargetState.Ready);
        check.IsCloudSynced.Should().BeTrue();
    }

    /// <summary><see cref="Progress{T}"/> meldet asynchron über den SynchronizationContext — für Tests synchron.</summary>
    private sealed class SyncProgress(Action<RelocationProgress> report) : IProgress<RelocationProgress>
    {
        public void Report(RelocationProgress value) => report(value);
    }
}

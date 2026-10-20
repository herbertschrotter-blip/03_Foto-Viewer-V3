using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Infrastructure;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Thumbnails;
using PhotoViewerV3.Services.Watch;

namespace PhotoViewerV3.Services.Database.Relocation;

/// <summary>
/// Umzug von Katalog-DB und Thumbnail-Cache (ADR-0026). Umschalten erst nach geprüfter Kopie; der neue Ort gilt ab dem
/// nächsten Start (kein Umschalten zur Laufzeit), die alten Dateien löscht der nächste Start.
/// </summary>
public sealed class DatabaseRelocationService : IDatabaseRelocationService
{
    /// <summary>Reserve über dem reinen Platzbedarf: 10 % plus 50 MB.</summary>
    private const long SpaceReserveBytes = 50L * 1024 * 1024;

    private const string PartSuffix = ".part";
    private const string WriteProbePrefix = ".photoviewer-schreibtest-";

    private readonly IAppDataPaths _paths;
    private readonly ILibraryContextFactory _factory;
    private readonly IScanGate _scanGate;
    private readonly IThumbnailService _thumbnails;
    private readonly ILibraryWatchService _watch;
    private readonly IStorageProbe _storage;
    private readonly ILogger<DatabaseRelocationService> _logger;
    private int _running;

    public DatabaseRelocationService(
        IAppDataPaths paths,
        ILibraryContextFactory factory,
        IScanGate scanGate,
        IThumbnailService thumbnails,
        ILibraryWatchService watch,
        IStorageProbe storage,
        ILogger<DatabaseRelocationService> logger)
    {
        _paths = paths;
        _factory = factory;
        _scanGate = scanGate;
        _thumbnails = thumbnails;
        _watch = watch;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>Höchstens so lange auf laufende Scans, Datei-Batches und Thumbnail-Aufträge warten.</summary>
    public TimeSpan QuiesceTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string CurrentFolder => Path.GetDirectoryName(_paths.LibraryDatabasePath)!;

    public string DefaultFolder => _paths.AppDataRoot;

    // ---- Prüfen ------------------------------------------------------------------------------------

    public async Task<RelocationTargetCheck> CheckTargetAsync(string targetFolder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || !Path.IsPathFullyQualified(targetFolder))
        {
            return Invalid(targetFolder, "Bitte einen vollständigen Ordnerpfad wählen.");
        }

        string target;
        try
        {
            target = AppDataPaths.NormalizeFolder(targetFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid(targetFolder, $"Der Pfad ist ungültig: {ex.Message}");
        }

        if (AppDataPaths.PathsEqual(target, CurrentFolder))
        {
            return Invalid(target, "Katalog und Vorschaubilder liegen bereits in diesem Ordner.");
        }

        if (_storage.IsNetworkLocation(target))
        {
            return Invalid(target,
                "Netzlaufwerke und Freigaben (\\\\Server\\…) eignen sich nicht: SQLite braucht für den Katalog ein lokales " +
                "Laufwerk (interne Platte, SSD oder USB-Laufwerk).");
        }

        if (AppDataPaths.IsSameOrInside(target, _paths.ThumbnailsRoot) || AppDataPaths.IsSameOrInside(target, _paths.LogsRoot))
        {
            return Invalid(target, "Der Zielordner liegt im bisherigen Vorschaubild- oder Protokollordner.");
        }

        var libraryRoot = await FindEnclosingLibraryRootAsync(target, ct).ConfigureAwait(false);
        if (libraryRoot is not null)
        {
            return Invalid(target,
                $"Der Zielordner liegt in der Bibliothekswurzel „{libraryRoot}“ — die Vorschaubilder würden dort als " +
                "Fotos eingelesen. Bitte einen Ordner außerhalb der Bibliotheken wählen.");
        }

        var writeProblem = ProbeWritable(target);
        if (writeProblem is not null)
        {
            return Invalid(target, writeProblem);
        }

        var targetDb = Path.Combine(target, AppDataPaths.DatabaseFileName);
        var targetThumbs = Path.Combine(target, AppDataPaths.ThumbnailsFolderName);
        var (thumbCount, thumbBytes, existing) = await Task.Run(() =>
        {
            var (count, bytes) = MeasureDirectory(_paths.ThumbnailsRoot);
            var info = File.Exists(targetDb) || HasAnyFile(targetThumbs) ? InspectExisting(targetDb) : null;
            return (count, bytes, info);
        }, ct).ConfigureAwait(false);

        var dbBytes = FileLength(_paths.LibraryDatabasePath) + FileLength(_paths.LibraryDatabasePath + "-wal");
        var required = (long)((dbBytes + thumbBytes) * 1.1) + SpaceReserveBytes;
        var available = _storage.GetAvailableFreeSpace(target);
        var enough = available is null || available.Value >= required;
        var spaceProblem = enough
            ? null
            : $"Nicht genug Platz im Ziel: benötigt etwa {FormatBytes(required)}, frei sind {FormatBytes(available!.Value)}.";
        var cloud = _storage.IsCloudSynced(target);

        if (existing is not null)
        {
            return new RelocationTargetCheck(RelocationTargetState.ExistingData, target, spaceProblem, required, available, enough, cloud, thumbCount, existing);
        }

        return enough
            ? new RelocationTargetCheck(RelocationTargetState.Ready, target, null, required, available, true, cloud, thumbCount, null)
            : new RelocationTargetCheck(RelocationTargetState.Invalid, target, spaceProblem, required, available, false, cloud, thumbCount, null);
    }

    private static RelocationTargetCheck Invalid(string target, string problem)
        => new(RelocationTargetState.Invalid, target, problem, 0, null, false, false, 0, null);

    private async Task<string?> FindEnclosingLibraryRootAsync(string target, CancellationToken ct)
    {
        await using var ctx = _factory.Create();
        var roots = await ctx.LibraryRoots.AsNoTracking().Select(r => r.Path).ToListAsync(ct).ConfigureAwait(false);
        roots.AddRange(await ctx.Libraries.AsNoTracking().Select(l => l.RootPath).ToListAsync(ct).ConfigureAwait(false));
        return roots.FirstOrDefault(root => !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && AppDataPaths.IsSameOrInside(target, root));
    }

    private static string? ProbeWritable(string target)
    {
        var probe = Path.Combine(target, WriteProbePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        var created = !Directory.Exists(target);
        try
        {
            Directory.CreateDirectory(target);
            File.WriteAllBytes(probe, [0x50, 0x56]);
            File.Delete(probe);
            if (created)
            {
                Directory.Delete(target); // die Prüfung hinterlässt nichts; der Umzug legt den Ordner selbst an
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"In den Zielordner kann nicht geschrieben werden: {ex.Message}";
        }
    }

    private static ExistingCatalogInfo InspectExisting(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return new ExistingCatalogInfo(false, false, 0, 0, null, "Im Zielordner liegt nur ein Vorschaubild-Ordner ohne Katalog.");
        }

        var modified = File.GetLastWriteTimeUtc(databasePath);
        try
        {
            using var connection = OpenRaw(databasePath, SqliteOpenMode.ReadWrite);
            var check = ExecuteScalarText(connection, "PRAGMA quick_check;");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new ExistingCatalogInfo(true, false, 0, 0, modified, $"Die Datenbank ist beschädigt ({check}).");
            }

            var tables = ReadTableNames(connection);
            if (!tables.Contains("Libraries") || !tables.Contains("Files") || !tables.Contains("__EFMigrationsHistory"))
            {
                return new ExistingCatalogInfo(true, false, 0, 0, modified, "Die Datei ist kein PhotoViewer-Katalog.");
            }

            var libraries = Convert.ToInt32(ExecuteScalar(connection, "SELECT count(*) FROM Libraries;"));
            var files = Convert.ToInt64(ExecuteScalar(connection, "SELECT count(*) FROM Files;"));
            return new ExistingCatalogInfo(true, true, libraries, files, modified, null);
        }
        catch (SqliteException ex)
        {
            return new ExistingCatalogInfo(true, false, 0, 0, modified, $"Die Datenbank ist nicht lesbar: {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    // ---- Umziehen ----------------------------------------------------------------------------------

    public async Task<RelocationResult> RelocateAsync(
        string targetFolder,
        RelocationMode mode,
        IProgress<RelocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return new RelocationResult(false, "Ein Umzug läuft bereits.", _paths.LibraryDatabasePath);
        }

        try
        {
            return await RelocateCoreAsync(targetFolder, mode, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<RelocationResult> RelocateCoreAsync(
        string targetFolder,
        RelocationMode mode,
        IProgress<RelocationProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new RelocationProgress("Zielordner prüfen"));
        RelocationTargetCheck check;
        try
        {
            check = await CheckTargetAsync(targetFolder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB-Umzug: Zielordner {Target} konnte nicht geprüft werden", targetFolder);
            return Failed(ex is OperationCanceledException ? "Der Umzug wurde abgebrochen." : $"Der Zielordner konnte nicht geprüft werden: {ex.Message}");
        }

        var modeProblem = ValidateMode(check, mode);
        if (modeProblem is not null)
        {
            return Failed(modeProblem);
        }

        var target = check.TargetFolder;
        var oldDb = _paths.LibraryDatabasePath;
        var oldThumbs = _paths.ThumbnailsRoot;
        var targetDb = Path.Combine(target, AppDataPaths.DatabaseFileName);
        var targetThumbs = Path.Combine(target, AppDataPaths.ThumbnailsFolderName);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        _logger.LogInformation("DB-Umzug ({Mode}): {Old} → {Target}", mode, CurrentFolder, target);

        var rollback = new Stack<(string What, Action Undo)>();
        var pendingDeletes = new List<string>();
        Quiescence? quiet = null;
        var copied = 0;
        var skipped = 0;
        try
        {
            progress?.Report(new RelocationProgress("Laufende Scans, Datei-Operationen und Vorschaubilder abwarten"));
            quiet = await QuiesceAsync(oldDb, ct).ConfigureAwait(false);

            if (mode == RelocationMode.AdoptExisting)
            {
                progress?.Report(new RelocationProgress("Vorhandenen Katalog prüfen"));
                await Task.Run(() => VerifyCatalog(targetDb, expectedCounts: null), ct).ConfigureAwait(false);
            }
            else
            {
                // Ersetzen: vorhandenen Katalog beiseitelegen. Auch beim Umzug in einen „leeren“ Ordner: verwaiste
                // -wal/-shm einer früheren library.db würde SQLite sonst auf die Kopie anwenden.
                var asideCandidates = mode == RelocationMode.ReplaceExisting
                    ? new[] { targetDb, targetDb + "-wal", targetDb + "-shm", targetThumbs }
                    : new[] { targetDb + "-wal", targetDb + "-shm" };
                foreach (var path in asideCandidates)
                {
                    var aside = MoveAside(path, stamp, rollback);
                    if (aside is not null)
                    {
                        pendingDeletes.Add(aside);
                    }
                }

                (copied, skipped) = await Task.Run(
                    () => CopyCatalog(oldDb, oldThumbs, targetDb, targetThumbs, check.ThumbnailFiles, rollback, progress, ct),
                    ct).ConfigureAwait(false);

                pendingDeletes.AddRange([oldDb, oldDb + "-wal", oldDb + "-shm", oldThumbs]);
            }

            progress?.Report(new RelocationProgress("Neuen Speicherort speichern"));
            SaveLocation(target, pendingDeletes);

            _logger.LogInformation(
                "DB-Umzug abgeschlossen: {Db} ({Copied} Vorschaubilder kopiert, {Skipped} übersprungen) — Neustart nötig",
                targetDb, copied, skipped);

            // Erfolg: DB bleibt gesperrt, Scans/Überwachung/Thumbnails bleiben angehalten — die App startet neu.
            return new RelocationResult(true, null, targetDb, copied, skipped);
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                OperationCanceledException => "Der Umzug wurde abgebrochen.",
                RelocationException => ex.Message,
                _ => $"Unerwarteter Fehler: {ex.Message}",
            };
            _logger.LogError(ex, "DB-Umzug nach {Target} fehlgeschlagen — Rücknahme", target);

            RollBack(rollback);
            if (quiet is not null)
            {
                await quiet.ResumeAsync().ConfigureAwait(false);
            }

            return Failed(message);
        }
    }

    private RelocationResult Failed(string message) => new(false, message, _paths.LibraryDatabasePath);

    private static string? ValidateMode(RelocationTargetCheck check, RelocationMode mode)
    {
        if (check.State == RelocationTargetState.Invalid)
        {
            return check.Problem ?? "Der Zielordner ist ungeeignet.";
        }

        return mode switch
        {
            RelocationMode.Move when check.State == RelocationTargetState.ExistingData
                => "Im Zielordner liegt bereits ein Katalog — bitte „Übernehmen“ oder „Ersetzen“ wählen.",
            RelocationMode.AdoptExisting when check.Existing is not { HasDatabase: true, IsValidCatalog: true }
                => check.Existing?.Problem ?? "Im Zielordner liegt kein Katalog, der übernommen werden kann.",
            RelocationMode.ReplaceExisting when check.State != RelocationTargetState.ExistingData
                => "Im Zielordner liegt kein Katalog, der ersetzt werden könnte.",
            RelocationMode.ReplaceExisting when !check.HasEnoughSpace
                => check.Problem,
            _ => null,
        };
    }

    /// <summary>
    /// DB kopieren und prüfen, dann Thumbnails kopieren. Jeder angelegte Zielpfad landet vorher in
    /// <paramref name="rollback"/>.
    /// </summary>
    private (int Copied, int Skipped) CopyCatalog(
        string oldDb,
        string oldThumbs,
        string targetDb,
        string targetThumbs,
        int thumbnailTotal,
        Stack<(string What, Action Undo)> rollback,
        IProgress<RelocationProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new RelocationProgress("Datenbank kopieren"));
        var targetFolder = Path.GetDirectoryName(targetDb)!;
        if (!Directory.Exists(targetFolder))
        {
            Directory.CreateDirectory(targetFolder);
            rollback.Push(("Zielordner", () => DeleteEmptyDirectory(targetFolder)));
        }

        var part = targetDb + PartSuffix;
        DeleteDatabaseFiles(part);
        rollback.Push(("Teilkopie", () => DeleteDatabaseFiles(part)));
        var sourceCounts = BackupDatabase(oldDb, part);
        ct.ThrowIfCancellationRequested();

        File.Move(part, targetDb);
        rollback.Push(("Datenbank im Ziel", () => DeleteDatabaseFiles(targetDb)));

        progress?.Report(new RelocationProgress("Kopie prüfen"));
        VerifyCatalog(targetDb, sourceCounts);
        ct.ThrowIfCancellationRequested();
        RebaseThumbnailPaths(targetDb, oldThumbs, targetThumbs);

        if (!Directory.Exists(oldThumbs))
        {
            return (0, 0);
        }

        rollback.Push(("Vorschaubilder im Ziel", () => DeleteDirectory(targetThumbs)));
        Directory.CreateDirectory(targetThumbs);
        return CopyThumbnails(oldThumbs, targetThumbs, thumbnailTotal, progress, ct);
    }

    /// <summary>
    /// <c>Thumbnails.CachePath</c> ist absolut: in der Kopie vom alten auf den neuen Thumbnail-Ordner umschreiben, sonst
    /// läse die App nach dem Neustart weiter aus dem alten Ordner (bis der Start ihn löscht, danach alles neu rendern).
    /// </summary>
    private void RebaseThumbnailPaths(string databasePath, string oldThumbs, string newThumbs)
    {
        var oldPrefix = AppDataPaths.NormalizeFolder(oldThumbs) + Path.DirectorySeparatorChar;
        var newPrefix = AppDataPaths.NormalizeFolder(newThumbs) + Path.DirectorySeparatorChar;
        using var connection = OpenRaw(databasePath, SqliteOpenMode.ReadWrite);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE Thumbnails SET CachePath = $new || substr(CachePath, length($old) + 1) " +
            "WHERE lower(substr(CachePath, 1, length($old))) = lower($old);";
        cmd.Parameters.AddWithValue("$old", oldPrefix);
        cmd.Parameters.AddWithValue("$new", newPrefix);
        var rows = cmd.ExecuteNonQuery();
        _logger.LogInformation("DB-Umzug: {Rows} Vorschaubild-Pfade auf {Thumbs} umgeschrieben", rows, newThumbs);
    }

    /// <summary>WAL in die DB zurückschreiben, Zeilen zählen und per SQLite-Backup-API in <paramref name="destination"/> kopieren.</summary>
    private Dictionary<string, long> BackupDatabase(string source, string destination)
    {
        using var src = OpenRaw(source, SqliteOpenMode.ReadWrite);
        SqlitePragmaInterceptor.ApplyPragmas(src);
        using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && reader.GetInt32(0) != 0)
            {
                _logger.LogWarning("DB-Umzug: WAL-Checkpoint nicht vollständig (busy) — die Backup-API kopiert den WAL-Inhalt mit");
            }
        }

        var counts = CountRows(src);
        using (var dst = OpenRaw(destination, SqliteOpenMode.ReadWriteCreate))
        {
            src.BackupDatabase(dst);
        }

        SqliteConnection.ClearAllPools();
        _logger.LogInformation("DB-Umzug: {Tables} Tabellen kopiert ({Rows} Zeilen)", counts.Count, counts.Values.Sum());
        return counts;
    }

    /// <summary>
    /// <c>integrity_check</c> muss „ok“ liefern; mit <paramref name="expectedCounts"/> müssen alle Tabellen dieselbe
    /// Zeilenzahl haben, ohne müssen die Katalog-Tabellen existieren.
    /// </summary>
    private void VerifyCatalog(string databasePath, IReadOnlyDictionary<string, long>? expectedCounts)
    {
        try
        {
            using var connection = OpenRaw(databasePath, SqliteOpenMode.ReadWrite);
            var problems = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    problems.Add(reader.GetString(0));
                }
            }

            if (problems is not ["ok"])
            {
                throw new RelocationException(
                    $"Die Prüfung der Datenbank im Ziel ist fehlgeschlagen (integrity_check: {string.Join("; ", problems.Take(3))}).");
            }

            var counts = CountRows(connection);
            if (expectedCounts is null)
            {
                if (!counts.ContainsKey("Libraries") || !counts.ContainsKey("Files"))
                {
                    throw new RelocationException("Die Datei im Zielordner ist kein PhotoViewer-Katalog.");
                }

                return;
            }

            foreach (var (table, expected) in expectedCounts)
            {
                if (!counts.TryGetValue(table, out var actual) || actual != expected)
                {
                    throw new RelocationException(
                        $"Die Kopie ist unvollständig: Tabelle {table} hat {(counts.ContainsKey(table) ? actual.ToString("N0") : "keine")} statt {expected:N0} Zeilen.");
                }
            }

            if (counts.Count != expectedCounts.Count)
            {
                throw new RelocationException("Die Kopie enthält andere Tabellen als das Original.");
            }
        }
        catch (SqliteException ex)
        {
            throw new RelocationException($"Die Datenbank im Ziel ist nicht lesbar: {ex.Message}", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private (int Copied, int Skipped) CopyThumbnails(
        string source,
        string destination,
        int total,
        IProgress<RelocationProgress>? progress,
        CancellationToken ct)
    {
        const string stage = "Vorschaubilder kopieren";
        progress?.Report(new RelocationProgress(stage, 0, total));
        var copied = 0;
        var skipped = 0;
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue; // halb geschriebene Thumbnails (atomarer Write) — der Dienst erzeugt sie neu
            }

            var dest = Path.Combine(destination, Path.GetRelativePath(source, file));
            try
            {
                var dir = Path.GetDirectoryName(dest)!;
                if (createdDirs.Add(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(file, dest, overwrite: true);
                copied++;
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new RelocationException("Der Zielordner ist voll — die Vorschaubilder passen nicht mehr hinein.", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                _logger.LogDebug(ex, "DB-Umzug: Vorschaubild {File} nicht kopiert", file);
            }

            if ((copied + skipped) % 250 == 0)
            {
                progress?.Report(new RelocationProgress(stage, copied + skipped, total));
            }
        }

        progress?.Report(new RelocationProgress(stage, copied + skipped, Math.Max(total, copied + skipped)));
        if (skipped > 0)
        {
            _logger.LogWarning("DB-Umzug: {Skipped} Vorschaubilder nicht kopiert — werden bei Bedarf neu erzeugt", skipped);
        }

        return (copied, skipped);
    }

    private void SaveLocation(string target, List<string> newPendingDeletes)
    {
        var newPaths = new AppDataPaths(_paths.AppDataRoot, target);
        var previous = DatabaseLocationStore.Load(_paths.AppDataRoot).PendingDeletes;
        var pending = previous.Concat(newPendingDeletes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !DatabaseLocationStore.TouchesActiveLocation(p, newPaths))
            .ToList();
        var folder = AppDataPaths.PathsEqual(target, _paths.AppDataRoot) ? null : target;
        try
        {
            DatabaseLocationStore.Save(_paths.AppDataRoot, new DatabaseLocation(folder, pending));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RelocationException($"Der neue Speicherort konnte nicht gespeichert werden: {ex.Message}", ex);
        }
    }

    // ---- Ruhigstellen ------------------------------------------------------------------------------

    /// <summary>
    /// Ordnerüberwachung stoppen, alle Bibliotheken über <see cref="IScanGate"/> belegen (wartet auf laufende Scans
    /// und Datei-Batches), Thumbnail-Worker anhalten, DB für neue Verbindungen sperren und auf laufende Schreiber
    /// warten (<c>BEGIN IMMEDIATE</c>).
    /// </summary>
    private async Task<Quiescence> QuiesceAsync(string databasePath, CancellationToken ct)
    {
        var quiet = new Quiescence(this);
        try
        {
            await _watch.StopAsync().ConfigureAwait(false);
            quiet.WatchStopped = true;

            List<int> libraryIds;
            await using (var ctx = _factory.Create())
            {
                libraryIds = await ctx.Libraries.AsNoTracking().Select(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QuiesceTimeout);
            try
            {
                foreach (var libraryId in libraryIds.Distinct().Order())
                {
                    quiet.Leases.Add(await _scanGate.AcquireForFileOperationAsync(libraryId, timeout.Token).ConfigureAwait(false));
                }

                await _thumbnails.PauseAsync(timeout.Token).ConfigureAwait(false);
                quiet.ThumbnailsPaused = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RelocationException(
                    $"Ein Scan, eine Datei-Operation oder ein Vorschaubild-Auftrag läuft seit über {QuiesceTimeout.TotalSeconds:0} s. " +
                    "Bitte warten, bis er fertig ist, und den Umzug dann erneut starten.");
            }

            quiet.Suspension = (_factory as IDatabaseAccessGate)?.Suspend("Speicherort wird umgezogen");
            SqliteConnection.ClearAllPools();
            WaitForWriters(databasePath);
            return quiet;
        }
        catch
        {
            await quiet.ResumeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Wartet (busy_timeout) auf eine laufende Schreib-Transaktion — danach kann keine neue mehr beginnen.</summary>
    private static void WaitForWriters(string databasePath)
    {
        try
        {
            using var connection = OpenRaw(databasePath, SqliteOpenMode.ReadWrite);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "BEGIN IMMEDIATE; ROLLBACK;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new RelocationException($"Die Datenbank wird noch beschrieben ({ex.Message}). Bitte später erneut versuchen.", ex);
        }
    }

    private sealed class Quiescence(DatabaseRelocationService owner)
    {
        public bool WatchStopped { get; set; }

        public bool ThumbnailsPaused { get; set; }

        public List<IDisposable> Leases { get; } = [];

        public IDisposable? Suspension { get; set; }

        public async Task ResumeAsync()
        {
            Suspension?.Dispose();
            Suspension = null;
            if (ThumbnailsPaused)
            {
                owner._thumbnails.Resume();
                ThumbnailsPaused = false;
            }

            foreach (var lease in Leases)
            {
                lease.Dispose();
            }

            Leases.Clear();
            if (WatchStopped)
            {
                WatchStopped = false;
                try
                {
                    await owner._watch.StartAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    owner._logger.LogWarning(ex, "DB-Umzug: Ordnerüberwachung konnte nach der Rücknahme nicht wieder gestartet werden");
                }
            }
        }
    }

    // ---- Hilfen ------------------------------------------------------------------------------------

    /// <summary>Benennt eine vorhandene Datei bzw. einen Ordner in „…ersetzt-{stamp}“ um; Rücknahme benennt zurück.</summary>
    private static string? MoveAside(string path, string stamp, Stack<(string What, Action Undo)> rollback)
    {
        var aside = $"{path}.ersetzt-{stamp}";
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, aside);
                rollback.Push(($"Umbenennung {path}", () => File.Move(aside, path)));
                return aside;
            }

            if (Directory.Exists(path))
            {
                Directory.Move(path, aside);
                rollback.Push(($"Umbenennung {path}", () => Directory.Move(aside, path)));
                return aside;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RelocationException($"Der vorhandene Katalog im Ziel kann nicht beiseitegelegt werden ({Path.GetFileName(path)}): {ex.Message}", ex);
        }
    }

    private void RollBack(Stack<(string What, Action Undo)> rollback)
    {
        while (rollback.TryPop(out var step))
        {
            try
            {
                step.Undo();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB-Umzug: Rücknahme „{What}“ fehlgeschlagen", step.What);
            }
        }

        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection OpenRaw(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static Dictionary<string, long> CountRows(SqliteConnection connection)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in ReadTableNames(connection))
        {
            counts[table] = Convert.ToInt64(ExecuteScalar(connection, $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\";"));
        }

        return counts;
    }

    private static HashSet<string> ReadTableNames(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string? ExecuteScalarText(SqliteConnection connection, string sql) => ExecuteScalar(connection, sql)?.ToString();

    private static void DeleteDatabaseFiles(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { databasePath, databasePath + "-wal", databasePath + "-shm", databasePath + "-journal" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static bool HasAnyFile(string folder)
        => Directory.Exists(folder)
           && Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Any();

    private static (int Count, long Bytes) MeasureDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var count = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            count++;
            bytes += file.Length;
        }

        return (count, bytes);
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>ERROR_DISK_FULL (112) bzw. ERROR_HANDLE_DISK_FULL (39).</summary>
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39;

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024):N0} KB",
    };
}

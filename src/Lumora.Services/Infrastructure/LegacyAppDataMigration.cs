using Microsoft.Data.Sqlite;

namespace Lumora.Services.Infrastructure;

/// <summary>Ausgang von <see cref="LegacyAppDataMigration.Run"/>.</summary>
public enum LegacyAppDataMigrationOutcome
{
    /// <summary>Kein alter Ordner vorhanden.</summary>
    NothingToDo,

    /// <summary>Alter Ordner wurde zum neuen umbenannt (Speicherort-Datei und Vorschaubild-Pfade nachgezogen).</summary>
    Moved,

    /// <summary>Umbenennen ging nicht (Dateien gesperrt, z. B. alte Lumora läuft noch) — der alte Ordner bleibt aktiv.</summary>
    KeptLegacy,

    /// <summary>Beide Ordner existieren — der neue gilt, der alte bleibt unangetastet liegen.</summary>
    BothExist,
}

/// <param name="Root">App-Wurzel, mit der dieser Start arbeitet.</param>
/// <param name="Detail">Fehlertext bzw. Hinweis fürs Protokoll.</param>
/// <param name="RebasedThumbnails">Umgeschriebene <c>Thumbnails.CachePath</c>-Zeilen.</param>
public sealed record LegacyAppDataMigrationResult(
    LegacyAppDataMigrationOutcome Outcome,
    string Root,
    string? Detail = null,
    int RebasedThumbnails = 0);

/// <summary>
/// Einmaliger Umzug der App-Wurzel <c>%LocalAppData%\PhotoViewerV3</c> → <c>%LocalAppData%\Lumora</c> beim Start
/// (ADR-0056). Beide liegen auf demselben Laufwerk, daher reicht ein Umbenennen des Ordners; danach zeigen
/// <c>database-location.json</c> und die absoluten <c>Thumbnails.CachePath</c> noch auf den alten Ort und werden
/// umgeschrieben. Läuft beim Start, bevor Logger und Datenbank geöffnet sind; schlägt das Umbenennen fehl, arbeitet
/// der Start mit dem alten Ordner weiter und versucht es beim nächsten Mal erneut — nichts geht verloren.
/// </summary>
public static class LegacyAppDataMigration
{
    /// <summary>Name der App-Wurzel bis 0.4.2.</summary>
    public const string LegacyFolderName = "PhotoViewerV3";

    /// <summary>%LocalAppData%\PhotoViewerV3.</summary>
    public static string DefaultLegacyRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);

    public static LegacyAppDataMigrationResult Run(string legacyRoot, string newRoot)
    {
        if (!Directory.Exists(legacyRoot))
        {
            return new(LegacyAppDataMigrationOutcome.NothingToDo, newRoot);
        }

        try
        {
            // Ein leerer neuer Ordner (von Hand oder von einem Werkzeug angelegt) darf den Umzug nicht verhindern —
            // sonst startete Lumora mit leerem Katalog, während die Daten im alten Ordner liegen
            if (Directory.Exists(newRoot))
            {
                if (Directory.EnumerateFileSystemEntries(newRoot).Any())
                {
                    return new(LegacyAppDataMigrationOutcome.BothExist, newRoot, $"Alter Datenordner {legacyRoot} bleibt liegen");
                }

                Directory.Delete(newRoot);
            }

            Directory.Move(legacyRoot, newRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(LegacyAppDataMigrationOutcome.KeptLegacy, legacyRoot, ex.Message);
        }

        var notes = new List<string>();
        try
        {
            RebaseLocationFile(legacyRoot, newRoot);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Der Start meldet eine beschädigte Speicherort-Datei ohnehin selbst (ADR-0026)
            notes.Add($"Speicherort-Datei nicht angepasst: {ex.Message}");
        }

        var rows = 0;
        try
        {
            rows = RebaseThumbnailPaths(legacyRoot, newRoot);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Ohne Umschreiben rendert der Vorschaubild-Dienst fehlende Dateien neu — kein Datenverlust
            notes.Add($"Vorschaubild-Pfade nicht umgeschrieben: {ex.Message}");
        }

        return new(LegacyAppDataMigrationOutcome.Moved, newRoot, notes.Count == 0 ? null : string.Join("; ", notes), rows);
    }

    /// <summary>Gewählter Datenordner und vorgemerkte Löschungen, die im alten Ordner lagen, zeigen auf den neuen.</summary>
    private static void RebaseLocationFile(string legacyRoot, string newRoot)
    {
        var location = DatabaseLocationStore.Load(newRoot);
        if (location.DatabaseFolder is null && location.PendingDeletes.Count == 0)
        {
            return;
        }

        var folder = location.DatabaseFolder is null ? null : Rebase(location.DatabaseFolder, legacyRoot, newRoot);
        var deletes = location.PendingDeletes.Select(p => Rebase(p, legacyRoot, newRoot)).ToList();
        DatabaseLocationStore.Save(newRoot, new DatabaseLocation(folder, deletes));
    }

    private static string Rebase(string path, string legacyRoot, string newRoot)
    {
        if (!AppDataPaths.IsSameOrInside(path, legacyRoot))
        {
            return path;
        }

        var relative = Path.GetRelativePath(AppDataPaths.NormalizeFolder(legacyRoot), Path.GetFullPath(path));
        return relative == "." ? newRoot : Path.Combine(newRoot, relative);
    }

    /// <summary>
    /// <c>Thumbnails.CachePath</c> ist absolut (wie beim Katalog-Umzug, ADR-0026): Präfix des alten Ordners durch den neuen
    /// ersetzen. Ein Datenordner außerhalb des alten Ordners hat keine solchen Pfade (0 Zeilen).
    /// </summary>
    private static int RebaseThumbnailPaths(string legacyRoot, string newRoot)
    {
        var database = AppDataPaths.Resolve(newRoot).LibraryDatabasePath;
        if (!File.Exists(database))
        {
            return 0;
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'Thumbnails';";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
            {
                return 0;
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "PRAGMA busy_timeout = 5000;" +
            "UPDATE Thumbnails SET CachePath = $new || substr(CachePath, length($old) + 1) " +
            "WHERE lower(substr(CachePath, 1, length($old))) = lower($old);";
        cmd.Parameters.AddWithValue("$old", AppDataPaths.NormalizeFolder(legacyRoot) + Path.DirectorySeparatorChar);
        cmd.Parameters.AddWithValue("$new", AppDataPaths.NormalizeFolder(newRoot) + Path.DirectorySeparatorChar);
        return cmd.ExecuteNonQuery();
    }
}

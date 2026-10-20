using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PhotoViewerV3.Services.Database;

namespace PhotoViewerV3.Services.Scanning;

/// <summary>
/// Trägt die Benutzerdaten einer Bibliothek über „Neu aufbauen“ (<see cref="ScanMode.ClearAndRescan"/>) hinweg
/// (ADR-0023): Favorit (<c>Files.IsFavorite</c>), Bewertung (<c>Files.Rating</c>), Tag-Zuweisungen
/// (<c>FileTags</c>) und Kategorie-Werte (<c>FileCategoryValues</c>, ADR-0027). <see cref="Capture"/> sichert sie VOR dem Leeren des Katalogs in TEMP-Tabellen einer eigenen
/// Verbindung, <see cref="Restore"/> schreibt sie NACH dem Bulk-Insert auf die neuen FileIds zurück — mengenbasiert
/// (INSERT…SELECT / UPDATE…FROM, keine Zeilenschleife in C#) und in einer Transaktion.
/// <para>
/// Schlüssel ist dieselbe Identität wie beim Incremental-Scan (ADR-0019): voller Ordnerpfad (<c>Folders.Path</c>,
/// damit auch Wurzel-übergreifend eindeutig, ADR-0020) + Dateiname, ohne Groß-/Kleinschreibung (Unicode, wie
/// <see cref="StringComparer.OrdinalIgnoreCase"/>). Keine Prüfung von Größe/Änderungszeit: eine an Ort und Stelle
/// geänderte Datei behält ihre Daten — wie beim Incremental-Scan, wo sie ihre FileId behält. Dateien, die nach dem
/// Neuscan nicht mehr existieren (auch zuvor „fehlt“-markierte), verlieren ihre Daten. Die Tabellen <c>Tags</c>,
/// <c>Categories</c> und <c>CategoryValues</c> selbst werden nie angefasst.
/// </para>
/// <para>
/// Die TEMP-Tabellen leben nur in dieser Verbindung (<c>temp_store=MEMORY</c>, Pooling aus) und verschwinden mit
/// <see cref="Dispose"/>; ein Absturz zwischen Leeren und Zurückschreiben verliert die Daten wie vor 0.2.12.
/// </para>
/// </summary>
public sealed class UserDataCarryOver : IDisposable
{
    /// <summary>Ergebnis des Zurückschreibens; <see cref="LostFiles"/> = gesicherte Dateien ohne Gegenstück im Neuscan.</summary>
    public readonly record struct RestoreReport(
        int CapturedFiles,
        int RestoredFiles,
        int LostFiles,
        int FavoritesOrRatingsRestored,
        int TagAssignmentsRestored,
        TimeSpan Duration,
        int CategoryValuesRestored = 0);

    private const string CaseFoldFunction = "pv_casefold";

    private const string CreateTablesSql = """
        CREATE TEMP TABLE pv_carry_files(
            OldFileId  INTEGER PRIMARY KEY,
            FolderKey  TEXT NOT NULL,
            NameKey    TEXT NOT NULL,
            Name       TEXT NOT NULL,
            IsFavorite INTEGER NOT NULL,
            Rating     INTEGER,
            UNIQUE(FolderKey, NameKey));
        CREATE TEMP TABLE pv_carry_tags(
            OldFileId INTEGER NOT NULL,
            TagId     INTEGER NOT NULL,
            PRIMARY KEY(OldFileId, TagId)) WITHOUT ROWID;
        CREATE TEMP TABLE pv_carry_categories(
            OldFileId  INTEGER NOT NULL,
            CategoryId INTEGER NOT NULL,
            ValueId    INTEGER NOT NULL,
            PRIMARY KEY(OldFileId, CategoryId)) WITHOUT ROWID;
        CREATE TEMP TABLE pv_carry_folders(
            FolderKey TEXT PRIMARY KEY,
            FolderId  INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TEMP TABLE pv_carry_map(
            OldFileId INTEGER PRIMARY KEY,
            NewFileId INTEGER NOT NULL);
        """;

    // Nur Dateien mit Benutzerdaten. Bei kollidierenden Schlüsseln (nur auf case-sensitiven Ordnern möglich) gewinnt
    // die vorhandene vor der „fehlt“-markierten Zeile, sonst die ältere.
    private const string CaptureFilesSql = """
        INSERT OR IGNORE INTO temp.pv_carry_files(OldFileId, FolderKey, NameKey, Name, IsFavorite, Rating)
        SELECT f.Id, pv_casefold(d.Path), pv_casefold(f.Name), f.Name, f.IsFavorite, f.Rating
        FROM Files f JOIN Folders d ON d.Id = f.FolderId
        WHERE f.LibraryId = $lib
          AND (f.IsFavorite <> 0 OR f.Rating IS NOT NULL
               OR EXISTS (SELECT 1 FROM FileTags t WHERE t.FileId = f.Id)
               OR EXISTS (SELECT 1 FROM FileCategoryValues v WHERE v.FileId = f.Id))
        ORDER BY f.Missing, f.Id;
        """;

    private const string CaptureTagsSql = """
        INSERT OR IGNORE INTO temp.pv_carry_tags(OldFileId, TagId)
        SELECT t.FileId, t.TagId FROM temp.pv_carry_files c CROSS JOIN FileTags t ON t.FileId = c.OldFileId;
        """;

    private const string CaptureCategoriesSql = """
        INSERT OR IGNORE INTO temp.pv_carry_categories(OldFileId, CategoryId, ValueId)
        SELECT v.FileId, v.CategoryId, v.ValueId FROM temp.pv_carry_files c CROSS JOIN FileCategoryValues v ON v.FileId = c.OldFileId;
        """;

    // Ordner-Schlüssel des Neuscans: eine Funktionsauswertung je Ordner, nicht je Datei.
    private const string NewFoldersSql = """
        INSERT OR IGNORE INTO temp.pv_carry_folders(FolderKey, FolderId)
        SELECT pv_casefold(Path), Id FROM Folders WHERE LibraryId = $lib ORDER BY Id;
        """;

    // Zuordnung alt → neu, getrieben von den gesicherten Zeilen (CROSS JOIN legt die Reihenfolge fest):
    // Ordner per Primärschlüssel, Datei per IX_Files_Folder_Name mit exaktem Namen … Kein Filter auf Files.LibraryId:
    // pv_carry_folders enthält nur Ordner dieser Bibliothek, und der Filter lockte den Planer auf
    // IX_Files_Library_Folder (Ordner-Scan statt Namens-Lookup; 200k Dateien: 7,6 s statt 1,0 s).
    private const string MapExactSql = """
        INSERT OR IGNORE INTO temp.pv_carry_map(OldFileId, NewFileId)
        SELECT c.OldFileId, f.Id
        FROM temp.pv_carry_files c
        CROSS JOIN temp.pv_carry_folders nf ON nf.FolderKey = c.FolderKey
        CROSS JOIN Files f ON f.FolderId = nf.FolderId AND f.Name = c.Name;
        """;

    // … und für den seltenen Rest (Name nur in Groß-/Kleinschreibung geändert) die Dateien desselben Ordners.
    private const string MapCaseInsensitiveSql = """
        INSERT OR IGNORE INTO temp.pv_carry_map(OldFileId, NewFileId)
        SELECT c.OldFileId, f.Id
        FROM temp.pv_carry_files c
        CROSS JOIN temp.pv_carry_folders nf ON nf.FolderKey = c.FolderKey
        CROSS JOIN Files f ON f.FolderId = nf.FolderId
        WHERE c.OldFileId NOT IN (SELECT OldFileId FROM temp.pv_carry_map)
          AND pv_casefold(f.Name) = c.NameKey;
        """;

    private const string RestoreFilesSql = """
        UPDATE Files SET IsFavorite = c.IsFavorite, Rating = c.Rating
        FROM temp.pv_carry_map m CROSS JOIN temp.pv_carry_files c ON c.OldFileId = m.OldFileId
        WHERE Files.Id = m.NewFileId AND (c.IsFavorite <> 0 OR c.Rating IS NOT NULL);
        """;

    // Ein während des Scans gelöschtes Tag fällt still heraus (sonst verletzt die Zuweisung den Fremdschlüssel).
    private const string RestoreTagsSql = """
        INSERT OR IGNORE INTO FileTags(FileId, TagId)
        SELECT m.NewFileId, t.TagId
        FROM temp.pv_carry_tags t CROSS JOIN temp.pv_carry_map m ON m.OldFileId = t.OldFileId
        WHERE EXISTS (SELECT 1 FROM Tags g WHERE g.Id = t.TagId);
        """;

    // Wie bei Tags: ein während des Scans gelöschter Wert (oder seine Kategorie) fällt still heraus.
    private const string RestoreCategoriesSql = """
        INSERT OR IGNORE INTO FileCategoryValues(FileId, CategoryId, ValueId)
        SELECT m.NewFileId, v.CategoryId, v.ValueId
        FROM temp.pv_carry_categories v CROSS JOIN temp.pv_carry_map m ON m.OldFileId = v.OldFileId
        WHERE EXISTS (SELECT 1 FROM CategoryValues cv WHERE cv.Id = v.ValueId AND cv.CategoryId = v.CategoryId);
        """;

    private readonly SqliteConnection _connection;
    private readonly int _libraryId;
    private bool _restored;

    private UserDataCarryOver(SqliteConnection connection, int libraryId)
    {
        _connection = connection;
        _libraryId = libraryId;
    }

    /// <summary>Anzahl gesicherter Dateien (mit Favorit, Bewertung, mindestens einem Tag oder Kategorie-Wert).</summary>
    public int CapturedFiles { get; private set; }

    /// <summary>Anzahl gesicherter Tag-Zuweisungen.</summary>
    public int CapturedTagAssignments { get; private set; }

    /// <summary>Anzahl gesicherter Kategorie-Werte.</summary>
    public int CapturedCategoryValues { get; private set; }

    /// <summary>Dauer der Sicherung.</summary>
    public TimeSpan CaptureDuration { get; private set; }

    /// <summary>
    /// Öffnet eine eigene Verbindung und sichert die Benutzerdaten der Bibliothek in TEMP-Tabellen (eine
    /// Lese-Transaktion, schreibt nur in die TEMP-Datenbank). Aufrufen, bevor der Katalog geleert wird.
    /// </summary>
    public static UserDataCarryOver Capture(ILibraryContextFactory contextFactory, int libraryId)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        var sw = Stopwatch.StartNew();
        var connection = contextFactory.OpenSqliteConnection();
        try
        {
            connection.CreateFunction<string, string>(CaseFoldFunction, static s => s.ToUpperInvariant(), isDeterministic: true);
            var carry = new UserDataCarryOver(connection, libraryId);
            Execute(connection, null, CreateTablesSql, libraryId);

            using (var tx = connection.BeginTransaction(deferred: true))
            {
                carry.CapturedFiles = Execute(connection, tx, CaptureFilesSql, libraryId);
                carry.CapturedTagAssignments = Execute(connection, tx, CaptureTagsSql, libraryId);
                carry.CapturedCategoryValues = Execute(connection, tx, CaptureCategoriesSql, libraryId);
                tx.Commit();
            }

            carry.CaptureDuration = sw.Elapsed;
            return carry;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Schreibt die gesicherten Daten auf die neuen FileIds zurück — in EINER Schreibtransaktion
    /// (BEGIN IMMEDIATE, busy_timeout). Aufrufen, nachdem der Neuscan seine Files-Zeilen geschrieben hat.
    /// Darf nur einmal laufen. FTS-Spalten (Name, Extension) werden nicht berührt, der Index bleibt gültig.
    /// </summary>
    public RestoreReport Restore()
    {
        if (_restored)
        {
            throw new InvalidOperationException("Benutzerdaten wurden bereits zurückgeschrieben.");
        }

        _restored = true;
        var sw = Stopwatch.StartNew();
        if (CapturedFiles == 0)
        {
            return new RestoreReport(0, 0, 0, 0, 0, sw.Elapsed);
        }

        using var tx = _connection.BeginTransaction();
        Execute(_connection, tx, NewFoldersSql, _libraryId);
        var mapped = Execute(_connection, tx, MapExactSql, _libraryId);
        mapped += Execute(_connection, tx, MapCaseInsensitiveSql, _libraryId);
        var files = Execute(_connection, tx, RestoreFilesSql, _libraryId);
        var tags = Execute(_connection, tx, RestoreTagsSql, _libraryId);
        var categories = Execute(_connection, tx, RestoreCategoriesSql, _libraryId);
        tx.Commit();

        return new RestoreReport(CapturedFiles, mapped, CapturedFiles - mapped, files, tags, sw.Elapsed, categories);
    }

    /// <summary>Schließt die Verbindung; die TEMP-Tabellen verschwinden mit ihr.</summary>
    public void Dispose() => _connection.Dispose();

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, int libraryId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (sql.Contains("$lib", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("$lib", libraryId);
        }

        return cmd.ExecuteNonQuery();
    }
}

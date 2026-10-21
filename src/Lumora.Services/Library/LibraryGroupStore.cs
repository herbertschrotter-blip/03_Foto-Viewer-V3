using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Lumora.Services.Library;

/// <summary>
/// SQL der Bibliotheks-Gruppen (ADR-0048) auf rohen Verbindungen. Lesen ohne Transaktion (WAL), Schreiben in der
/// Transaktion des Aufrufers. Die Tabellen mit <c>GroupId</c> stehen in <see cref="TermTables"/>.
/// </summary>
internal static class LibraryGroupStore
{
    /// <summary>Settings-Schlüssel der Ansicht „Alle verknüpften“: Wert = Gruppen-Id; fehlt = einzelne Bibliothek.</summary>
    public const string ViewAllLinkedKey = "Library.ViewAllLinkedGroup";

    /// <summary>
    /// Tabellen mit benutzerdefinierten Begriffen je Gruppe (CategoryValues hängen an Categories). MotifTerms (0.2.56) hat
    /// zusätzlich eine Anker-Bibliothek für den Fremdschlüssel (<see cref="ReanchorMotifTerms"/>).
    /// </summary>
    public static readonly string[] TermTables = ["Tags", "Categories", "PersonRelations", "Persons", "MotifTerms", "Albums" /* ADR-0054 */];

    /// <summary>Settings-Merker „Start-Liste der Motive angelegt“ je Gruppe (ADR-0052, seit 0.2.56 je Gruppe).</summary>
    public static string MotifSeededKey(int groupId) => "motifs.seeded.g" + groupId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Aktive Bibliothek (jüngstes LastOpenedAt, sonst CreatedAt, sonst Id — wie LibraryService) oder null.</summary>
    public static (int LibraryId, int GroupId)? ReadActive(SqliteConnection connection, SqliteTransaction? tx = null)
    {
        using var cmd = Command(connection, tx, """
            SELECT Id, GroupId FROM Libraries
            ORDER BY LastOpenedAt DESC, CreatedAt DESC, Id DESC LIMIT 1;
            """);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
    }

    /// <summary>GroupId der Bibliothek (0 = keine) oder null, wenn es sie nicht gibt.</summary>
    public static int? ReadGroupOf(SqliteConnection connection, SqliteTransaction? tx, int libraryId)
    {
        using var cmd = Command(connection, tx, "SELECT GroupId FROM Libraries WHERE Id = $id;", ("$id", libraryId));
        return cmd.ExecuteScalar() is long g ? (int)g : null;
    }

    /// <summary>true, wenn die Gruppe als Zeile existiert.</summary>
    public static bool GroupExists(SqliteConnection connection, SqliteTransaction? tx, int groupId)
        => groupId != 0 && Scalar(connection, tx, "SELECT COUNT(*) FROM LibraryGroups WHERE Id = $g;", ("$g", groupId)) > 0;

    public static string ReadGroupName(SqliteConnection connection, SqliteTransaction? tx, int groupId)
    {
        using var cmd = Command(connection, tx, "SELECT Name FROM LibraryGroups WHERE Id = $g;", ("$g", groupId));
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }

    public static string ReadLibraryName(SqliteConnection connection, SqliteTransaction? tx, int libraryId)
    {
        using var cmd = Command(connection, tx, "SELECT Name FROM Libraries WHERE Id = $id;", ("$id", libraryId));
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>Bibliotheken der Gruppe, Id aufsteigend.</summary>
    public static List<int> ReadGroupLibraries(SqliteConnection connection, SqliteTransaction? tx, int groupId)
    {
        using var cmd = Command(connection, tx, "SELECT Id FROM Libraries WHERE GroupId = $g ORDER BY Id;", ("$g", groupId));
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    /// <summary>Gruppe der Ansicht „Alle verknüpften“ oder null.</summary>
    public static int? ReadViewAllLinked(SqliteConnection connection, SqliteTransaction? tx = null)
    {
        using var cmd = Command(connection, tx, "SELECT Value FROM Settings WHERE Key = $k;", ("$k", ViewAllLinkedKey));
        return cmd.ExecuteScalar() is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : null;
    }

    public static void WriteViewAllLinked(SqliteConnection connection, SqliteTransaction tx, int? groupId)
    {
        if (groupId is { } g)
        {
            Execute(connection, tx,
                "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
                ("$k", ViewAllLinkedKey), ("$v", g.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            Execute(connection, tx, "DELETE FROM Settings WHERE Key = $k;", ("$k", ViewAllLinkedKey));
        }
    }

    /// <summary>Neue Gruppe; liefert ihre Id.</summary>
    public static int CreateGroup(SqliteConnection connection, SqliteTransaction tx, string name)
    {
        using var cmd = Command(connection, tx,
            "INSERT INTO LibraryGroups (Name, CreatedAtUtc) VALUES ($n, $t); SELECT last_insert_rowid();",
            ("$n", string.IsNullOrWhiteSpace(name) ? "Bibliotheken" : name.Trim()),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gibt der Bibliothek eine eigene Gruppe, falls sie keine (gültige) hat, und übernimmt Begriffe ohne Gruppe
    /// (GroupId 0 — angelegt, als es keine Bibliothek gab). Liefert die Gruppe; 0, wenn es die Bibliothek nicht gibt.
    /// </summary>
    public static int EnsureGroup(SqliteConnection connection, SqliteTransaction tx, int libraryId)
    {
        var current = ReadGroupOf(connection, tx, libraryId);
        if (current is null)
        {
            return 0;
        }

        if (current.Value != 0 && GroupExists(connection, tx, current.Value))
        {
            return current.Value;
        }

        var groupId = CreateGroup(connection, tx, ReadLibraryName(connection, tx, libraryId));
        Execute(connection, tx, "UPDATE Libraries SET GroupId = $g WHERE Id = $id;", ("$g", groupId), ("$id", libraryId));
        AdoptOrphans(connection, tx, groupId);
        return groupId;
    }

    /// <summary>
    /// Begriffe ohne Gruppe (GroupId 0) gehören ab jetzt <paramref name="groupId"/>. Motiv-Begriffe ohne Gruppe folgen
    /// stattdessen ihrer Anker-Bibliothek (sie entstehen nur für eine Bibliothek, nie „ohne Bibliothek“) — die einer
    /// anderen Bibliothek ohne Gruppe bleiben bei 0, bis diese ihre Gruppe bekommt.
    /// </summary>
    public static void AdoptOrphans(SqliteConnection connection, SqliteTransaction tx, int groupId)
    {
        foreach (var table in TermTables)
        {
            if (table == "MotifTerms")
            {
                Execute(connection, tx, """
                    UPDATE MotifTerms SET GroupId = (SELECT l.GroupId FROM Libraries l WHERE l.Id = MotifTerms.LibraryId)
                    WHERE GroupId = 0 AND (SELECT l.GroupId FROM Libraries l WHERE l.Id = MotifTerms.LibraryId) <> 0;
                    """);
                continue;
            }

            Execute(connection, tx, $"UPDATE {table} SET GroupId = $g WHERE GroupId = 0;", ("$g", groupId));
        }
    }

    /// <summary>
    /// Motiv-Begriffe, deren Anker <paramref name="libraryId"/> ist, bekommen eine andere Bibliothek ihrer Gruppe als
    /// Anker (vor dem Entfernen der Bibliothek bzw. nach dem Lösen) — sonst nähme die Kaskade des Fremdschlüssels
    /// <c>MotifTerms.LibraryId</c> gemeinsame Begriffe samt Treffern der übrigen Bibliotheken mit. Ohne andere Bibliothek in
    /// der Gruppe bleibt der Anker (die Begriffe gehen dann mit der Gruppe).
    /// </summary>
    public static int ReanchorMotifTerms(SqliteConnection connection, SqliteTransaction tx, int libraryId)
        => Execute(connection, tx, """
            UPDATE MotifTerms SET LibraryId = (SELECT MIN(l.Id) FROM Libraries l WHERE l.GroupId = MotifTerms.GroupId AND l.Id <> $lib)
            WHERE LibraryId = $lib
              AND EXISTS (SELECT 1 FROM Libraries l WHERE l.GroupId = MotifTerms.GroupId AND l.Id <> $lib);
            """, ("$lib", libraryId));

    /// <summary>
    /// Löscht eine Gruppe ohne Bibliotheken samt ihrer Begriffe (Zuordnungen per Kaskade, Gesichter behalten nichts —
    /// ihre Dateien sind mit der Bibliothek gegangen). Nichts, wenn noch eine Bibliothek in der Gruppe ist.
    /// </summary>
    public static bool DeleteGroupIfEmpty(SqliteConnection connection, SqliteTransaction tx, int groupId)
    {
        if (groupId == 0 || Scalar(connection, tx, "SELECT COUNT(*) FROM Libraries WHERE GroupId = $g;", ("$g", groupId)) > 0)
        {
            return false;
        }

        Execute(connection, tx, "DELETE FROM FileTags WHERE TagId IN (SELECT Id FROM Tags WHERE GroupId = $g);", ("$g", groupId));
        Execute(connection, tx, "DELETE FROM Tags WHERE GroupId = $g;", ("$g", groupId));
        Execute(connection, tx, "DELETE FROM FileCategoryValues WHERE CategoryId IN (SELECT Id FROM Categories WHERE GroupId = $g);", ("$g", groupId));
        Execute(connection, tx, "DELETE FROM CategoryValues WHERE CategoryId IN (SELECT Id FROM Categories WHERE GroupId = $g);", ("$g", groupId));
        Execute(connection, tx, "DELETE FROM Categories WHERE GroupId = $g;", ("$g", groupId));
        Execute(connection, tx, "DELETE FROM Persons WHERE GroupId = $g;", ("$g", groupId)); // Faces: SET NULL, Ablehnungen: Kaskade
        Execute(connection, tx, "DELETE FROM PersonRelations WHERE GroupId = $g;", ("$g", groupId));
        Execute(connection, tx, "DELETE FROM MotifTerms WHERE GroupId = $g;", ("$g", groupId)); // Treffer per Kaskade (0.2.56)
        Execute(connection, tx, "DELETE FROM Settings WHERE Key = $k;", ("$k", MotifSeededKey(groupId)));
        Albums.AlbumGroupStore.DeleteGroup(connection, tx, groupId); // Alben (ADR-0054), Verknüpfungen per Kaskade
        Execute(connection, tx, "DELETE FROM LibraryGroups WHERE Id = $g;", ("$g", groupId));
        if (ReadViewAllLinked(connection, tx) == groupId)
        {
            WriteViewAllLinked(connection, tx, null);
        }

        return true;
    }

    /// <summary>
    /// Nach dem Anlegen eines Begriffs über EF (Tags, Kategorien): ein gleichzeitiges Verknüpfen kann die zuvor gelesene
    /// Gruppe aufgelöst haben (ihre Begriffe sind umgezogen, die Gruppe gelöscht) — dann zieht der neue Begriff in die
    /// jetzige Gruppe der Bibliothek nach. Ein Anlegen während der Verknüpfungs-Transaktion ist ausgeschlossen (Schreibsperre),
    /// davor erfasst das Verknüpfen ihn selbst. Liefert false, wenn der Name dort schon vergeben ist (Aufrufer räumt auf).
    /// </summary>
    public static async Task<bool> RehomeIfGroupGoneAsync(Database.ILibraryContextFactory factory, string table, int id, int libraryId, CancellationToken ct)
    {
        if (libraryId == 0)
        {
            return true;
        }

        return await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            try
            {
                Execute(connection, tx, $"""
                    UPDATE {table} SET GroupId = (SELECT GroupId FROM Libraries WHERE Id = $lib)
                    WHERE Id = $id AND GroupId <> 0
                      AND NOT EXISTS (SELECT 1 FROM Libraries l WHERE l.GroupId = {table}.GroupId)
                      AND EXISTS (SELECT 1 FROM Libraries WHERE Id = $lib);
                    """, ("$lib", libraryId), ("$id", id));
                tx.Commit();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return false; // gleicher Name in der Zielgruppe
            }
        }, ct).ConfigureAwait(false);
    }

    // ---- Hilfen -----------------------------------------------------------------------------------------------------

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }
}

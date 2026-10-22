using Microsoft.Data.Sqlite;

namespace Lumora.Services.Assistant;

/// <summary>
/// Verlauf und gemerkte Antworten des Assistenten beim Verknüpfen, Lösen und Entfernen von Bibliotheks-Gruppen
/// (ADR-0063, Regeln aus ADR-0048) — in der Transaktion des Aufrufers (<c>LibraryLinkStore</c>, <c>LibraryGroupStore</c>).
/// <list type="bullet">
/// <item>Verknüpfen: gemerkte Antworten der Zielgruppe gewinnen bei gleichem Begriff, die übrigen wandern; der Verlauf wandert.</item>
/// <item>Personen zusammenführen: Antworten zeigen danach auf die bleibende Person (sonst nähme die Kaskade sie mit).</item>
/// <item>Lösen: die herausgelöste Bibliothek bekommt Kopien (Antworten auf die kopierten Personen, Verlauf als Kopie).</item>
/// <item>Gruppe ohne Bibliothek: alles geht mit.</item>
/// </list>
/// </summary>
internal static class AssistantGroupStore
{
    public static void Link(SqliteConnection connection, SqliteTransaction tx, int targetGroup, int sourceGroup)
    {
        var p = new (string, object?)[] { ("$t", targetGroup), ("$s", sourceGroup) };
        AssistantStore.Execute(connection, tx, """
            DELETE FROM AssistantAliases WHERE GroupId = $s AND Term IN (SELECT Term FROM AssistantAliases WHERE GroupId = $t);
            UPDATE OR IGNORE AssistantAliases SET GroupId = $t WHERE GroupId = $s;
            DELETE FROM AssistantAliases WHERE GroupId = $s;
            DELETE FROM AssistantHistory WHERE GroupId = $s AND Sentence IN (SELECT Sentence FROM AssistantHistory WHERE GroupId = $t);
            UPDATE AssistantHistory SET GroupId = $t WHERE GroupId = $s;
            """, p);
    }

    /// <summary>Vor dem Löschen der Quell-Person beim Zusammenführen (Verknüpfen).</summary>
    public static void MovePersonAliases(SqliteConnection connection, SqliteTransaction tx, int sourcePersonId, int targetPersonId)
        => AssistantStore.Execute(connection, tx, "UPDATE OR IGNORE AssistantAliases SET PersonId = $t WHERE PersonId = $s;", ("$s", sourcePersonId), ("$t", targetPersonId));

    /// <summary>Kopien für die herausgelöste Gruppe; braucht <c>temp._CopyMap</c> (Art „P“) des Aufrufers.</summary>
    public static void CopyForLibrary(SqliteConnection connection, SqliteTransaction tx, int fromGroup, int toGroup)
    {
        var p = new (string, object?)[] { ("$from", fromGroup), ("$to", toGroup) };
        AssistantStore.Execute(connection, tx, """
            INSERT OR IGNORE INTO AssistantAliases (GroupId, Term, PersonId, CreatedAtUtc)
            SELECT $to, a.Term, m.NewId, a.CreatedAtUtc FROM AssistantAliases a JOIN temp._CopyMap m ON m.Kind = 'P' AND m.OldId = a.PersonId
            WHERE a.GroupId = $from;
            INSERT INTO AssistantHistory (GroupId, Sentence, IntentJson, Action, ResultCount, CreatedAtUtc, LastUsedAtUtc)
            SELECT $to, Sentence, IntentJson, Action, 0, CreatedAtUtc, LastUsedAtUtc FROM AssistantHistory WHERE GroupId = $from;
            """, p);
    }

    public static void DeleteGroup(SqliteConnection connection, SqliteTransaction tx, int groupId)
        => AssistantStore.Execute(connection, tx, "DELETE FROM AssistantAliases WHERE GroupId = $g; DELETE FROM AssistantHistory WHERE GroupId = $g;", ("$g", groupId));
}

using Microsoft.Data.Sqlite;
using Lumora.Domain.Entities;

namespace Lumora.Services.Faces.Assignment;

/// <summary>
/// SQL der Personen-Details und Beziehungen (0.2.47, ADR-0045) auf rohen Verbindungen — Schreiben immer in der
/// Transaktion des Aufrufers. <c>Persons.Name</c> wird bei jedem Schreiben aus Vor-/Nachname/Rufname nachgezogen.
/// Seit 0.2.51 (ADR-0048) gehören Personen und Beziehungen einer Bibliotheks-Gruppe: Lesen, Suchen und „Das bin ich“
/// gelten je Gruppe.
/// </summary>
internal static class PersonDetailsStore
{
    /// <summary>Spalten für <see cref="ReadDetails"/> — setzt <c>Persons p LEFT JOIN PersonRelations r</c> voraus.</summary>
    public const string DetailColumns =
        "p.FirstName, p.LastName, p.Nickname, p.RelationId, r.Name, p.BirthYear, p.BirthMonth, p.BirthDay, p.IsMe";

    /// <summary>Liest <see cref="DetailColumns"/> ab Spalte <paramref name="offset"/>.</summary>
    public static PersonDetails ReadDetails(SqliteDataReader reader, int personId, int offset)
    {
        int? Int(int i) => reader.IsDBNull(offset + i) ? null : reader.GetInt32(offset + i);
        string? Text(int i) => reader.IsDBNull(offset + i) ? null : reader.GetString(offset + i);
        return new PersonDetails(
            personId,
            Text(0) ?? string.Empty,
            Text(1) ?? string.Empty,
            Text(2),
            Int(3),
            Text(4),
            PersonBirthDate.FromStored(Int(5), Int(6), Int(7)),
            !reader.IsDBNull(offset + 8) && reader.GetBoolean(offset + 8));
    }

    /// <summary>Details der Personen einer Gruppe; <paramref name="personId"/> = nur diese (leer, wenn in anderer Gruppe).</summary>
    public static List<PersonDetails> ReadAll(SqliteConnection connection, int groupId, int? personId = null)
    {
        using var cmd = FaceAssignmentStore.Command(connection, null, $"""
            SELECT p.Id, {DetailColumns}
            FROM Persons p LEFT JOIN PersonRelations r ON r.Id = p.RelationId
            WHERE p.GroupId = $group AND ($person IS NULL OR p.Id = $person)
            ORDER BY p.Name COLLATE NOCASE, p.Id;
            """, ("$group", groupId), ("$person", (object?)personId ?? DBNull.Value));
        var list = new List<PersonDetails>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadDetails(reader, reader.GetInt32(0), 1));
        }

        return list;
    }

    /// <summary>Legt eine Person in der Gruppe an (Eingabe schon normalisiert und geprüft).</summary>
    public static int InsertPerson(SqliteConnection connection, SqliteTransaction tx, int groupId, PersonDetailsInput input, int? relationId)
    {
        if (input.IsMe)
        {
            ClearIsMe(connection, tx, groupId);
        }

        FaceAssignmentStore.Execute(connection, tx, """
            INSERT INTO Persons (Name, CreatedAtUtc, CoverFaceId, IsHidden, FirstName, LastName, Nickname, RelationId,
                                 BirthYear, BirthMonth, BirthDay, IsMe, GroupId)
            VALUES ($n, $now, NULL, 0, $first, $last, $nick, $rel, $y, $m, $d, $me, $group);
            """, [.. Parameters(input, relationId, ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())), ("$group", groupId)]);
        return (int)FaceAssignmentStore.Scalar(connection, tx, "SELECT last_insert_rowid();");
    }

    public static int UpdatePerson(SqliteConnection connection, SqliteTransaction tx, int personId, PersonDetailsInput input, int? relationId)
    {
        if (input.IsMe)
        {
            ClearIsMe(connection, tx, FaceAssignmentStore.ReadPersonGroup(connection, tx, personId) ?? 0);
        }

        return FaceAssignmentStore.Execute(connection, tx, """
            UPDATE Persons SET Name = $n, FirstName = $first, LastName = $last, Nickname = $nick, RelationId = $rel,
                               BirthYear = $y, BirthMonth = $m, BirthDay = $d, IsMe = $me
            WHERE Id = $p;
            """, Parameters(input, relationId, ("$p", personId)));
    }

    /// <summary>Beziehung zum Namen in der Gruppe (ohne Groß-/Kleinschreibung) oder neu angelegt; null für leeren Namen.</summary>
    public static int? ResolveRelation(SqliteConnection connection, SqliteTransaction tx, int groupId, string? name)
    {
        if (PersonNames.CleanOrNull(name) is not { } cleaned)
        {
            return null;
        }

        if (FindRelation(connection, tx, groupId, cleaned) is { } existing)
        {
            return existing;
        }

        FaceAssignmentStore.Execute(connection, tx, "INSERT INTO PersonRelations (Name, GroupId) VALUES ($n, $g);", ("$n", cleaned), ("$g", groupId));
        return (int)FaceAssignmentStore.Scalar(connection, tx, "SELECT last_insert_rowid();");
    }

    public static int? FindRelation(SqliteConnection connection, SqliteTransaction? tx, int groupId, string name, int? exceptId = null)
    {
        using var cmd = FaceAssignmentStore.Command(connection, tx,
            "SELECT Id FROM PersonRelations WHERE GroupId = $g AND Name = $n AND Id <> $except LIMIT 1;",
            ("$g", groupId), ("$n", name), ("$except", (object?)exceptId ?? -1));
        return cmd.ExecuteScalar() is long id ? (int)id : null;
    }

    public static bool RelationExists(SqliteConnection connection, SqliteTransaction tx, int relationId)
        => FaceAssignmentStore.Scalar(connection, tx, "SELECT COUNT(*) FROM PersonRelations WHERE Id = $r;", ("$r", relationId)) > 0;

    /// <summary>Gruppe der Beziehung oder null.</summary>
    public static int? ReadRelationGroup(SqliteConnection connection, SqliteTransaction? tx, int relationId)
    {
        using var cmd = FaceAssignmentStore.Command(connection, tx, "SELECT GroupId FROM PersonRelations WHERE Id = $r;", ("$r", relationId));
        return cmd.ExecuteScalar() is long g ? (int)g : null;
    }

    public static List<PersonRelationInfo> ReadRelations(SqliteConnection connection, int groupId)
    {
        using var cmd = FaceAssignmentStore.Command(connection, null, """
            SELECT r.Id, r.Name, (SELECT COUNT(*) FROM Persons p WHERE p.RelationId = r.Id)
            FROM PersonRelations r
            WHERE r.GroupId = $g
            ORDER BY r.Name COLLATE NOCASE, r.Id;
            """, ("$g", groupId));
        var list = new List<PersonRelationInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PersonRelationInfo(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return list;
    }

    private static void ClearIsMe(SqliteConnection connection, SqliteTransaction tx, int groupId)
        => FaceAssignmentStore.Execute(connection, tx, "UPDATE Persons SET IsMe = 0 WHERE IsMe = 1 AND GroupId = $g;", ("$g", groupId));

    private static (string, object)[] Parameters(PersonDetailsInput input, int? relationId, (string, object) extra)
        =>
        [
            ("$n", input.DisplayName),
            ("$first", input.FirstName),
            ("$last", input.LastName),
            ("$nick", (object?)input.Nickname ?? DBNull.Value),
            ("$rel", (object?)relationId ?? DBNull.Value),
            ("$y", (object?)input.BirthDate?.Year ?? DBNull.Value),
            ("$m", (object?)input.BirthDate?.Month ?? DBNull.Value),
            ("$d", (object?)input.BirthDate?.Day ?? DBNull.Value),
            ("$me", input.IsMe),
            extra,
        ];
}

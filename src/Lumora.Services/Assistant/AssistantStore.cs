using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Entities;
using Lumora.Services.Database;
using Lumora.Services.Library;

namespace Lumora.Services.Assistant;

/// <summary>Eintrag „Zuletzt gefragt“.</summary>
public sealed record AssistantHistoryItem(int Id, string Sentence, string IntentJson, IntentAction Action, int ResultCount, DateTime LastUsedAtUtc);

/// <summary>Gemerkte Antwort (Einstellungen › Assistent: ansehen, löschen).</summary>
public sealed record AssistantAliasInfo(string Term, IReadOnlyList<int> PersonIds, string PersonNames);

/// <summary>Verlauf und gemerkte Antworten des Assistenten (ADR-0063) — je Bibliotheks-Gruppe, im Katalog der Sitzung.</summary>
public interface IAssistantStore
{
    Task<AssistantVocabulary> ReadVocabularyAsync(LibraryScopeInfo scope, CancellationToken ct = default);

    Task<IReadOnlyList<AssistantHistoryItem>> GetHistoryAsync(int groupId, int max = 8, CancellationToken ct = default);

    /// <summary>Satz merken (gleicher Satz: nur „zuletzt benutzt“ und Treffer aktualisieren); höchstens 50 je Gruppe.</summary>
    Task AddHistoryAsync(int groupId, string sentence, StoryIntent intent, IntentAction action, int resultCount, CancellationToken ct = default);

    Task DeleteHistoryAsync(int id, CancellationToken ct = default);

    Task<int> ClearHistoryAsync(int groupId, CancellationToken ct = default);

    /// <summary>Antwort merken: <paramref name="term"/> (normalisiert) zeigt auf die Personen (ersetzt eine frühere Antwort).</summary>
    Task SetAliasAsync(int groupId, string term, IReadOnlyList<int> personIds, CancellationToken ct = default);

    Task<IReadOnlyList<AssistantAliasInfo>> GetAliasesAsync(int groupId, CancellationToken ct = default);

    Task DeleteAliasAsync(int groupId, string term, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AssistantStore(ILibraryContextFactory factory) : IAssistantStore
{
    public const int MaxHistory = 50;

    public Task<AssistantVocabulary> ReadVocabularyAsync(LibraryScopeInfo scope, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!scope.HasLibrary)
            {
                return AssistantVocabulary.Empty;
            }

            using var connection = factory.OpenSqliteConnection();
            return ReadVocabulary(connection, scope);
        }, ct);

    internal static AssistantVocabulary ReadVocabulary(SqliteConnection connection, LibraryScopeInfo scope)
    {
        var libs = scope.SqlLibraryIdList;
        var g = scope.GroupId;
        var persons = new List<AssistantPerson>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT p.Id, p.Name, p.FirstName, p.LastName, p.Nickname, r.Name, p.BirthYear, p.BirthMonth, p.BirthDay, p.IsMe,
                       (SELECT COUNT(DISTINCT fa.FileId) FROM Faces fa CROSS JOIN Files f ON f.Id = fa.FileId
                        WHERE fa.PersonId = p.Id AND fa.State = 2 AND f.LibraryId IN ({libs}) AND f.Missing = 0),
                       COALESCE((SELECT fa.Id FROM Faces fa CROSS JOIN Files f ON f.Id = fa.FileId WHERE fa.Id = p.CoverFaceId AND f.LibraryId IN ({libs})),
                                (SELECT MIN(fa.Id) FROM Faces fa CROSS JOIN Files f ON f.Id = fa.FileId WHERE fa.PersonId = p.Id AND fa.State = 2 AND f.LibraryId IN ({libs})))
                FROM Persons p LEFT JOIN PersonRelations r ON r.Id = p.RelationId
                WHERE p.GroupId = $g AND p.IsHidden = 0
                ORDER BY p.Name COLLATE NOCASE, p.Id;
                """;
            cmd.Parameters.AddWithValue("$g", g);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                persons.Add(new AssistantPerson(
                    reader.GetInt32(0), reader.GetString(1), Text(2) ?? string.Empty, Text(3) ?? string.Empty, Text(4), Text(5),
                    PersonBirthDate.FromStored(Int(6), Int(7), Int(8)), !reader.IsDBNull(9) && reader.GetBoolean(9), reader.GetInt32(10), Int(11)));
            }
        }

        // Bibliothek der Gesichter (Ausschnitt-Cache je Bibliothek)
        var faceIds = persons.Where(p => p.FaceId is not null).Select(p => p.FaceId!.Value).ToList();
        if (faceIds.Count > 0)
        {
            var libraries = new Dictionary<int, int>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT fa.Id, f.LibraryId FROM Faces fa CROSS JOIN Files f ON f.Id = fa.FileId WHERE fa.Id IN ({IdList(faceIds)});";
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    libraries[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }

            persons = persons.Select(p => p.FaceId is { } f && libraries.TryGetValue(f, out var lib) ? p with { FaceLibraryId = lib } : p).ToList();
        }

        var tags = ReadNamed(connection, "SELECT Id, Name FROM Tags WHERE GroupId = $g ORDER BY SortOrder, Name COLLATE NOCASE;", g);
        var categories = ReadNamed(connection, "SELECT Id, Name FROM Categories WHERE GroupId = $g ORDER BY SortOrder, Name COLLATE NOCASE;", g);
        var motifs = ReadNamed(connection, "SELECT Id, Name FROM MotifTerms WHERE GroupId = $g ORDER BY SortOrder, Name COLLATE NOCASE;", g);
        var values = new List<AssistantCategoryValue>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT v.Id, v.CategoryId, v.Name, c.Name FROM CategoryValues v JOIN Categories c ON c.Id = v.CategoryId
                WHERE c.GroupId = $g ORDER BY c.SortOrder, v.SortOrder, v.Name COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$g", g);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                values.Add(new AssistantCategoryValue(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        return new AssistantVocabulary(persons, tags, categories, values, motifs, ReadAliases(connection, g));
    }

    private static List<AssistantNamedItem> ReadNamed(SqliteConnection connection, string sql, int groupId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$g", groupId);
        var list = new List<AssistantNamedItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AssistantNamedItem(reader.GetInt32(0), reader.GetString(1)));
        }

        return list;
    }

    private static Dictionary<string, IReadOnlyList<int>> ReadAliases(SqliteConnection connection, int groupId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT a.Term, a.PersonId FROM AssistantAliases a JOIN Persons p ON p.Id = a.PersonId WHERE a.GroupId = $g ORDER BY a.Term, a.PersonId;";
        cmd.Parameters.AddWithValue("$g", groupId);
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var term = reader.GetString(0);
            if (!map.TryGetValue(term, out var list))
            {
                map[term] = list = new List<int>();
            }

            list.Add(reader.GetInt32(1));
        }

        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value, StringComparer.Ordinal);
    }

    public Task<IReadOnlyList<AssistantHistoryItem>> GetHistoryAsync(int groupId, int max = 8, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<AssistantHistoryItem>>(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Sentence, IntentJson, Action, ResultCount, LastUsedAtUtc FROM AssistantHistory WHERE GroupId = $g ORDER BY LastUsedAtUtc DESC, Id DESC LIMIT $max;";
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.Parameters.AddWithValue("$max", Math.Clamp(max, 1, MaxHistory));
            var list = new List<AssistantHistoryItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AssistantHistoryItem(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), (IntentAction)reader.GetInt32(3), reader.GetInt32(4),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).UtcDateTime));
            }

            return list;
        }, ct);

    public Task AddHistoryAsync(int groupId, string sentence, StoryIntent intent, IntentAction action, int resultCount, CancellationToken ct = default)
    {
        var text = sentence.Trim();
        if (text.Length == 0)
        {
            return Task.CompletedTask;
        }

        var json = StoryIntentJson.ToJson(intent);
        return WriteAsync((c, tx) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var updated = Execute(c, tx, """
                UPDATE AssistantHistory SET IntentJson = $j, Action = $a, ResultCount = $n, LastUsedAtUtc = $now
                WHERE GroupId = $g AND Sentence = $s;
                """, ("$j", json), ("$a", (int)action), ("$n", resultCount), ("$now", now), ("$g", groupId), ("$s", text));
            if (updated == 0)
            {
                Execute(c, tx, """
                    INSERT INTO AssistantHistory (GroupId, Sentence, IntentJson, Action, ResultCount, CreatedAtUtc, LastUsedAtUtc)
                    VALUES ($g, $s, $j, $a, $n, $now, $now);
                    """, ("$j", json), ("$a", (int)action), ("$n", resultCount), ("$now", now), ("$g", groupId), ("$s", text));
            }

            Execute(c, tx, """
                DELETE FROM AssistantHistory WHERE GroupId = $g AND Id NOT IN
                    (SELECT Id FROM AssistantHistory WHERE GroupId = $g ORDER BY LastUsedAtUtc DESC, Id DESC LIMIT $max);
                """, ("$g", groupId), ("$max", MaxHistory));
            return 0;
        }, ct);
    }

    public Task DeleteHistoryAsync(int id, CancellationToken ct = default)
        => WriteAsync((c, tx) => Execute(c, tx, "DELETE FROM AssistantHistory WHERE Id = $id;", ("$id", id)), ct);

    public Task<int> ClearHistoryAsync(int groupId, CancellationToken ct = default)
        => WriteAsync((c, tx) => Execute(c, tx, "DELETE FROM AssistantHistory WHERE GroupId = $g;", ("$g", groupId)), ct);

    public Task SetAliasAsync(int groupId, string term, IReadOnlyList<int> personIds, CancellationToken ct = default)
    {
        var key = AssistantText.Normalize(term);
        if (key.Length == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync((c, tx) =>
        {
            Execute(c, tx, "DELETE FROM AssistantAliases WHERE GroupId = $g AND Term = $t;", ("$g", groupId), ("$t", key));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var personId in personIds.Distinct())
            {
                // nur Personen derselben Gruppe
                Execute(c, tx, """
                    INSERT OR IGNORE INTO AssistantAliases (GroupId, Term, PersonId, CreatedAtUtc)
                    SELECT $g, $t, p.Id, $now FROM Persons p WHERE p.Id = $p AND p.GroupId = $g;
                    """, ("$g", groupId), ("$t", key), ("$p", personId), ("$now", now));
            }

            return 0;
        }, ct);
    }

    public Task<IReadOnlyList<AssistantAliasInfo>> GetAliasesAsync(int groupId, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<AssistantAliasInfo>>(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT a.Term, a.PersonId, p.Name FROM AssistantAliases a JOIN Persons p ON p.Id = a.PersonId
                WHERE a.GroupId = $g ORDER BY a.Term, p.Name COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$g", groupId);
            var rows = new List<(string Term, int Person, string Name)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
            }

            return rows.GroupBy(r => r.Term, StringComparer.Ordinal)
                .Select(grp => new AssistantAliasInfo(grp.Key, grp.Select(r => r.Person).ToList(), string.Join(" und ", grp.Select(r => r.Name))))
                .ToList();
        }, ct);

    public Task DeleteAliasAsync(int groupId, string term, CancellationToken ct = default)
        => WriteAsync((c, tx) => Execute(c, tx, "DELETE FROM AssistantAliases WHERE GroupId = $g AND Term = $t;", ("$g", groupId), ("$t", AssistantText.Normalize(term))), ct);

    private Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken ct)
        => Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            var result = write(connection, tx);
            tx.Commit();
            return result;
        }, ct);

    internal static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    internal static string IdList(IEnumerable<int> ids)
    {
        var text = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        return text.Length == 0 ? "-1" : text;
    }
}

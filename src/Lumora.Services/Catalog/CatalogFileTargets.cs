using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Services.Database;

namespace Lumora.Services.Catalog;

/// <summary>Eine Ziel-Datei eines Schreibvorgangs mit ihrer aktuellen Id und Bibliothek.</summary>
public readonly record struct CatalogFileTarget(int FileId, int LibraryId);

/// <summary>
/// Ziel-Dateien eines Schreibvorgangs auf Benutzerdaten (Favorit/Bewertung, Tags, Kategorie-Werte), ADR-0030.
/// „Neu aufbauen“ ersetzt den Katalog in einer Transaktion und vergibt dabei NEUE FileIds (ADR-0028); eine Bewertung,
/// die währenddessen auf die Schreibsperre wartet, träfe nach dem Commit eine Id, die es nicht mehr gibt, und ginge
/// still verloren. Deshalb: vor dem Warten Ordnerpfad + Name der Ziele lesen (<see cref="Capture"/>; Lesen ist im WAL
/// nie gesperrt), unter der Schreibsperre fehlende Ids über Bibliothek + Ordnerpfad + Name auf die neuen Ids umsetzen
/// (<see cref="Resolve"/>) — dieselbe Identität wie beim Übernehmen der Benutzerdaten (ADR-0019/0023).
/// Unbekannte Ids fallen wie bisher still weg.
/// </summary>
internal sealed class CatalogFileTargets
{
    private const int ChunkSize = 500;

    private readonly Dictionary<int, Identity> _identities;

    private CatalogFileTargets(Dictionary<int, Identity> identities) => _identities = identities;

    /// <summary>Anzahl der Ziele, die beim Sichern im Katalog standen.</summary>
    public int Count => _identities.Count;

    /// <summary>
    /// Der ganze Schreibvorgang auf dem Thread-Pool: Verbindung öffnen, Ziele sichern, asynchron auf die Schreibsperre
    /// warten (<see cref="SqliteBusyRetry.BeginTransactionAsync(SqliteConnection, CancellationToken)"/>), Ziele auflösen,
    /// <paramref name="write"/> in der Transaktion ausführen, Commit. Der UI-Thread wartet nie auf SQLite.
    /// </summary>
    public static Task<T> WriteAsync<T>(
        ILibraryContextFactory factory,
        IReadOnlyCollection<int> fileIds,
        ILogger logger,
        Func<SqliteConnection, SqliteTransaction, IReadOnlyList<CatalogFileTarget>, T> write,
        CancellationToken ct)
        => Task.Run(
            async () =>
            {
                using var connection = factory.OpenSqliteConnection();
                var targets = Capture(connection, fileIds);
                using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                var files = targets.Resolve(connection, tx, logger);
                var result = write(connection, tx, files);
                tx.Commit();
                return result;
            },
            ct);

    /// <summary>Liest Bibliothek, Ordnerpfad und Name der Dateien (ohne Transaktion, vor dem Warten auf die Sperre).</summary>
    public static CatalogFileTargets Capture(SqliteConnection connection, IReadOnlyCollection<int> fileIds)
    {
        var identities = new Dictionary<int, Identity>(fileIds.Count);
        foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT f.Id, f.LibraryId, d.Path, f.Name FROM Files f JOIN Folders d ON d.Id = f.FolderId " +
                $"WHERE f.Id IN ({BindIds(cmd, chunk, "$f")});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                identities[reader.GetInt32(0)] = new Identity(reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
            }
        }

        return new CatalogFileTargets(identities);
    }

    /// <summary>
    /// Unter der Schreibsperre: die aktuellen Ids der Ziele. Vorhandene Ids bleiben; fehlende (Katalog inzwischen neu
    /// aufgebaut) werden über Bibliothek + Ordnerpfad + Name umgesetzt; was es nicht mehr gibt, fällt weg (Log).
    /// </summary>
    public IReadOnlyList<CatalogFileTarget> Resolve(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        var result = new List<CatalogFileTarget>(_identities.Count);
        var seen = new HashSet<int>();
        var missing = new List<KeyValuePair<int, Identity>>();
        foreach (var chunk in _identities.Keys.Chunk(ChunkSize))
        {
            var present = new Dictionary<int, int>(chunk.Length);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"SELECT Id, LibraryId FROM Files WHERE Id IN ({BindIds(cmd, chunk, "$f")});";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    present[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }

            foreach (var id in chunk)
            {
                if (present.TryGetValue(id, out var libraryId))
                {
                    if (seen.Add(id))
                    {
                        result.Add(new CatalogFileTarget(id, libraryId));
                    }
                }
                else
                {
                    missing.Add(new KeyValuePair<int, Identity>(id, _identities[id]));
                }
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        var remapped = 0;
        foreach (var chunk in missing.Chunk(ChunkSize))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var values = new StringBuilder(chunk.Length * 24);
            for (var i = 0; i < chunk.Length; i++)
            {
                var (oldId, identity) = (chunk[i].Key, chunk[i].Value);
                values.Append(i == 0 ? "" : ",").Append($"($o{i},$l{i},$p{i},$n{i})");
                cmd.Parameters.AddWithValue($"$o{i}", oldId);
                cmd.Parameters.AddWithValue($"$l{i}", identity.LibraryId);
                cmd.Parameters.AddWithValue($"$p{i}", identity.FolderPath);
                cmd.Parameters.AddWithValue($"$n{i}", identity.Name);
            }

            cmd.CommandText =
                $"WITH t(OldId, LibraryId, Path, Name) AS (VALUES {values}) " +
                "SELECT t.OldId, MIN(f.Id), t.LibraryId FROM t " +
                "JOIN Folders d ON d.LibraryId = t.LibraryId AND d.Path = t.Path " +
                "JOIN Files f ON f.FolderId = d.Id AND f.Name = t.Name " +
                "GROUP BY t.OldId;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var newId = reader.GetInt32(1);
                if (seen.Add(newId))
                {
                    result.Add(new CatalogFileTarget(newId, reader.GetInt32(2)));
                }

                remapped++;
            }
        }

        logger.LogInformation(
            "Katalog wurde inzwischen neu aufgebaut: {Remapped} von {Missing} Ziel-Datei(en) auf neue FileIds umgesetzt",
            remapped, missing.Count);
        if (remapped < missing.Count)
        {
            logger.LogWarning(
                "{Lost} Ziel-Datei(en) stehen nach dem Neuaufbau nicht mehr im Katalog — die Änderung entfällt für sie",
                missing.Count - remapped);
        }

        return result;
    }

    internal static string BindIds(SqliteCommand cmd, IReadOnlyList<int> ids, string prefix)
    {
        var sb = new StringBuilder(ids.Count * 6);
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var p = prefix + i;
            sb.Append(p);
            cmd.Parameters.AddWithValue(p, ids[i]);
        }

        return sb.ToString();
    }

    private sealed record Identity(int LibraryId, string FolderPath, string Name);
}

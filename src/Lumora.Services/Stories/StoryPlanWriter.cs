using System.Globalization;
using Microsoft.Data.Sqlite;
using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>
/// Schreibt einen Abgleich (<see cref="StoryReconcilePlan"/>) in der Transaktion des Aufrufers (ADR-0058): löscht Storys
/// ohne Gegenstück, setzt Signatur/Art/Orte wiedergefundener Storys neu (Benutzer-Spalten bleiben unberührt), gleicht ihre
/// automatischen Einträge ab (manuelle Zu- und Abgänge bleiben), legt neue an und zieht Zähler/Zeitraum nach.
/// </summary>
internal static class StoryPlanWriter
{
    /// <summary>Ergebnis: geänderte bzw. neue Story-Ids und gelöschte Ids.</summary>
    public sealed record Result(List<int> Changed, List<int> Deleted, int Inserted);

    public static Result Apply(SqliteConnection connection, SqliteTransaction tx, int groupId, StoryReconcilePlan plan, long now)
    {
        var changed = new HashSet<int>();
        var insertedIds = new HashSet<int>();
        var deleted = new List<int>();

        foreach (var e in plan.Delete)
        {
            DeleteStory(connection, tx, e.Id);
            deleted.Add(e.Id);
        }

        // Signaturen erst frei machen (eindeutig je Gruppe), dann setzen — zwei Storys können sie tauschen.
        foreach (var m in plan.Matches)
        {
            if (m.Existing is { } e && e.Signature != m.Detected.Signature)
            {
                StorySql.Execute(connection, tx, "UPDATE Stories SET Signature = '~' || Id WHERE Id = $id;", ("$id", e.Id));
            }
        }

        using var deleteItem = Prepare(connection, tx, "DELETE FROM StoryItems WHERE StoryId = $s AND FileId = $f AND Origin = 0;", "$s", "$f");
        using var insertItem = Prepare(connection, tx, "INSERT OR IGNORE INTO StoryItems (StoryId, FileId, Day, Origin, AddedAtUtc) VALUES ($s, $f, $d, 0, $now);", "$s", "$f", "$d", "$now");
        using var updateDay = Prepare(connection, tx, "UPDATE StoryItems SET Day = $d WHERE StoryId = $s AND FileId = $f;", "$s", "$f", "$d");
        using var insertBatch = PrepareBatch(connection, tx);

        foreach (var m in plan.Matches)
        {
            var d = m.Detected;
            int storyId;
            var itemsChanged = false;
            if (m.Existing is { } e)
            {
                storyId = e.Id;
                UpdateMeta(connection, tx, storyId, d);
                var detectedFiles = new HashSet<int>(d.Items.Count);
                foreach (var item in d.Items)
                {
                    detectedFiles.Add(item.FileId);
                    if (!e.Items.TryGetValue(item.FileId, out var current))
                    {
                        Run(insertItem, storyId, item.FileId, item.Day, now);
                        itemsChanged = true;
                    }
                    else if (current.Day != item.Day)
                    {
                        Run(updateDay, storyId, item.FileId, item.Day);
                        itemsChanged = true;
                    }
                }

                foreach (var (fileId, v) in e.Items)
                {
                    if (v.Origin == StoryItemOrigin.Auto && !detectedFiles.Contains(fileId))
                    {
                        Run(deleteItem, storyId, fileId);
                        itemsChanged = true;
                    }
                }

                var countStale = e.FileCount != e.Items.Values.Count(v => v.Origin != StoryItemOrigin.Excluded); // Dateien inzwischen entfernt
                if (itemsChanged || countStale || e.Signature != d.Signature || e.StartAt != d.StartAt || e.EndAt != d.EndAt || e.Kind != d.Kind)
                {
                    changed.Add(storyId);
                }
            }
            else
            {
                storyId = Insert(connection, tx, groupId, d, now);
                InsertNewItems(insertBatch, insertItem, storyId, d.Items, now);
                insertedIds.Add(storyId);
                changed.Add(storyId);
            }
        }

        // Neue Storys tragen ihre Zähler/Zeiträume schon aus der Erkennung (keine manuellen Einträge) — nur die übrigen nachziehen.
        var refresh = changed.Where(id => !insertedIds.Contains(id)).ToList();
        refresh.AddRange(plan.Keep.Select(k => k.Id));
        StorySql.RefreshAggregates(connection, tx, refresh, now);

        // Behaltene (nicht mehr erkannte) Storys mit Benutzer-Zustand gehen erst, wenn keine Datei mehr übrig ist.
        foreach (var k in plan.Keep)
        {
            if (StorySql.Scalar(connection, tx, "SELECT FileCount FROM Stories WHERE Id = $id;", ("$id", k.Id)) == 0)
            {
                DeleteStory(connection, tx, k.Id);
                deleted.Add(k.Id);
            }
        }

        return new Result(changed.ToList(), deleted, insertedIds.Count);
    }

    /// <summary>Story samt Einträgen (Kaskade) und gesicherten manuellen Einträgen entfernter Dateien löschen.</summary>
    public static void DeleteStory(SqliteConnection connection, SqliteTransaction tx, int storyId)
        => StorySql.Execute(connection, tx, "DELETE FROM FileLinkUndo WHERE Kind IN (2, 3) AND TargetId = $id; DELETE FROM Stories WHERE Id = $id;", ("$id", storyId));

    private static void UpdateMeta(SqliteConnection connection, SqliteTransaction tx, int storyId, DetectedStory d)
        => StorySql.Execute(connection, tx, """
            UPDATE Stories SET Kind = $k, Signature = $sig, DatePrecision = $prec, PlaceId = $place, PersonId = $person,
                               PlaceIds = $places, VisitCount = $visits, IsAway = $away
            WHERE Id = $id;
            """,
            ("$k", (int)d.Kind), ("$sig", d.Signature), ("$prec", (int)d.Precision), ("$place", d.PlaceId), ("$person", d.PersonId),
            ("$places", PlaceList(d.PlaceIds)), ("$visits", d.VisitCount), ("$away", d.IsAway), ("$id", storyId));

    public static int Insert(SqliteConnection connection, SqliteTransaction tx, int groupId, DetectedStory d, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Stories (GroupId, Kind, Signature, StartAt, EndAt, FirstDay, LastDay, DatePrecision, PlaceId, PersonId, PlaceIds,
                                 VisitCount, IsAway, CustomTitle, CoverFileId, CoverIsManual, IsPinned, PinnedAtUtc, IsHidden, IsSeen,
                                 SeenAtUtc, FileCount, VideoCount, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($g, $k, $sig, $start, $end, $first, $last, $prec, $place, $person, $places,
                    $visits, $away, NULL, NULL, 0, 0, NULL, 0, 0, NULL, $count, $videos, $now, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$k", (int)d.Kind);
        cmd.Parameters.AddWithValue("$sig", d.Signature);
        cmd.Parameters.AddWithValue("$start", d.StartAt);
        cmd.Parameters.AddWithValue("$end", d.EndAt);
        cmd.Parameters.AddWithValue("$first", d.FirstDay);
        cmd.Parameters.AddWithValue("$last", d.LastDay);
        cmd.Parameters.AddWithValue("$prec", (int)d.Precision);
        cmd.Parameters.AddWithValue("$place", (object?)d.PlaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$person", (object?)d.PersonId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$places", (object?)PlaceList(d.PlaceIds) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$visits", (object?)d.VisitCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$away", d.IsAway);
        cmd.Parameters.AddWithValue("$count", d.Items.Count);
        cmd.Parameters.AddWithValue("$videos", d.VideoCount);
        cmd.Parameters.AddWithValue("$now", now);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static string? PlaceList(IReadOnlyList<int> placeIds)
        => placeIds.Count == 0 ? null : string.Join(",", placeIds.Select(p => p.ToString(CultureInfo.InvariantCulture)));

    public static List<int> ParsePlaceList(string? text)
    {
        var list = new List<int>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                list.Add(id);
            }
        }

        return list;
    }

    private static SqliteCommand Prepare(SqliteConnection connection, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var name in names)
        {
            cmd.Parameters.Add(name, SqliteType.Integer);
        }

        return cmd;
    }

    /// <summary>Zeilen je Mehrfach-INSERT neuer Einträge (weniger Aufrufe bei der ersten Erkennung großer Bibliotheken).</summary>
    private const int BatchRows = 200;

    private static SqliteCommand PrepareBatch(SqliteConnection connection, SqliteTransaction tx)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        var values = string.Join(",", Enumerable.Range(0, BatchRows).Select(i => FormattableString.Invariant($"($s, $f{i}, $d{i}, 0, $now)")));
        cmd.CommandText = "INSERT OR IGNORE INTO StoryItems (StoryId, FileId, Day, Origin, AddedAtUtc) VALUES " + values + ";";
        cmd.Parameters.Add("$s", SqliteType.Integer);
        cmd.Parameters.Add("$now", SqliteType.Integer);
        for (var i = 0; i < BatchRows; i++)
        {
            cmd.Parameters.Add(FormattableString.Invariant($"$f{i}"), SqliteType.Integer);
            cmd.Parameters.Add(FormattableString.Invariant($"$d{i}"), SqliteType.Integer);
        }

        return cmd;
    }

    private static void InsertNewItems(SqliteCommand batch, SqliteCommand single, int storyId, IReadOnlyList<StoryTimelineItem> items, long now)
    {
        var i = 0;
        batch.Parameters[0].Value = storyId;
        batch.Parameters[1].Value = now;
        for (; i + BatchRows <= items.Count; i += BatchRows)
        {
            for (var k = 0; k < BatchRows; k++)
            {
                batch.Parameters[2 + k * 2].Value = items[i + k].FileId;
                batch.Parameters[3 + k * 2].Value = items[i + k].Day;
            }

            batch.ExecuteNonQuery();
        }

        for (; i < items.Count; i++)
        {
            Run(single, storyId, items[i].FileId, items[i].Day, now);
        }
    }

    private static void Run(SqliteCommand cmd, params long[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            cmd.Parameters[i].Value = values[i];
        }

        cmd.ExecuteNonQuery();
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lumora.Services.Duplicates;

/// <summary>Was „Angaben übertragen“ an der behaltenen Datei geändert hat — damit „Rückgängig“ genau das zurücknimmt.</summary>
public sealed record TransferLog
{
    public bool FavoriteSet { get; init; }

    public bool RatingSet { get; init; }

    public int? OldRating { get; init; }

    public int? NewRating { get; init; }

    public List<int> Tags { get; init; } = [];

    /// <summary>(Kategorie, Wert).</summary>
    public List<int[]> Categories { get; init; } = [];

    public List<FaceChange> Faces { get; init; } = [];

    /// <summary>Gesichter, die aus der byte-gleichen gelöschten Datei kopiert wurden (behaltene war noch nicht analysiert).</summary>
    public List<int> CopiedFaces { get; init; } = [];

    public bool CopiedFaceScan { get; init; }

    public bool IsEmpty => !FavoriteSet && !RatingSet && Tags.Count == 0 && Categories.Count == 0 && Faces.Count == 0 && CopiedFaces.Count == 0 && !CopiedFaceScan;

    public int Count => (FavoriteSet ? 1 : 0) + (RatingSet ? 1 : 0) + Tags.Count + Categories.Count + Faces.Count + (CopiedFaces.Count > 0 ? 1 : 0);

    public bool TouchesFaces => Faces.Count > 0 || CopiedFaces.Count > 0 || CopiedFaceScan;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static TransferLog FromJson(string? json)
        => string.IsNullOrEmpty(json) ? new TransferLog() : JsonSerializer.Deserialize<TransferLog>(json) ?? new TransferLog();
}

/// <summary>Personen-Zuordnung eines Gesichts der behaltenen Datei vor der Übertragung.</summary>
public sealed record FaceChange(int FaceId, int? OldPersonId, int OldState, int? OldSuggestedPersonId, double? OldSuggestionScore, int NewPersonId);

/// <summary>
/// „Angaben übertragen“ vor dem Löschen (ADR-0053): von der gelöschten auf die behaltene Datei — Favorit (oder), Bewertung
/// (höhere), Tags und Kategorien-Werte (fehlende; je Kategorie bleibt ein vorhandener Wert der behaltenen), Personen:
/// ist die behaltene Datei schon analysiert, bekommt ihr Gesicht mit der besten Überdeckung (IoU ≥ 0,3, relative Boxen)
/// die Person; ist sie es noch nicht und die Dateien sind byte-gleich, werden die Gesichter samt Merker kopiert (gleiche
/// Pixel → gleiche Boxen und Vektoren). Sonst zählt die Person als „nicht übertragbar“. Alles in der Transaktion des
/// Aufrufers; Tags, Kategorien und Personen gehören zur Gruppe beider Dateien (Doppelte nur im Bereich, ADR-0048).
/// </summary>
public static class MetadataTransfer
{
    private const double MinOverlap = 0.3;
    private const int ConfirmedState = 2;
    private const int IgnoredState = 3;

    public static (TransferLog Log, int PersonsMissed) Transfer(SqliteConnection connection, SqliteTransaction tx, int keep, int delete, bool exact)
    {
        var (keepFavorite, keepRating) = ReadFlags(connection, tx, keep);
        var (deleteFavorite, deleteRating) = ReadFlags(connection, tx, delete);
        var favoriteSet = false;
        if (deleteFavorite && !keepFavorite)
        {
            Execute(connection, tx, "UPDATE Files SET IsFavorite = 1 WHERE Id = $k", ("$k", keep));
            favoriteSet = true;
        }

        var ratingSet = false;
        if (deleteRating is > 0 && deleteRating > (keepRating ?? 0))
        {
            Execute(connection, tx, "UPDATE Files SET Rating = $r WHERE Id = $k", ("$k", keep), ("$r", deleteRating.Value));
            ratingSet = true;
        }

        var tags = Ints(connection, tx, "SELECT TagId FROM FileTags WHERE FileId = $d AND TagId NOT IN (SELECT TagId FROM FileTags WHERE FileId = $k) ORDER BY TagId", ("$d", delete), ("$k", keep));
        foreach (var tag in tags)
        {
            Execute(connection, tx, "INSERT OR IGNORE INTO FileTags(FileId, TagId) VALUES ($k, $t)", ("$k", keep), ("$t", tag));
        }

        var categories = Pairs(connection, tx, "SELECT CategoryId, ValueId FROM FileCategoryValues WHERE FileId = $d AND CategoryId NOT IN (SELECT CategoryId FROM FileCategoryValues WHERE FileId = $k) ORDER BY CategoryId", ("$d", delete), ("$k", keep));
        foreach (var (category, value) in categories)
        {
            Execute(connection, tx, "INSERT OR IGNORE INTO FileCategoryValues(FileId, CategoryId, ValueId) VALUES ($k, $c, $v)", ("$k", keep), ("$c", category), ("$v", value));
        }

        var faces = new List<FaceChange>();
        var copied = new List<int>();
        var copiedScan = false;
        var missed = 0;
        var keepPersons = Ints(connection, tx, "SELECT DISTINCT PersonId FROM Faces WHERE FileId = $k AND PersonId IS NOT NULL", ("$k", keep)).ToHashSet();
        var wanted = ReadFaces(connection, tx, "SELECT Id, PersonId, BoxX, BoxY, BoxW, BoxH, State, SuggestedPersonId, SuggestionScore FROM Faces WHERE FileId = $f AND PersonId IS NOT NULL", delete)
            .Where(f => !keepPersons.Contains(f.PersonId!.Value))
            .GroupBy(f => f.PersonId!.Value)
            .Select(g => g.First())
            .ToList();
        if (wanted.Count > 0)
        {
            var keepScanned = Scalar(connection, tx, "SELECT 1 FROM FaceScans WHERE FileId = $k", ("$k", keep)) is not null;
            if (keepScanned)
            {
                var free = ReadFaces(connection, tx, $"SELECT Id, PersonId, BoxX, BoxY, BoxW, BoxH, State, SuggestedPersonId, SuggestionScore FROM Faces WHERE FileId = $f AND PersonId IS NULL AND State <> {IgnoredState}", keep);
                foreach (var face in wanted)
                {
                    var best = free.Select(c => (Face: c, Overlap: Iou(face, c))).Where(x => x.Overlap >= MinOverlap).OrderByDescending(x => x.Overlap).FirstOrDefault();
                    if (best.Face is null)
                    {
                        missed++;
                        continue;
                    }

                    free.Remove(best.Face);
                    Execute(connection, tx, $"UPDATE Faces SET PersonId = $p, State = {ConfirmedState}, SuggestedPersonId = NULL, SuggestionScore = NULL WHERE Id = $id", ("$p", face.PersonId!.Value), ("$id", best.Face.Id));
                    faces.Add(new FaceChange(best.Face.Id, null, best.Face.State, best.Face.SuggestedPersonId, best.Face.SuggestionScore, face.PersonId!.Value));
                }
            }
            else if (exact && CopyFaceScan(connection, tx, delete, keep))
            {
                copiedScan = true;
                foreach (var faceId in Ints(connection, tx, "SELECT Id FROM Faces WHERE FileId = $d ORDER BY Id", ("$d", delete)))
                {
                    copied.Add(CopyFace(connection, tx, faceId, keep));
                }
            }
            else
            {
                missed += wanted.Count;
            }
        }

        var log = new TransferLog
        {
            FavoriteSet = favoriteSet,
            RatingSet = ratingSet,
            OldRating = keepRating,
            NewRating = ratingSet ? deleteRating : null,
            Tags = tags,
            Categories = categories.Select(c => new[] { c.Item1, c.Item2 }).ToList(),
            Faces = faces,
            CopiedFaces = copied,
            CopiedFaceScan = copiedScan,
        };
        return (log, missed);
    }

    /// <summary>Nimmt eine Übertragung zurück (nur, was seither unverändert ist).</summary>
    public static void Revert(SqliteConnection connection, SqliteTransaction tx, int keep, TransferLog log)
    {
        if (log.FavoriteSet)
        {
            Execute(connection, tx, "UPDATE Files SET IsFavorite = 0 WHERE Id = $k", ("$k", keep));
        }

        if (log.RatingSet && log.NewRating is { } newRating)
        {
            Execute(connection, tx, "UPDATE Files SET Rating = $old WHERE Id = $k AND Rating = $new", ("$k", keep), ("$old", (object?)log.OldRating ?? DBNull.Value), ("$new", newRating));
        }

        foreach (var tag in log.Tags)
        {
            Execute(connection, tx, "DELETE FROM FileTags WHERE FileId = $k AND TagId = $t", ("$k", keep), ("$t", tag));
        }

        foreach (var pair in log.Categories.Where(p => p.Length == 2))
        {
            Execute(connection, tx, "DELETE FROM FileCategoryValues WHERE FileId = $k AND CategoryId = $c AND ValueId = $v", ("$k", keep), ("$c", pair[0]), ("$v", pair[1]));
        }

        foreach (var face in log.Faces)
        {
            Execute(
                connection,
                tx,
                "UPDATE Faces SET PersonId = $op, State = $os, SuggestedPersonId = $sp, SuggestionScore = $ss WHERE Id = $id AND PersonId = $np",
                ("$id", face.FaceId),
                ("$op", (object?)face.OldPersonId ?? DBNull.Value),
                ("$os", face.OldState),
                ("$sp", (object?)face.OldSuggestedPersonId ?? DBNull.Value),
                ("$ss", (object?)face.OldSuggestionScore ?? DBNull.Value),
                ("$np", face.NewPersonId));
        }

        foreach (var faceId in log.CopiedFaces)
        {
            Execute(connection, tx, "DELETE FROM Faces WHERE Id = $id AND FileId = $k", ("$id", faceId), ("$k", keep));
        }

        if (log.CopiedFaceScan)
        {
            Execute(connection, tx, "DELETE FROM FaceScans WHERE FileId = $k", ("$k", keep));
        }
    }

    private sealed record FaceRow(int Id, int? PersonId, double X, double Y, double W, double H, int State, int? SuggestedPersonId, double? SuggestionScore);

    private static double Iou(FaceRow a, FaceRow b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.W, b.X + b.W);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = (a.W * a.H) + (b.W * b.H) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static List<FaceRow> ReadFaces(SqliteConnection connection, SqliteTransaction tx, string sql, int fileId)
    {
        using var cmd = Command(connection, tx, sql, ("$f", fileId));
        var list = new List<FaceRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FaceRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8)));
        }

        return list;
    }

    /// <summary>Merker der Gesichtserkennung kopieren; false, wenn die behaltene Datei inzwischen selbst einen hat.</summary>
    private static bool CopyFaceScan(SqliteConnection connection, SqliteTransaction tx, int from, int to)
    {
        var columns = Columns(connection, tx, "FaceScans").Where(c => c != "FileId").ToList();
        var list = string.Join(",", columns.Select(c => $"\"{c}\""));
        using var cmd = Command(connection, tx, $"INSERT OR IGNORE INTO FaceScans(FileId,{list}) SELECT $to,{list} FROM FaceScans WHERE FileId = $from", ("$to", to), ("$from", from));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static int CopyFace(SqliteConnection connection, SqliteTransaction tx, int faceId, int to)
    {
        var columns = Columns(connection, tx, "Faces").Where(c => c is not "Id" and not "FileId").ToList();
        var list = string.Join(",", columns.Select(c => $"\"{c}\""));
        Execute(connection, tx, $"INSERT INTO Faces(FileId,{list}) SELECT $to,{list} FROM Faces WHERE Id = $id", ("$to", to), ("$id", faceId));
        return Convert.ToInt32(Scalar(connection, tx, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
    }

    private static List<string> Columns(SqliteConnection connection, SqliteTransaction tx, string table)
    {
        using var cmd = Command(connection, tx, "SELECT name FROM pragma_table_info($t)", ("$t", table));
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static (bool Favorite, int? Rating) ReadFlags(SqliteConnection connection, SqliteTransaction tx, int fileId)
    {
        using var cmd = Command(connection, tx, "SELECT IsFavorite, Rating FROM Files WHERE Id = $id", ("$id", fileId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Datei {fileId} nicht im Katalog.");
        }

        return (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private static List<int> Ints(SqliteConnection connection, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = Command(connection, tx, sql, args);
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    private static List<(int, int)> Pairs(SqliteConnection connection, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = Command(connection, tx, sql, args);
        var list = new List<(int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return list;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = Command(connection, tx, sql, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = Command(connection, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }
}

using Microsoft.Data.Sqlite;
using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Wpf.Tests.Persons;

/// <summary>Schreibt Personen und Gesichter (Fake-Daten, kein Foto) direkt per SQL in eine migrierte Test-DB.</summary>
internal static class FaceTestData
{
    public static int InsertPerson(SqliteConnection connection, string name, bool hidden = false)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Persons (Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES ($n, 1, NULL, $h); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$h", hidden ? 1 : 0);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static int InsertFace(
        SqliteConnection connection,
        int fileId,
        FaceState state,
        int? personId = null,
        int? suggestedPersonId = null,
        float x = 0.25f,
        float y = 0.2f,
        float w = 0.2f,
        float h = 0.3f)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality,
                Embedding, ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
            VALUES ($file, $x, $y, $w, $h, $lm, 0.9, 120, 50, 0.8, $emb, 'test/v1', $p, $s, $sp, $score, 1);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$w", w);
        cmd.Parameters.AddWithValue("$h", h);
        cmd.Parameters.AddWithValue("$lm", new byte[40]);
        cmd.Parameters.AddWithValue("$emb", new byte[2048]);
        cmd.Parameters.AddWithValue("$p", (object?)personId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (int)state);
        cmd.Parameters.AddWithValue("$sp", (object?)suggestedPersonId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$score", suggestedPersonId is null ? DBNull.Value : 0.7f);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}

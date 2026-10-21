using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Enums;
using Lumora.Services.Database;

namespace Lumora.Services.Faces.Indexing;

/// <summary>Ein Foto, das der Index (neu) analysieren soll.</summary>
public sealed record FaceIndexCandidate(int FileId, string Path);

/// <summary>Ergebnis einer Datei: Analyse oder Fehlercode (dann keine Gesichter, aber ein FaceScans-Eintrag).</summary>
public sealed record FaceFileResult(int FileId, string ModelVersion, FaceAnalysis? Analysis, string? ErrorCode = null);

/// <summary>
/// SQL des Gesichts-Index (ADR-0035): offene Fotos lesen, Ergebnisse stapelweise in EINER Transaktion schreiben
/// (Single-Writer über die Schreibsperre von SQLite, Wartelogik <see cref="SqliteBusyRetry"/> — ADR-0030), Index einer
/// Bibliothek zurücksetzen. Der Index wartet still: seine Wiederholung meldet sich nicht in der Statusleiste („Änderung
/// wartet…“ ist für Benutzeraktionen gedacht).
/// </summary>
public sealed class FaceIndexStore
{
    private readonly ILibraryContextFactory _factory;
    private readonly ILogger _logger;
    private readonly SqliteBusyRetry _retry;

    public FaceIndexStore(ILibraryContextFactory factory, ILogger logger, SqliteBusyRetryOptions? retryOptions = null)
    {
        _factory = factory;
        _logger = logger;
        _retry = new SqliteBusyRetry(retryOptions, new WeakReferenceMessenger(), logger);
    }

    /// <summary>Analysierte (aktuelle Modell-Version) und alle Fotos der Bibliothek (Bilder, nicht „fehlt“).</summary>
    public Task<(int Done, int Total)> CountAsync(int libraryId, string modelVersion, CancellationToken ct)
        => Task.Run(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                var total = Scalar(connection, null,
                    "SELECT COUNT(*) FROM Files WHERE LibraryId = $lib AND MediaType = 0 AND Missing = 0;",
                    ("$lib", libraryId));
                var done = Scalar(connection, null,
                    "SELECT COUNT(*) FROM Files f JOIN FaceScans s ON s.FileId = f.Id " +
                    "WHERE f.LibraryId = $lib AND f.MediaType = 0 AND f.Missing = 0 AND s.ModelVersion = $ver;",
                    ("$lib", libraryId), ("$ver", modelVersion));
                return ((int)done, (int)total);
            },
            ct);

    /// <summary>
    /// Nächste offene Fotos (ohne FaceScans-Eintrag oder mit anderer Modell-Version), neueste zuerst (Id absteigend),
    /// unterhalb von <paramref name="beforeId"/> — ein Lauf geht jede Datei höchstens einmal an.
    /// </summary>
    public Task<IReadOnlyList<FaceIndexCandidate>> ReadPendingAsync(int libraryId, string modelVersion, int? beforeId, int limit, CancellationToken ct)
        => Task.Run<IReadOnlyList<FaceIndexCandidate>>(
            () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT f.Id, d.Path, f.Name
                    FROM Files f
                    JOIN Folders d ON d.Id = f.FolderId
                    LEFT JOIN FaceScans s ON s.FileId = f.Id
                    WHERE f.LibraryId = $lib AND f.MediaType = 0 AND f.Missing = 0 AND f.Id < $before
                      AND (s.FileId IS NULL OR s.ModelVersion <> $ver)
                    ORDER BY f.Id DESC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$lib", libraryId);
                cmd.Parameters.AddWithValue("$ver", modelVersion);
                cmd.Parameters.AddWithValue("$before", beforeId ?? int.MaxValue);
                cmd.Parameters.AddWithValue("$limit", limit);
                var list = new List<FaceIndexCandidate>(limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new FaceIndexCandidate(reader.GetInt32(0), Path.Combine(reader.GetString(1), reader.GetString(2))));
                }

                return list;
            },
            ct);

    /// <summary>
    /// Schreibt die Ergebnisse in einer Transaktion: Gesichter abgleichen (<see cref="FaceRowMerger"/>), FaceScans
    /// setzen. Dateien, die inzwischen nicht mehr im Katalog stehen (Papierkorb, „Neu aufbauen“ mit neuen FileIds),
    /// fallen still weg. Liefert die FileIds, die geschrieben wurden.
    /// </summary>
    public Task<IReadOnlyList<int>> WriteAsync(IReadOnlyList<FaceFileResult> results, CancellationToken ct)
        => Task.Run<IReadOnlyList<int>>(
            async () =>
            {
                if (results.Count == 0)
                {
                    return [];
                }

                using var connection = _factory.OpenSqliteConnection();
                using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                var written = new List<int>(results.Count);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // Mindestgröße (ADR-0040): neue Gesichter darunter werden gleich automatisch ignoriert eingefügt.
                var minSize = FaceAutoIgnore.ReadMinSize(connection, tx);
                foreach (var result in results)
                {
                    if (Scalar(connection, tx, "SELECT COUNT(*) FROM Files WHERE Id = $id;", ("$id", result.FileId)) == 0)
                    {
                        continue;
                    }

                    WriteFile(connection, tx, result, now, minSize);
                    written.Add(result.FileId);
                }

                tx.Commit();
                if (written.Count < results.Count)
                {
                    _logger.LogDebug("Gesichts-Index: {Dropped} Ergebnis(se) verworfen — Datei nicht mehr im Katalog", results.Count - written.Count);
                }

                return written;
            },
            ct);

    /// <summary>„Index neu aufbauen“: FaceScans der Bibliothek löschen; die Gesichter bleiben und werden beim erneuten Analysieren abgeglichen.</summary>
    public Task<int> ResetScansAsync(int libraryId, CancellationToken ct)
        => Task.Run(
            async () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var tx = await _retry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM FaceScans WHERE FileId IN (SELECT Id FROM Files WHERE LibraryId = $lib);";
                cmd.Parameters.AddWithValue("$lib", libraryId);
                var count = cmd.ExecuteNonQuery();
                tx.Commit();
                return count;
            },
            ct);

    private static void WriteFile(SqliteConnection connection, SqliteTransaction tx, FaceFileResult result, long now, int minSize)
    {
        var analysis = result.ErrorCode is null ? result.Analysis : null;
        var faceCount = 0;
        var existing = ReadExisting(connection, tx, result.FileId);
        // Fehler (nicht dekodierbar): wie „keine Gesichter“ abgleichen — veraltete unbestätigte Gesichter verschwinden,
        // bestätigte bleiben (FaceRowMerger).
        List<RelativeBox> boxes = analysis is null
            ? []
            : analysis.Faces.Select(f => ToRelative(f.Detection, analysis.ImageWidth, analysis.ImageHeight)).ToList();
        var plan = FaceRowMerger.Plan(existing, boxes);
        foreach (var id in plan.DeleteIds)
        {
            // Persons.CoverFaceId hat keinen Fremdschlüssel (ADR-0035) — Titelbild auf ein gelöschtes Gesicht lösen.
            Execute(connection, tx, "UPDATE Persons SET CoverFaceId = NULL WHERE CoverFaceId = $id;", ("$id", id));
            Execute(connection, tx, "DELETE FROM Faces WHERE Id = $id;", ("$id", id));
        }

        if (analysis is not null)
        {

            foreach (var (index, old) in plan.Updates)
            {
                var (state, suggested, score) = FaceRowMerger.CarryState(old);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE Faces SET BoxX = $x, BoxY = $y, BoxW = $w, BoxH = $h, Landmarks = $lm, DetScore = $det,
                        Size = $size, Sharpness = $sharp, Quality = $q, Embedding = $emb, ModelVersion = $ver,
                        State = $state, SuggestedPersonId = $sp, SuggestionScore = $ss
                    WHERE Id = $id;
                    """;
                BindFace(cmd, analysis.Faces[index], boxes[index], analysis, result.ModelVersion);
                cmd.Parameters.AddWithValue("$state", (int)state);
                cmd.Parameters.AddWithValue("$sp", (object?)suggested ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ss", (object?)score ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", old.Id);
                cmd.ExecuteNonQuery();
            }

            foreach (var index in plan.Inserts)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality,
                        Embedding, ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
                    VALUES ($file, $x, $y, $w, $h, $lm, $det, $size, $sharp, $q, $emb, $ver, NULL, $state, NULL, $auto, $now);
                    """;
                BindFace(cmd, analysis.Faces[index], boxes[index], analysis, result.ModelVersion);
                var tooSmall = FaceAutoIgnore.Applies(minSize, analysis.Faces[index].Quality.Size);
                cmd.Parameters.AddWithValue("$file", result.FileId);
                cmd.Parameters.AddWithValue("$state", (int)(tooSmall ? FaceState.Ignored : FaceState.Unconfirmed));
                cmd.Parameters.AddWithValue("$auto", tooSmall ? (double)FaceAutoIgnore.AutoMarker : DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            faceCount = analysis.Faces.Count;
        }

        Execute(connection, tx, """
            INSERT INTO FaceScans (FileId, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode)
            VALUES ($file, $ver, $now, $count, $err)
            ON CONFLICT(FileId) DO UPDATE SET ModelVersion = excluded.ModelVersion, ScannedAtUtc = excluded.ScannedAtUtc,
                FaceCount = excluded.FaceCount, ErrorCode = excluded.ErrorCode;
            """,
            ("$file", result.FileId), ("$ver", result.ModelVersion), ("$now", now), ("$count", faceCount),
            ("$err", (object?)result.ErrorCode ?? DBNull.Value));
    }

    private static List<ExistingFace> ReadExisting(SqliteConnection connection, SqliteTransaction tx, int fileId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, BoxX, BoxY, BoxW, BoxH, State, PersonId, SuggestedPersonId, SuggestionScore FROM Faces WHERE FileId = $file ORDER BY Id;";
        cmd.Parameters.AddWithValue("$file", fileId);
        using var reader = cmd.ExecuteReader();
        var list = new List<ExistingFace>();
        while (reader.Read())
        {
            list.Add(new ExistingFace(
                reader.GetInt32(0),
                new RelativeBox(reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4)),
                (FaceState)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetFloat(8)));
        }

        return list;
    }

    /// <summary>Box in Bildanteilen, auf das Bild geklemmt.</summary>
    public static RelativeBox ToRelative(FaceDetection d, int width, int height)
    {
        var x1 = Math.Clamp(d.X1 / width, 0f, 1f);
        var y1 = Math.Clamp(d.Y1 / height, 0f, 1f);
        var x2 = Math.Clamp(d.X2 / width, 0f, 1f);
        var y2 = Math.Clamp(d.Y2 / height, 0f, 1f);
        return new RelativeBox(x1, y1, Math.Max(0f, x2 - x1), Math.Max(0f, y2 - y1));
    }

    /// <summary>Landmarken in Bildanteilen (nicht geklemmt) als 10 float32.</summary>
    public static byte[] EncodeLandmarks(FaceDetection d, int width, int height)
    {
        var values = new float[d.Landmarks.Length * 2];
        for (var i = 0; i < d.Landmarks.Length; i++)
        {
            values[i * 2] = d.Landmarks[i].X / width;
            values[i * 2 + 1] = d.Landmarks[i].Y / height;
        }

        return FaceEmbeddingCodec.EncodeFloats(values);
    }

    private static void BindFace(SqliteCommand cmd, AnalyzedFace face, RelativeBox box, FaceAnalysis analysis, string modelVersion)
    {
        cmd.Parameters.AddWithValue("$x", box.X);
        cmd.Parameters.AddWithValue("$y", box.Y);
        cmd.Parameters.AddWithValue("$w", box.W);
        cmd.Parameters.AddWithValue("$h", box.H);
        cmd.Parameters.AddWithValue("$lm", EncodeLandmarks(face.Detection, analysis.ImageWidth, analysis.ImageHeight));
        cmd.Parameters.AddWithValue("$det", face.Quality.DetScore);
        cmd.Parameters.AddWithValue("$size", face.Quality.Size);
        cmd.Parameters.AddWithValue("$sharp", face.Quality.Sharpness);
        cmd.Parameters.AddWithValue("$q", face.Quality.Quality);
        cmd.Parameters.AddWithValue("$emb", FaceEmbeddingCodec.Encode(face.Embedding));
        cmd.Parameters.AddWithValue("$ver", modelVersion);
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }
}

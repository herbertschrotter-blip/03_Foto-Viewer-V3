using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Recognition;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Faces.Assignment;

/// <summary>
/// Migrierte Test-DB mit Fotos und synthetischen Fingerabdrücken (ADR-0037): je „Person“ (Cluster) ein zufälliges
/// Zentrum, Gesichter mit Kosinus 0,80–0,90 dazu (Paar ~0,64–0,81, wie ArcFace), fremde Cluster ~0. Kein Modell nötig.
/// </summary>
internal sealed class FaceAssignmentWorld : IDisposable
{
    public const string Version = "fake/v1";

    private readonly Random _random = new(4242);
    private readonly Dictionary<string, float[]> _centers = [];
    private int _fileCounter;

    private FaceAssignmentWorld(TempLibraryFixture fx, int libraryId)
    {
        Fx = fx;
        LibraryId = libraryId;
    }

    public TempLibraryFixture Fx { get; }

    public int LibraryId { get; }

    public static async Task<FaceAssignmentWorld> CreateAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        return new FaceAssignmentWorld(fx, library.Id);
    }

    public FaceRecognitionService CreateService(IFaceRecognizer? recognizer = null, bool start = true)
    {
        var service = new FaceRecognitionService(
            Fx.ContextFactory,
            Fx.CreateLibraryService(),
            recognizer ?? new FaceRecognizer(),
            new FaceClusterer(),
            NullLogger<FaceRecognitionService>.Instance,
            Fx.Messenger,
            new FaceRecognitionOptions
            {
                StartDelay = TimeSpan.Zero,
                Debounce = TimeSpan.FromMilliseconds(10),
                SuggestBatchSize = 5,
            });
        if (start)
        {
            service.Start();
        }

        return service;
    }

    /// <summary>Legt Fotos an (Datei + Scan) — eins je Gesicht, damit FileIds unterscheidbar sind.</summary>
    public async Task<List<int>> CreateFilesAsync(int count)
    {
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var name = $"foto{++_fileCounter:D4}.jpg";
            Fx.WriteLibraryFile(name);
            names.Add(name);
        }

        await Fx.CreateScanService().ScanAsync(LibraryId, Services.Scanning.ScanMode.Incremental, null, CancellationToken.None);
        var ids = new List<int>();
        foreach (var name in names)
        {
            ids.Add((await Fx.FindFileAsync(name))!.Id);
        }

        return ids;
    }

    /// <summary>Fingerabdruck eines Gesichts von „Person“ <paramref name="cluster"/>; <paramref name="cos"/> = Ähnlichkeit zum Zentrum.</summary>
    public float[] Embedding(string cluster, float? cos = null)
    {
        var center = Center(cluster);
        var c = cos ?? 0.80f + (float)_random.NextDouble() * 0.10f;
        var noise = RandomUnit();
        // Rauschen orthogonal zum Zentrum machen, damit der Kosinus genau c ist
        var dot = Dot(noise, center);
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] -= dot * center[i];
        }

        Normalize(noise);
        var s = MathF.Sqrt(1 - c * c);
        var v = new float[center.Length];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = c * center[i] + s * noise[i];
        }

        Normalize(v);
        return v;
    }

    /// <summary>Mischung zweier Cluster (Gesicht, das beiden ähnelt).</summary>
    public float[] Mix(string a, float wa, string b, float wb)
    {
        var ca = Center(a);
        var cb = Center(b);
        var noise = RandomUnit();
        var rest = MathF.Sqrt(Math.Max(0.05f, 1 - wa * wa - wb * wb));
        var v = new float[ca.Length];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = wa * ca[i] + wb * cb[i] + rest * noise[i];
        }

        Normalize(v);
        return v;
    }

    /// <summary>Gesichter-Zeile direkt in die DB (wie der Index), liefert die Face-Id.</summary>
    public long InsertFace(int fileId, float[] embedding, float quality = 0.9f, FaceState state = FaceState.Unconfirmed, int? personId = null, float boxX = 0.3f)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality,
                Embedding, ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
            VALUES ($file, $x, 0.2, 0.3, 0.4, $lm, 0.9, 100, 200, $q, $emb, $ver, $p, $state, NULL, NULL, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$x", boxX);
        cmd.Parameters.AddWithValue("$lm", new byte[40]);
        cmd.Parameters.AddWithValue("$q", quality);
        cmd.Parameters.AddWithValue("$emb", FaceEmbeddingCodec.Encode(embedding));
        cmd.Parameters.AddWithValue("$ver", Version);
        cmd.Parameters.AddWithValue("$p", (object?)personId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", (int)state);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Je ein Foto mit einem Gesicht des Clusters; liefert die Face-Ids.</summary>
    public async Task<List<long>> AddFacesAsync(string cluster, int count, float? cos = null)
    {
        var files = await CreateFilesAsync(count);
        return files.Select(f => InsertFace(f, Embedding(cluster, cos))).ToList();
    }

    public FaceRow Face(long faceId)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT State, PersonId, SuggestedPersonId, SuggestionScore, FileId FROM Faces WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", faceId);
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue($"Gesicht {faceId} existiert");
        return new FaceRow(
            (FaceState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFloat(3),
            reader.GetInt32(4));
    }

    public long Scalar(string sql, params (string, object)[] parameters)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    public SqliteConnection OpenConnection() => Fx.ContextFactory.OpenSqliteConnection();

    private float[] Center(string cluster)
    {
        if (!_centers.TryGetValue(cluster, out var center))
        {
            _centers[cluster] = center = RandomUnit();
        }

        return center;
    }

    private float[] RandomUnit()
    {
        var v = new float[FaceEmbeddingCodec.Dimensions];
        for (var i = 0; i < v.Length; i++)
        {
            // Box-Muller: Normalverteilung → gleichverteilte Richtung
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        Normalize(v);
        return v;
    }

    private static float Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return (float)sum;
    }

    private static void Normalize(float[] v) => FaceEmbeddingCodec.NormalizeL2(v);

    public void Dispose() => Fx.Dispose();
}

internal sealed record FaceRow(FaceState State, int? PersonId, int? SuggestedPersonId, float? SuggestionScore, int FileId);

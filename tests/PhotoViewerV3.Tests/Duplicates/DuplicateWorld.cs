using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Duplicates;

/// <summary>Papierkorb-Fake mit Zurückholen: verschiebt in einen Ablage-Ordner und merkt sich den Ursprungspfad.</summary>
internal sealed class StashRecycleBin(string stashDir) : IRecycleBin
{
    private readonly Dictionary<string, (string Stash, DateTime DeletedUtc)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    /// <summary>Dateinamen, deren Papierkorb-Schritt scheitert (gesperrte Datei).</summary>
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void MoveToRecycleBin(string filePath)
    {
        if (Locked.Contains(Path.GetFileName(filePath)))
        {
            throw new IOException("Datei ist gesperrt.");
        }

        Directory.CreateDirectory(stashDir);
        var stash = Path.Combine(stashDir, Guid.NewGuid().ToString("N"));
        File.Move(filePath, stash);
        _entries[filePath] = (stash, DateTime.UtcNow);
    }

    public void RestoreFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
    {
        if (!_entries.Remove(originalPath, out var entry) || entry.DeletedUtc < deletedAfterUtc.AddMinutes(-2))
        {
            throw new FileNotFoundException("nicht im Papierkorb", originalPath);
        }

        File.Move(entry.Stash, originalPath);
    }
}

/// <summary>Gemeinsamer Aufbau der Doppelten-Tests: Bibliothek mit Eingang, Dateien, Dienste, Hilfs-SQL.</summary>
internal sealed class DuplicateWorld : IAsyncDisposable
{
    private DuplicateWorld(TempLibraryFixture fx, Library library, StashRecycleBin bin)
    {
        Fx = fx;
        Library = library;
        RecycleBin = bin;
        Gate = new ScanGate(Fx.Messenger);
        Loader = new FakeImageLoader();
        FileOps = fx.CreateFileOperationService(bin, Gate);
        Index = new DuplicateIndexService(fx.ContextFactory, NullLogger<DuplicateIndexService>.Instance, Loader, Gate, fx.Messenger) { StartDelay = Timeout.InfiniteTimeSpan };
        Queries = new DuplicateService(fx.ContextFactory, NullLogger<DuplicateService>.Instance);
        Cleanup = new DuplicateCleanupService(fx.ContextFactory, FileOps, NullLogger<DuplicateCleanupService>.Instance, Gate, fx.Messenger);
    }

    public TempLibraryFixture Fx { get; }

    public Library Library { get; }

    public StashRecycleBin RecycleBin { get; }

    public ScanGate Gate { get; }

    public FakeImageLoader Loader { get; }

    public FileOperationService FileOps { get; }

    public DuplicateIndexService Index { get; }

    public DuplicateService Queries { get; }

    public DuplicateCleanupService Cleanup { get; }

    public static async Task<DuplicateWorld> CreateAsync(bool inbox = true)
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        if (inbox)
        {
            await fx.AddInboxRootAsync(library.Id);
        }

        return new DuplicateWorld(fx, library, new StashRecycleBin(Path.Combine(fx.RootDir, "papierkorb")));
    }

    /// <summary>Datei mit bestimmtem Inhalt in Bibliothek bzw. Eingang.</summary>
    public string Write(string relative, byte[] content, bool inInbox = false)
    {
        var path = Path.GetFullPath(Path.Combine(inInbox ? Fx.InboxDir : Fx.LibraryRoot, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>JPEG-artiger Inhalt (Signatur + Muster) in einer Länge, abhängig von <paramref name="seed"/>.</summary>
    public static byte[] Content(int seed, int length = 4096)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    public Task ScanAsync() => Fx.ScanAsync(Library.Id);

    /// <summary>Incremental-Scan nach dem ersten Scan (neue Dateien aufnehmen).</summary>
    public Task RescanAsync() => Fx.CreateScanService().ScanAsync(Library.Id, PhotoViewerV3.Services.Scanning.ScanMode.Incremental, null, CancellationToken.None);

    public async Task<int> IdAsync(string name) => (await Fx.FindFileAsync(name))!.Id;

    public SqliteConnection Open() => Fx.ContextFactory.OpenSqliteConnection();

    public void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public List<int> Ints(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }

        return list;
    }

    /// <summary>Gesicht (relative Box) einfügen; liefert die Id.</summary>
    public int InsertFace(int fileId, double x, double y, double w, double h, int? personId, int state = 2)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality,
                Embedding, ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
            VALUES ($file, $x, $y, $w, $h, $lm, 0.9, 100, 50, 0.8, $emb, 'v', $p, $s, NULL, NULL, 1);
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
        cmd.Parameters.AddWithValue("$s", state);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        Index.Dispose();
        FileOps.Dispose();
        Fx.Dispose();
        return ValueTask.CompletedTask;
    }
}

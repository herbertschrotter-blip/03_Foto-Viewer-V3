using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Data;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Scanning;

namespace PhotoViewerV3.Tests.Fixtures;

/// <summary>
/// Temporäre Umgebung pro Test: eigenes Verzeichnis mit AppData (DB + Thumbnail-Cache),
/// Library-Root und einem Ordner außerhalb der Library. Die DB entsteht über die echte
/// Migration (<see cref="DatabaseInitializer"/>). Eigener Messenger, damit parallel laufende
/// Tests sich keine Nachrichten zuspielen. Dispose räumt alles weg.
/// </summary>
public sealed class TempLibraryFixture : IDisposable
{
    private TempLibraryFixture()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "PhotoViewerV3.Tests", Guid.NewGuid().ToString("N"));
        LibraryRoot = Path.Combine(RootDir, "library");
        OutsideDir = Path.Combine(RootDir, "outside");
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(OutsideDir);

        Paths = new TestAppDataPaths(Path.Combine(RootDir, "appdata"));
        Paths.EnsureDirectoriesExist();
        ContextFactory = new LibraryContextFactory(Paths, new SqlitePragmaInterceptor());
        Messages = new MessageCollector(Messenger);
    }

    public string RootDir { get; }

    public string LibraryRoot { get; }

    public string OutsideDir { get; }

    public TestAppDataPaths Paths { get; }

    public LibraryContextFactory ContextFactory { get; }

    public MediaFormatRegistry Formats { get; } = new();

    public WeakReferenceMessenger Messenger { get; } = new();

    public MessageCollector Messages { get; }

    public static async Task<TempLibraryFixture> CreateAsync()
    {
        var fixture = new TempLibraryFixture();
        var initializer = new DatabaseInitializer(fixture.ContextFactory, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        return fixture;
    }

    public LibraryContext CreateContext() => ContextFactory.Create();

    public async Task<Library> CreateLibraryAsync(string name = "Test")
    {
        await using var ctx = CreateContext();
        var library = new Library
        {
            Name = name,
            RootPath = LibraryRoot,
            CreatedAt = DateTime.UtcNow,
            LastOpenedAt = DateTime.UtcNow,
        };
        ctx.Libraries.Add(library);
        await ctx.SaveChangesAsync();
        return library;
    }

    /// <summary>Legt eine Datei unterhalb der Library-Root an (Dummy-Bytes mit JPEG-Signatur).</summary>
    public string WriteLibraryFile(string relativePath, int size = 2048)
        => WriteFile(Path.Combine(LibraryRoot, relativePath), size);

    public static string WriteFile(string fullPath, int size = 2048)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var bytes = new byte[Math.Max(size, 4)];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        for (var i = 4; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    /// <summary>Fresh-Scan der Library über den echten ScanService (Enumerator + BulkWriter + FTS).</summary>
    public Task<ScanResult> ScanAsync(int libraryId)
    {
        var scanner = new ScanService(
            ContextFactory,
            new FolderEnumerator(Formats, NullLogger<FolderEnumerator>.Instance),
            new BulkScanWriter(),
            new FtsIndexer(),
            NullLogger<ScanService>.Instance);
        return scanner.ScanAsync(libraryId, ScanMode.Fresh, null, CancellationToken.None);
    }

    public FileOperationService CreateFileOperationService(IRecycleBin? recycleBin = null)
        => new(ContextFactory, Paths, recycleBin ?? new FakeRecycleBin(), Formats, NullLogger<FileOperationService>.Instance, Messenger);

    public CatalogUpdateService CreateCatalogUpdateService()
        => new(ContextFactory, NullLogger<CatalogUpdateService>.Instance, Messenger);

    public async Task<MediaFile?> FindFileAsync(string name)
    {
        await using var ctx = CreateContext();
        return await ctx.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Name == name);
    }

    public async Task<MediaFile?> FindFileByIdAsync(int id)
    {
        await using var ctx = CreateContext();
        return await ctx.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task<Folder?> FindFolderAsync(string fullPath)
    {
        await using var ctx = CreateContext();
        return await ctx.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == fullPath);
    }

    public async Task<OperationBatch?> GetBatchAsync(int batchId)
    {
        await using var ctx = CreateContext();
        return await ctx.OperationBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId);
    }

    public async Task<List<FileOperation>> GetOperationsAsync(int batchId)
    {
        await using var ctx = CreateContext();
        return await ctx.FileOperations.AsNoTracking().Where(o => o.BatchId == batchId).OrderBy(o => o.Id).ToListAsync();
    }

    public async Task<List<Thumbnail>> GetThumbnailRowsAsync(int fileId)
    {
        await using var ctx = CreateContext();
        return await ctx.Thumbnails.AsNoTracking().Where(t => t.FileId == fileId).ToListAsync();
    }

    /// <summary>rowids (= Files.Id) aller FTS-Treffer für einen Suchbegriff.</summary>
    public List<int> FtsRowIds(string term)
    {
        using var connection = ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM FilesFts WHERE FilesFts MATCH $term";
        cmd.Parameters.AddWithValue("$term", "\"" + term + "\"");
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add((int)reader.GetInt64(0));
        }

        return ids;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDir))
                {
                    Directory.Delete(RootDir, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

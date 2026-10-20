using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Data;
using PhotoViewerV3.Data.RawSql;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Catalog;
using PhotoViewerV3.Services.Categories;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Media;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Tags;
using PhotoViewerV3.Services.Watch;

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
        InboxDir = Path.Combine(RootDir, "inbox");
        OutsideDir = Path.Combine(RootDir, "outside");
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(OutsideDir);

        Paths = new TestAppDataPaths(Path.Combine(RootDir, "appdata"));
        Paths.EnsureDirectoriesExist();
        ContextFactory = new LibraryContextFactory(Paths, new SqlitePragmaInterceptor());
        Messages = new MessageCollector(Messenger);
    }

    public string RootDir { get; }

    /// <summary>Archiv-Wurzel der Test-Library (ADR-0020); entspricht Libraries.RootPath.</summary>
    public string LibraryRoot { get; }

    /// <summary>Zweiter Ordner neben der Archiv-Wurzel — per <see cref="AddInboxRootAsync"/> als Eingangs-Wurzel nutzbar.</summary>
    public string InboxDir { get; }

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

    /// <summary>Library mit <see cref="LibraryRoot"/> als Archiv-Wurzel (wie <see cref="LibraryService.CreateAsync"/>, ADR-0020).</summary>
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
        ctx.LibraryRoots.Add(new LibraryRoot
        {
            LibraryId = library.Id,
            Path = LibraryRoot,
            Role = LibraryRootRole.Archive,
            Name = "library",
            SortOrder = 0,
        });
        await ctx.SaveChangesAsync();
        return library;
    }

    /// <summary>Fügt <see cref="InboxDir"/> (oder einen anderen Ordner) als Eingangs-Wurzel hinzu.</summary>
    public async Task<LibraryRoot> AddInboxRootAsync(int libraryId, string? path = null)
    {
        await using var ctx = CreateContext();
        var root = new LibraryRoot
        {
            LibraryId = libraryId,
            Path = path ?? InboxDir,
            Role = LibraryRootRole.Inbox,
            Name = Path.GetFileName(path ?? InboxDir),
            SortOrder = await ctx.LibraryRoots.CountAsync(r => r.LibraryId == libraryId),
        };
        ctx.LibraryRoots.Add(root);
        await ctx.SaveChangesAsync();
        return root;
    }

    public LibraryService CreateLibraryService()
        => new(ContextFactory, Paths, NullLogger<LibraryService>.Instance, Messenger);

    /// <summary>Legt eine Datei unterhalb der Library-Root an (Dummy-Bytes mit JPEG-Signatur).</summary>
    public string WriteLibraryFile(string relativePath, int size = 2048)
        => WriteFile(Path.Combine(LibraryRoot, relativePath), size);

    /// <summary>Legt eine Datei unterhalb des Eingangs-Ordners an.</summary>
    public string WriteInboxFile(string relativePath, int size = 2048)
        => WriteFile(Path.Combine(InboxDir, relativePath), size);

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
        => CreateScanService().ScanAsync(libraryId, ScanMode.Fresh, null, CancellationToken.None);

    /// <summary>ScanService mit dem Messenger der Fixture (Enumerator + BulkWriter + FTS + LibraryService).</summary>
    /// <param name="scanGate">Gemeinsame Scan-Sperre wie in der App (sonst eine eigene).</param>
    public ScanService CreateScanService(IScanGate? scanGate = null) => new(
        ContextFactory,
        new FolderEnumerator(Formats, NullLogger<FolderEnumerator>.Instance),
        new BulkScanWriter(),
        new FtsIndexer(),
        CreateLibraryService(),
        NullLogger<ScanService>.Instance,
        Messenger,
        scanGate);

    /// <param name="scanGate">Gemeinsame Scan-Sperre wie in der App (sonst eine eigene).</param>
    /// <param name="ownChanges">Gemeinsame Liste eigener Pfade wie in der App (sonst eine eigene).</param>
    public FileOperationService CreateFileOperationService(IRecycleBin? recycleBin = null, IScanGate? scanGate = null, IOwnChangeRegistry? ownChanges = null)
        => new(ContextFactory, Paths, recycleBin ?? new FakeRecycleBin(), Formats, NullLogger<FileOperationService>.Instance, Messenger, scanGate, ownChanges);

    public CatalogUpdateService CreateCatalogUpdateService()
        => new(ContextFactory, NullLogger<CatalogUpdateService>.Instance, Messenger);

    public TagService CreateTagService()
        => new(ContextFactory, NullLogger<TagService>.Instance, Messenger);

    public CategoryService CreateCategoryService()
        => new(ContextFactory, NullLogger<CategoryService>.Instance, Messenger);

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

    public async Task<Folder?> FindFolderByIdAsync(int id)
    {
        await using var ctx = CreateContext();
        return await ctx.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task<List<LibraryRoot>> GetRootsAsync(int libraryId)
    {
        await using var ctx = CreateContext();
        return await ctx.LibraryRoots.AsNoTracking().Where(r => r.LibraryId == libraryId).OrderBy(r => r.Id).ToListAsync();
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

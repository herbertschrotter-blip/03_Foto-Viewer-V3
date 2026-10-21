using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Infrastructure;
using Lumora.Services.Media;
using Lumora.Services.Operations;

namespace Lumora.Wpf.Tests.ArchiveTarget;

/// <summary>
/// Temporäre Katalog-DB (echte Migration) mit einer Bibliothek aus Archiv- und Eingangs-Wurzel (ADR-0020) für die
/// Galerie-/Archiv-Tests der Wpf-Schicht. Ordner liegen wirklich auf der Platte, Dateien nur im Katalog (Dummy-Bytes
/// bei Bedarf über <see cref="WriteFile"/>). Dispose räumt alles weg.
/// </summary>
internal sealed class ArchiveTestDb : IDisposable
{
    private ArchiveTestDb()
    {
        RootDir = Path.Combine(Path.GetTempPath(), "Lumora.Wpf.Tests", Guid.NewGuid().ToString("N"));
        ArchiveDir = Path.Combine(RootDir, "archiv");
        InboxDir = Path.Combine(RootDir, "eingang");
        Directory.CreateDirectory(ArchiveDir);
        Directory.CreateDirectory(InboxDir);
        Paths = new TempPaths(Path.Combine(RootDir, "appdata"));
        Paths.EnsureDirectoriesExist();
        Factory = new LibraryContextFactory(Paths, new SqlitePragmaInterceptor());
    }

    public string RootDir { get; }

    public string ArchiveDir { get; }

    public string InboxDir { get; }

    public IAppDataPaths Paths { get; }

    public LibraryContextFactory Factory { get; }

    public int LibraryId { get; private set; }

    public int ArchiveRootId { get; private set; }

    public int InboxRootId { get; private set; }

    public static async Task<ArchiveTestDb> CreateAsync()
    {
        var db = new ArchiveTestDb();
        await new DatabaseInitializer(db.Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        await using var ctx = db.Factory.Create();
        var library = new Library { Name = "Test", RootPath = db.ArchiveDir, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
        ctx.Libraries.Add(library);
        await ctx.SaveChangesAsync();
        var archive = new LibraryRoot { LibraryId = library.Id, Path = db.ArchiveDir, Role = LibraryRootRole.Archive, Name = "archiv", SortOrder = 0 };
        var inbox = new LibraryRoot { LibraryId = library.Id, Path = db.InboxDir, Role = LibraryRootRole.Inbox, Name = "eingang", SortOrder = 1 };
        ctx.LibraryRoots.AddRange(archive, inbox);
        await ctx.SaveChangesAsync();
        db.LibraryId = library.Id;
        db.ArchiveRootId = archive.Id;
        db.InboxRootId = inbox.Id;
        return db;
    }

    /// <summary>Legt eine Ordnerzeile an (Verzeichnis auf der Platte inklusive).</summary>
    public async Task<Folder> AddFolderAsync(int rootId, string path, int? parentId = null)
    {
        Directory.CreateDirectory(path);
        await using var ctx = Factory.Create();
        var folder = new Folder
        {
            LibraryId = LibraryId,
            RootId = rootId,
            Path = path,
            ParentId = parentId,
            Name = Path.GetFileName(path),
            ScannedAt = DateTime.UtcNow,
        };
        ctx.Folders.Add(folder);
        await ctx.SaveChangesAsync();
        return folder;
    }

    /// <summary>Legt eine Datei im Katalog an und trägt sie in den FTS-Index ein (wie der Scanner).</summary>
    public async Task<MediaFile> AddFileAsync(Folder folder, string name, MediaType type = MediaType.Image)
    {
        await using var ctx = Factory.Create();
        var file = new MediaFile
        {
            LibraryId = LibraryId,
            FolderId = folder.Id,
            Name = name,
            Extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
            Size = 1000,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            MediaType = type,
        };
        ctx.Files.Add(file);
        await ctx.SaveChangesAsync();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO FilesFts(rowid, Name, Extension) VALUES({file.Id}, {file.Name}, {file.Extension})");
        return file;
    }

    public static string WriteFile(string fullPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 });
        return fullPath;
    }

    /// <param name="scanGate">Gemeinsame Scan-Sperre wie in der App (sonst eine eigene).</param>
    public FileOperationService CreateFileOperationService(Lumora.Services.Scanning.IScanGate? scanGate = null) => new(
        Factory,
        Paths,
        new NoRecycleBin(),
        new MediaFormatRegistry(),
        NullLogger<FileOperationService>.Instance,
        new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger(),
        scanGate);

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort — Temp-Verzeichnis
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public void MoveToRecycleBin(string filePath) => throw new NotSupportedException("Papierkorb im Test nicht vorgesehen.");
    }

    private sealed class TempPaths : IAppDataPaths
    {
        public TempPaths(string root)
        {
            AppDataRoot = root;
            LibraryDatabasePath = Path.Combine(root, "library.db");
            ThumbnailsRoot = Path.Combine(root, "thumbs");
            LogsRoot = Path.Combine(root, "logs");
        }

        public string AppDataRoot { get; }

        public string LibraryDatabasePath { get; }

        public string ThumbnailsRoot { get; }

        public string LogsRoot { get; }

        public string GetLibraryThumbnailsPath(long libraryId) => Path.Combine(ThumbnailsRoot, libraryId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(ThumbnailsRoot);
            Directory.CreateDirectory(LogsRoot);
        }
    }
}

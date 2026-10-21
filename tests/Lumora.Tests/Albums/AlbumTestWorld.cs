using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Albums;
using Lumora.Services.Library;
using Lumora.Services.Operations;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Albums;

/// <summary>Auswerter-Fake für intelligente Alben: liefert feste Ids (die echte Auswertung prüfen die Wpf-Tests).</summary>
internal sealed class FakeSmartAlbumEvaluator : ISmartAlbumEvaluator
{
    public IReadOnlyList<int> Ids { get; set; } = [];

    public SmartAlbumFilter? LastFilter { get; private set; }

    public Task<IReadOnlyList<int>> GetFileIdsAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct = default)
    {
        LastFilter = filter;
        return Task.FromResult(Ids);
    }

    public Task<(int Count, int? FirstFileId)> SummarizeAsync(SmartAlbumFilter filter, LibraryScopeInfo scope, CancellationToken ct = default)
    {
        LastFilter = filter;
        return Task.FromResult((Ids.Count, Ids.Count > 0 ? (int?)Ids[0] : null));
    }
}

/// <summary>Bibliothek mit Dateien in zwei Ordnern, Alben-/Verknüpfungsdienst und Datei-Operationen (ADR-0054).</summary>
internal sealed class AlbumTestWorld : IDisposable
{
    private AlbumTestWorld(TempLibraryFixture fx, int libraryId)
    {
        Fx = fx;
        LibraryId = libraryId;
        Catalog = new TestCatalogContext();
        Scope = new LibraryScope(fx.ContextFactory, Catalog, fx.Messenger);
        Evaluator = new FakeSmartAlbumEvaluator();
        Albums = new AlbumService(fx.ContextFactory, NullLogger<AlbumService>.Instance, fx.Messenger, Scope, Evaluator, Catalog);
        Links = new FolderLinkService(fx.ContextFactory, NullLogger<FolderLinkService>.Instance, fx.Messenger);
        RecycleBin = new FakeRecycleBin();
        FileOps = fx.CreateFileOperationService(RecycleBin);
    }

    public TempLibraryFixture Fx { get; }

    public int LibraryId { get; }

    public TestCatalogContext Catalog { get; }

    public LibraryScope Scope { get; }

    public FakeSmartAlbumEvaluator Evaluator { get; }

    public AlbumService Albums { get; }

    public FolderLinkService Links { get; }

    public FakeRecycleBin RecycleBin { get; }

    public FileOperationService FileOps { get; }

    /// <summary>„Urlaub“ mit urlaub1..3.jpg, „Familie“ mit familie1..2.jpg.</summary>
    public static async Task<AlbumTestWorld> CreateAsync()
    {
        var fx = await TempLibraryFixture.CreateAsync();
        var library = await fx.CreateLibraryAsync();
        for (var i = 1; i <= 3; i++)
        {
            fx.WriteLibraryFile(Path.Combine("Urlaub", $"urlaub{i}.jpg"), 2048 + i);
        }

        for (var i = 1; i <= 2; i++)
        {
            fx.WriteLibraryFile(Path.Combine("Familie", $"familie{i}.jpg"), 4096 + i);
        }

        await fx.ScanAsync(library.Id);
        return new AlbumTestWorld(fx, library.Id);
    }

    public async Task<int> FileIdAsync(string name) => (await Fx.FindFileAsync(name))!.Id;

    public async Task<int> FolderIdAsync(string relative) => (await Fx.FindFolderAsync(Path.Combine(Fx.LibraryRoot, relative)))!.Id;

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        Albums.Dispose();
        Links.Dispose();
        FileOps.Dispose();
        Fx.Dispose();
    }
}

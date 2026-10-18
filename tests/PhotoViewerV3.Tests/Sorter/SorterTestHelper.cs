using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Sorter;

/// <summary>Baut die Sorter-Services gegen eine <see cref="TempLibraryFixture"/> (echte DB, echter FileOperationService).</summary>
internal sealed class SorterServices : IDisposable
{
    public SorterServices(TempLibraryFixture fx)
    {
        FileOps = fx.CreateFileOperationService();
        Store = new SorterProfileStore(fx.ContextFactory, NullLogger<SorterProfileStore>.Instance);
        var catalog = new SorterCatalog(fx.ContextFactory, NullLogger<SorterCatalog>.Instance);
        var runLog = new SorterRunLog(fx.ContextFactory, NullLogger<SorterRunLog>.Instance);
        var executor = new SorterMoveExecutor(FileOps, runLog, catalog, NullLogger<SorterMoveExecutor>.Instance);
        Sorter = new FileSorterService(Store, catalog, executor, fx.Formats, NullLogger<FileSorterService>.Instance);
        Flatten = new FlattenService(catalog, executor, fx.Formats, NullLogger<FlattenService>.Instance);
    }

    public FileOperationService FileOps { get; }

    public SorterProfileStore Store { get; }

    public FileSorterService Sorter { get; }

    public FlattenService Flatten { get; }

    public void Dispose() => FileOps.Dispose();
}

internal static class SorterTestHelper
{
    public static SorterFileInfo MakeFile(string name, long size = 100, int? fileId = 1) => new()
    {
        Name = name,
        FullPath = Path.Combine(@"C:\sorter-tests", name),
        Size = size,
        FileId = fileId,
    };

    public static SorterPattern Pattern(string name, string regex, int capture = 1, int priority = 50, bool enabled = true) => new()
    {
        Name = name,
        Regex = regex,
        GroupCapture = capture,
        Priority = priority,
        Enabled = enabled,
    };

    public static SorterSubLevel SubLevel(string name, string regex, int capture = 1, bool enabled = true) => new()
    {
        Name = name,
        Regex = regex,
        GroupCapture = capture,
        Enabled = enabled,
    };

    public static Dictionary<string, string> Mappings(params (string Key, string Folder)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Folder, StringComparer.Ordinal);
}

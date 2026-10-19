using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Watch;

namespace PhotoViewerV3.Tests.Watch;

/// <summary>Die Ordnerüberwachung beobachtet Archiv- und alle Eingangs-Wurzeln einer Bibliothek (ADR-0020).</summary>
public sealed class WatchRootPathsTests
{
    [Fact]
    public void Liefert_Archiv_und_alle_Eingangs_Wurzeln_ohne_Doppelte()
    {
        var library = Library(@"D:\Fotos",
            Root(1, @"D:\Fotos", LibraryRootRole.Archive),
            Root(2, @"E:\Eingang\Handy", LibraryRootRole.Inbox),
            Root(3, @"e:\eingang\handy", LibraryRootRole.Inbox),
            Root(4, @"F:\Kamera", LibraryRootRole.Inbox));

        LibraryWatchService.GetRootPaths(library).Should().Equal(@"D:\Fotos", @"E:\Eingang\Handy", @"F:\Kamera");
    }

    [Fact]
    public void Ohne_Wurzelliste_dient_RootPath_als_einzige_Wurzel()
    {
        LibraryWatchService.GetRootPaths(Library(@"D:\Fotos")).Should().Equal(@"D:\Fotos");
        LibraryWatchService.GetRootPaths(Library(string.Empty)).Should().BeEmpty();
    }

    private static LibraryRootInfo Root(int id, string path, LibraryRootRole role)
        => new(id, 1, path, role, Path.GetFileName(path), id, IsAvailable: true);

    private static LibraryInfo Library(string rootPath, params LibraryRootInfo[] roots)
        => new(1, "Test", rootPath, DateTime.UtcNow, null, 0, 0, true, roots);
}

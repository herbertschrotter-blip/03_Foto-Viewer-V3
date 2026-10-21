using PhotoViewerV3.Places;
using PhotoViewerV3.Services.Library;

namespace PhotoViewerV3.Wpf.Tests.Places;

/// <summary>
/// Zusammenbau 0.2.56 (ADR-0048): der Bereich der Karte kommt aus <see cref="ILibraryScope"/> — eine Bibliothek, alle der
/// Gruppe in „Alle verknüpften“, nichts ohne Bibliothek; frisch je Aufruf (Bereichswechsel ohne Zwischenstand).
/// </summary>
public sealed class PlacesScopeTests
{
    [Fact]
    public async Task Karte_nimmt_den_aktuellen_Bibliotheks_Bereich()
    {
        var scope = new FakeScope { Current = LibraryScopeInfo.ForLibrary(3, 7) };
        var places = new LibraryScopePlacesScope(scope);

        (await places.GetLibraryIdsAsync(CancellationToken.None)).Should().Equal(3);

        scope.Current = new LibraryScopeInfo(3, 7, "Familie", [3, 5], [3, 5], IsAllLinked: true);
        (await places.GetLibraryIdsAsync(CancellationToken.None)).Should().Equal(3, 5);

        scope.Current = new LibraryScopeInfo(5, 7, "Familie", [5], [3, 5], IsAllLinked: false);
        (await places.GetLibraryIdsAsync(CancellationToken.None)).Should().Equal([5], "verknüpft, aber einzeln angezeigt");

        scope.Current = LibraryScopeInfo.None;
        (await places.GetLibraryIdsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private sealed class FakeScope : ILibraryScope
    {
        public LibraryScopeInfo Current { get; set; } = LibraryScopeInfo.None;

        public Task<LibraryScopeInfo> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task<LibraryScopeInfo> GetForLibraryAsync(int libraryId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryGroupInfo>> GetGroupsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task ShowAllLinkedAsync(int groupId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RenameGroupAsync(int groupId, string name, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

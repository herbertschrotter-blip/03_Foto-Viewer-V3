using System.IO;
using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Gallery.ArchiveTarget;

namespace PhotoViewerV3.Wpf.Tests.ArchiveTarget;

/// <summary>
/// Dialog „Ins Archiv…“ ohne Fenster (ADR-0020): Ordnerbaum der Archiv-Wurzel aus Katalog- und Dateisystem-Ordnern,
/// letzter Zielordner vorgemerkt, Suche, Datumsvorschlag, „Neuer Ordner“ und „Umbenennen“ über den echten
/// FileOperationService (Dateisystem + Folders-Zeile).
/// </summary>
public sealed class ArchiveTargetViewModelTests
{
    [Fact]
    public async Task Baum_zeigt_Katalog_und_Dateisystem_Ordner_und_merkt_den_letzten_Zielordner_vor()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var (_, _, month) = await SeedArchiveAsync(db);
        Directory.CreateDirectory(Path.Combine(db.ArchiveDir, "2025")); // nur auf der Platte

        var vm = CreateViewModel(db, lastTarget: month.Path, suggestion: null);
        await vm.LoadAsync();

        var root = vm.Roots.Should().ContainSingle().Subject;
        root.IsRoot.Should().BeTrue();
        root.DisplayName.Should().Be("archiv  (Archiv)");
        root.Children.Select(c => c.Name).Should().Equal("2024", "2025");
        root.Children.Single(c => c.Name == "2024").FolderId.Should().NotBeNull();
        root.Children.Single(c => c.Name == "2025").IsFileSystemOnly.Should().BeTrue();

        vm.TargetPath.Should().Be(month.Path, "der zuletzt benutzte Zielordner ist vorgewählt");
        vm.SelectedNode!.FolderId.Should().Be(month.Id);
        vm.CanConfirm.Should().BeTrue();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Suche_filtert_bekannte_Ordner_und_Vorschlag_setzt_noch_fehlenden_Zielordner()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        await SeedArchiveAsync(db);
        var vm = CreateViewModel(db, lastTarget: null, suggestion: Path.Combine("2026", "2026-01"));
        await vm.LoadAsync();
        vm.TargetPath.Should().Be(db.ArchiveDir, "ohne gemerktes Ziel ist die Archiv-Wurzel gewählt");

        vm.SearchText = "07";
        vm.IsSearching.Should().BeTrue();
        vm.SearchResults.Select(r => r.Name).Should().Equal(Path.Combine("2024", "2024-07"));
        vm.ClearSearchCommand.Execute(null);
        vm.SearchResults.Should().BeEmpty();

        vm.HasSuggestion.Should().BeTrue();
        vm.UseSuggestionCommand.Execute(null);
        vm.TargetPath.Should().Be(Path.Combine(db.ArchiveDir, "2026", "2026-01"));
        vm.SelectedNode.Should().BeNull("der Ordner existiert noch nicht — MoveAsync legt ihn an");
        vm.CanConfirm.Should().BeTrue();

        vm.TargetPath = db.InboxDir;
        vm.CanConfirm.Should().BeFalse("Ziele außerhalb der Archiv-Wurzel sind nicht erlaubt");
    }

    [Fact]
    public async Task Neuer_Ordner_legt_Verzeichnis_und_Katalogzeile_an_und_waehlt_ihn_aus()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var (_, year, _) = await SeedArchiveAsync(db);
        var vm = CreateViewModel(db, lastTarget: year.Path, suggestion: null);
        vm.PromptName = (_, _, _) => Task.FromResult<string?>("Hochzeit");
        await vm.LoadAsync();

        await vm.CreateFolderCommand.ExecuteAsync(null);

        var expected = Path.Combine(year.Path, "Hochzeit");
        vm.ErrorMessage.Should().BeNull();
        Directory.Exists(expected).Should().BeTrue();
        vm.TargetPath.Should().Be(expected);
        vm.SelectedNode!.FolderId.Should().NotBeNull();
        await using var ctx = db.Factory.Create();
        var row = await ctx.Folders.AsNoTracking().SingleAsync(f => f.Path == expected);
        row.RootId.Should().Be(db.ArchiveRootId);
        row.ParentId.Should().Be(year.Id);
    }

    [Fact]
    public async Task Umbenennen_benennt_Ordner_samt_Unterordnern_um()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        var (_, year, month) = await SeedArchiveAsync(db);
        var vm = CreateViewModel(db, lastTarget: year.Path, suggestion: null);
        vm.PromptName = (_, _, _) => Task.FromResult<string?>("Jahr 2024");
        await vm.LoadAsync();
        vm.CanRename.Should().BeTrue();

        await vm.RenameFolderCommand.ExecuteAsync(null);

        var renamed = Path.Combine(db.ArchiveDir, "Jahr 2024");
        vm.ErrorMessage.Should().BeNull();
        Directory.Exists(renamed).Should().BeTrue();
        Directory.Exists(year.Path).Should().BeFalse();
        vm.TargetPath.Should().Be(renamed);
        await using var ctx = db.Factory.Create();
        (await ctx.Folders.AsNoTracking().SingleAsync(f => f.Id == month.Id)).Path.Should().Be(Path.Combine(renamed, "2024-07"));
    }

    [Fact]
    public async Task Wurzel_ist_nicht_umbenennbar()
    {
        using var db = await ArchiveTestDb.CreateAsync();
        await SeedArchiveAsync(db);
        var vm = CreateViewModel(db, lastTarget: null, suggestion: null);
        await vm.LoadAsync();

        vm.SelectedNode!.IsRoot.Should().BeTrue();
        vm.CanRename.Should().BeFalse();
        vm.RenameFolderCommand.CanExecute(null).Should().BeFalse();
    }

    private static async Task<(Domain.Entities.Folder Root, Domain.Entities.Folder Year, Domain.Entities.Folder Month)> SeedArchiveAsync(ArchiveTestDb db)
    {
        var root = await db.AddFolderAsync(db.ArchiveRootId, db.ArchiveDir);
        var year = await db.AddFolderAsync(db.ArchiveRootId, Path.Combine(db.ArchiveDir, "2024"), root.Id);
        var month = await db.AddFolderAsync(db.ArchiveRootId, Path.Combine(db.ArchiveDir, "2024", "2024-07"), year.Id);
        return (root, year, month);
    }

    private static ArchiveTargetViewModel CreateViewModel(ArchiveTestDb db, string? lastTarget, string? suggestion)
    {
        var request = new ArchiveTargetRequest(db.LibraryId, db.ArchiveRootId, db.ArchiveDir, "archiv", new[] { 1, 2 }, lastTarget, suggestion);
        return new ArchiveTargetViewModel(request, db.Factory, db.CreateFileOperationService());
    }
}

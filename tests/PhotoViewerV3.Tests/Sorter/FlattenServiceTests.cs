using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Tests.Fixtures;

namespace PhotoViewerV3.Tests.Sorter;

public sealed class FlattenServiceTests
{
    private const string Flat = "flat";

    /// <summary>
    /// Library mit flat\a.jpg (Wurzel), flat\test01\test01\raw\x.jpg, flat\test01\b\y.jpg, flat\d\c\x.jpg,
    /// flat\d\c\.thumbs\t.jpg (ignoriert) — gescannt.
    /// </summary>
    private static async Task<string> ArrangeAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Flat, "a.jpg"), 100);
        fx.WriteLibraryFile(Path.Combine(Flat, "test01", "test01", "raw", "x.jpg"), 1000);
        fx.WriteLibraryFile(Path.Combine(Flat, "test01", "b", "y.jpg"), 2000);
        fx.WriteLibraryFile(Path.Combine(Flat, "d", "c", "x.jpg"), 3000);
        fx.WriteLibraryFile(Path.Combine(Flat, "d", "c", ".thumbs", "t.jpg"), 10);
        await fx.ScanAsync(library.Id);
        return Path.Combine(fx.LibraryRoot, Flat);
    }

    private static string FlatPath(TempLibraryFixture fx, params string[] parts)
        => Path.Combine([fx.LibraryRoot, Flat, .. parts]);

    [Theory]
    [InlineData(new[] { "test01", "test01", "raw" }, "_", "test01_raw")]
    [InlineData(new[] { "Test01", "test01", "raw" }, "_", "Test01_raw")]
    [InlineData(new[] { "a", "b", "a" }, "-", "a-b")]
    [InlineData(new[] { "nur" }, "_", "nur")]
    [InlineData(new string[0], "_", "")]
    public void BuildDeduplicatedName_entspricht_V2(string[] levels, string separator, string expected)
        => FlattenService.BuildDeduplicatedName(levels, separator).Should().Be(expected);

    [Fact]
    public async Task Analyze_Pfadregel_exakt_wie_V2_Wurzel_ist_root_und_ignorierte_Ordner_fehlen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);

        var analysis = await s.Flatten.AnalyzeAsync(source);

        analysis.SourceFolder.Should().Be(source);
        analysis.Separator.Should().Be("_");
        analysis.Folders.Select(f => f.RelativePath).Should().Equal(".", @"d\c", @"test01\b", @"test01\test01\raw");
        analysis.Folders.Select(f => f.SuggestedName).Should().Equal(FlattenFolder.RootName, "d_c", "test01_b", "test01_raw");
        analysis.Folders.Select(f => f.TargetName).Should().Equal(analysis.Folders.Select(f => f.SuggestedName));
        analysis.Folders[0].IsRoot.Should().BeTrue();
        analysis.Folders[0].IsSelected.Should().BeFalse("Wurzel-Dateien bleiben standardmäßig liegen");
        analysis.Folders.Skip(1).Should().OnlyContain(f => f.IsSelected && !f.IsRoot);
        analysis.Folders[3].Levels.Should().Equal("test01", "test01", "raw");
        analysis.Folders[3].AbsolutePath.Should().Be(FlatPath(fx, "test01", "test01", "raw"));
        analysis.Folders[3].Files.Should().ContainSingle().Which.FileId.Should().BeGreaterThan(0);
        analysis.TotalFolders.Should().Be(4);
        analysis.TotalFiles.Should().Be(4, ".thumbs wird ignoriert");
        analysis.TotalSize.Should().Be(6100);
        analysis.MaxDepth.Should().Be(3);
        analysis.LevelNames.Should().Equal("b", "c", "d", "raw", "test01");
        analysis.NotInCatalog.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_nutzt_Separator_und_markiert_Dateien_ohne_Katalogeintrag()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);
        fx.WriteLibraryFile(Path.Combine(Flat, "d", "c", "neu.jpg"));

        var analysis = await s.Flatten.AnalyzeAsync(source, new FlattenAnalyzeOptions { Separator = "-" });

        analysis.Folders.Single(f => f.RelativePath == @"test01\test01\raw").SuggestedName.Should().Be("test01-raw");
        var dc = analysis.Folders.Single(f => f.RelativePath == @"d\c");
        dc.Files.Select(f => f.Name).Should().Equal("neu.jpg", "x.jpg");
        dc.NotInCatalogCount.Should().Be(1);
        analysis.NotInCatalog.Should().ContainSingle().Which.Name.Should().Be("neu.jpg");
    }

    [Fact]
    public async Task Analyze_lehnt_Ordner_ausserhalb_jeder_Bibliothek_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        await ArrangeAsync(fx);

        var act = () => s.Flatten.AnalyzeAsync(fx.OutsideDir);

        await act.Should().ThrowAsync<FolderNotInLibraryException>();
    }

    [Fact]
    public async Task BuildPlan_plant_Ziel_Ebenenname_Datei_und_Quellordner_Aufraeumen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);
        var analysis = await s.Flatten.AnalyzeAsync(source);

        var plan = s.Flatten.BuildPlan(analysis, new FlattenPlanOptions { DeleteEmptySourceFolders = true });

        plan.Kind.Should().Be(SorterPlan.KindFlatten);
        plan.TargetRoot.Should().Be(source);
        plan.Items.Should().HaveCount(3).And.OnlyContain(i => i.IsReady && i.RenameTo == null);
        plan.Items.Select(i => i.DestPath).Should().Equal(
            FlatPath(fx, "d_c", "x.jpg"),
            FlatPath(fx, "test01_b", "y.jpg"),
            FlatPath(fx, "test01_raw", "x.jpg"));
        plan.SkippedCount.Should().Be(1, "Wurzel-Datei a.jpg ist nicht ausgewählt");
        plan.DeleteEmptySourceFolders.Should().BeTrue();
        plan.SourceFoldersToClean.Should().BeEquivalentTo(FlatPath(fx, "d", "c"), FlatPath(fx, "test01", "b"), FlatPath(fx, "test01", "test01", "raw"));
        plan.CleanupBoundary.Should().Be(source);
    }

    [Fact]
    public async Task BuildPlan_dedupliziert_Dateinamen_bei_Konflikten_wie_V2()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);
        TempLibraryFixture.WriteFile(FlatPath(fx, "y_1.jpg"), 10);
        var analysis = await s.Flatten.AnalyzeAsync(source);
        foreach (var folder in analysis.Folders)
        {
            folder.IsSelected = true;
        }

        var plan = s.Flatten.BuildPlan(analysis, new FlattenPlanOptions { DirectToTargetRoot = true });

        plan.Items.Single(i => i.SourcePath == FlatPath(fx, "a.jpg")).Status.Should().Be(SorterPlanItemStatus.AlreadyInTarget);
        var first = plan.Items.Single(i => i.SourcePath == FlatPath(fx, "d", "c", "x.jpg"));
        var second = plan.Items.Single(i => i.SourcePath == FlatPath(fx, "test01", "test01", "raw", "x.jpg"));
        first.DestPath.Should().Be(FlatPath(fx, "x.jpg"));
        first.RenameTo.Should().BeNull();
        second.DestPath.Should().Be(FlatPath(fx, "x_1.jpg"));
        second.RenameTo.Should().Be("x_1.jpg");
        second.Note.Should().Contain("x_1.jpg");
        var y = plan.Items.Single(i => i.FileName == "y.jpg");
        y.DestPath.Should().Be(FlatPath(fx, "y.jpg"), "kein Konflikt im Ziel");
        plan.Items.Should().OnlyContain(i => i.TargetFolder == source);
    }

    [Fact]
    public async Task BuildPlan_markiert_ungueltige_Zielnamen_und_abgewaehlte_Ordner()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);
        var analysis = await s.Flatten.AnalyzeAsync(source);
        analysis.Folders.Single(f => f.RelativePath == @"d\c").TargetName = @"..\raus";
        analysis.Folders.Single(f => f.RelativePath == @"test01\b").IsSelected = false;

        var plan = s.Flatten.BuildPlan(analysis, new FlattenPlanOptions { TargetRoot = Path.Combine(fx.LibraryRoot, "ziel") });

        plan.TargetInsideLibrary.Should().BeTrue();
        plan.TargetError.Should().BeNull();
        plan.Items.Should().HaveCount(2);
        plan.Items.Single(i => i.FileName == "x.jpg" && i.GroupPath == @"d\c").Status.Should().Be(SorterPlanItemStatus.InvalidTarget);
        plan.Items.Single(i => i.GroupPath == @"test01\test01\raw").DestPath.Should().Be(Path.Combine(fx.LibraryRoot, "ziel", "test01_raw", "x.jpg"));
        plan.SkippedCount.Should().Be(2);
    }

    [Fact]
    public async Task Execute_verschiebt_loescht_leere_Quellordner_und_Undo_stellt_alles_wieder_her()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);
        var analysis = await s.Flatten.AnalyzeAsync(source);
        var plan = s.Flatten.BuildPlan(analysis, new FlattenPlanOptions { DeleteEmptySourceFolders = true });

        var result = await s.Flatten.ExecuteAsync(plan);

        result.Moved.Should().Be(3);
        result.Failed.Should().Be(0);
        result.BatchIds.Should().HaveCount(3, "ein Move-Batch je Zielordner");
        result.CreatedFolders.Should().BeEquivalentTo(FlatPath(fx, "d_c"), FlatPath(fx, "test01_b"), FlatPath(fx, "test01_raw"));
        File.Exists(FlatPath(fx, "d_c", "x.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "test01_b", "y.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "test01_raw", "x.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "a.jpg")).Should().BeTrue("Wurzel-Datei war nicht ausgewählt");

        Directory.Exists(FlatPath(fx, "test01")).Should().BeFalse("alle leeren Quellordner bis zur Quellwurzel werden gelöscht");
        Directory.Exists(FlatPath(fx, "d", "c")).Should().BeTrue("der .thumbs-Ordner macht ihn nicht leer");
        result.DeletedFolders.Should().BeEquivalentTo(
            FlatPath(fx, "test01", "test01", "raw"),
            FlatPath(fx, "test01", "test01"),
            FlatPath(fx, "test01", "b"),
            FlatPath(fx, "test01"));
        (await fx.FindFolderAsync(FlatPath(fx, "test01", "b"))).Should().BeNull();

        var run = (await s.Flatten.GetLastRunAsync())!;
        run.Kind.Should().Be(SorterPlan.KindFlatten);
        run.DeletedFolders.Should().HaveCount(4);
        (await s.Sorter.GetLastRunAsync()).Should().BeNull("Flatten hat ein eigenes Lauf-Protokoll");

        var undo = (await s.Flatten.UndoLastRunAsync())!;

        undo.Restored.Should().Be(3);
        undo.Failed.Should().Be(0);
        undo.RemovedFolders.Should().BeEquivalentTo(FlatPath(fx, "d_c"), FlatPath(fx, "test01_b"), FlatPath(fx, "test01_raw"));
        File.Exists(FlatPath(fx, "test01", "test01", "raw", "x.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "test01", "b", "y.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "d", "c", "x.jpg")).Should().BeTrue();
        Directory.Exists(FlatPath(fx, "test01_raw")).Should().BeFalse();
        (await fx.FindFileAsync("y.jpg"))!.FolderId.Should().Be((await fx.FindFolderAsync(FlatPath(fx, "test01", "b")))!.Id);
        (await s.Flatten.UndoLastRunAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Execute_mit_Dedup_benennt_vor_dem_Verschieben_um_und_Undo_stellt_Namen_wieder_her()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var source = await ArrangeAsync(fx);
        var analysis = await s.Flatten.AnalyzeAsync(source);
        var plan = s.Flatten.BuildPlan(analysis, new FlattenPlanOptions { DirectToTargetRoot = true });

        var result = await s.Flatten.ExecuteAsync(plan);

        result.Moved.Should().Be(3);
        result.Failed.Should().Be(0);
        result.BatchIds.Should().HaveCount(2, "ein Rename-Batch + ein Move-Batch in die Zielwurzel");
        File.Exists(FlatPath(fx, "x.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "x_1.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "y.jpg")).Should().BeTrue();
        var renamed = (await fx.FindFileAsync("x_1.jpg"))!;
        renamed.FolderId.Should().Be((await fx.FindFolderAsync(source))!.Id);
        fx.FtsRowIds("x_1").Should().Contain(renamed.Id);
        (await fx.GetBatchAsync(result.BatchIds[0]))!.OperationType.Should().Be(OperationType.Rename);
        (await fx.GetBatchAsync(result.BatchIds[1]))!.OperationType.Should().Be(OperationType.Move);

        var undo = (await s.Flatten.UndoLastRunAsync())!;

        undo.Restored.Should().Be(4, "3 Moves + 1 Rename");
        undo.Failed.Should().Be(0);
        File.Exists(FlatPath(fx, "test01", "test01", "raw", "x.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "d", "c", "x.jpg")).Should().BeTrue();
        File.Exists(FlatPath(fx, "x_1.jpg")).Should().BeFalse();
        File.Exists(FlatPath(fx, "x.jpg")).Should().BeFalse();
        (await fx.FindFileAsync("x_1.jpg")).Should().BeNull();
        (await fx.FindFileByIdAsync(renamed.Id))!.Name.Should().Be("x.jpg");
    }
}

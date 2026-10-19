using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Tests.Fixtures;
using static PhotoViewerV3.Tests.Sorter.SorterTestHelper;

namespace PhotoViewerV3.Tests.Sorter;

public sealed class FileSorterServiceTests
{
    private const string Inbox = "inbox";

    /// <summary>
    /// Library mit inbox\p006_0001.jpg, p006_0002.jpg, p020_0001.jpg, foto.jpg (kein Treffer), notizen.txt
    /// (kein Medium) — gescannt; danach p030_0001.jpg ohne Scan (nicht im Katalog).
    /// </summary>
    private static async Task<string> ArrangeInboxAsync(TempLibraryFixture fx, bool addUnscanned = true)
    {
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Inbox, "p006_0001.jpg"), 1000);
        fx.WriteLibraryFile(Path.Combine(Inbox, "p006_0002.jpg"), 2000);
        fx.WriteLibraryFile(Path.Combine(Inbox, "p020_0001.jpg"), 3000);
        fx.WriteLibraryFile(Path.Combine(Inbox, "foto.jpg"), 500);
        File.WriteAllText(Path.Combine(fx.LibraryRoot, Inbox, "notizen.txt"), "x");
        await fx.ScanAsync(library.Id);
        if (addUnscanned)
        {
            fx.WriteLibraryFile(Path.Combine(Inbox, "p030_0001.jpg"), 100);
        }

        return Path.Combine(fx.LibraryRoot, Inbox);
    }

    private static string InboxPath(TempLibraryFixture fx, params string[] parts)
        => Path.Combine([fx.LibraryRoot, Inbox, .. parts]);

    // ---------------------------------------------------------------- Analyse

    [Fact]
    public async Task Analyze_liefert_Gruppen_mit_FileIds_und_markiert_Dateien_ohne_Katalogeintrag()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);

        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        analysis.SourceFolder.Should().Be(inbox);
        analysis.SourceRoot.Path.Should().Be(fx.LibraryRoot);
        analysis.SourceRoot.IsArchive.Should().BeTrue();
        analysis.Roots.Should().ContainSingle();
        analysis.IsMultiLevel.Should().BeFalse();
        analysis.Groups.Select(g => g.Key).Should().Equal("p006", "p020", "p030", SorterGroup.UnsortedKey);
        analysis.TotalFiles.Should().Be(5, "notizen.txt ist kein Medium");

        var p006 = analysis.Groups[0];
        p006.Files.Select(f => f.Name).Should().Equal("p006_0001.jpg", "p006_0002.jpg");
        p006.Files.Should().OnlyContain(f => f.IsInCatalog && f.FileId > 0);
        p006.TotalSize.Should().Be(3000);
        p006.MovableCount.Should().Be(2);
        p006.Files[0].FullPath.Should().Be(InboxPath(fx, "p006_0001.jpg"));
        p006.FolderExists.Should().BeFalse();

        var p030 = analysis.Groups[2];
        p030.Files.Should().ContainSingle().Which.IsInCatalog.Should().BeFalse();
        p030.MovableCount.Should().Be(0);
        p030.NotInCatalogCount.Should().Be(1);
        analysis.NotInCatalog.Select(f => f.Name).Should().Equal("p030_0001.jpg");
        analysis.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Analyze_lehnt_Ordner_ausserhalb_jeder_Bibliothek_ab()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        await ArrangeInboxAsync(fx);
        TempLibraryFixture.WriteFile(Path.Combine(fx.OutsideDir, "p006_0001.jpg"));

        var act = () => s.Sorter.AnalyzeAsync(fx.OutsideDir);

        (await act.Should().ThrowAsync<FolderNotInLibraryException>())
            .Which.Message.Should().Contain("gehört zu keiner Bibliothek").And.Contain("scannen");
    }

    [Fact]
    public async Task Analyze_meldet_bereits_existierenden_Zielordner_mit_Dateianzahl()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        TempLibraryFixture.WriteFile(InboxPath(fx, "p006", "alt_1.jpg"));
        TempLibraryFixture.WriteFile(InboxPath(fx, "p006", "alt_2.jpg"));

        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var p006 = analysis.Groups.Single(g => g.Key == "p006");
        p006.FolderExists.Should().BeTrue();
        p006.FolderFileCount.Should().Be(2);
        analysis.Groups.Single(g => g.Key == "p020").FolderExists.Should().BeFalse();
    }

    [Fact]
    public async Task Analyze_ignoriert_Nicht_Medien_und_nutzt_uebergebene_Patterns()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx, addUnscanned: false);

        var analysis = await s.Sorter.AnalyzeAsync(inbox, new SorterAnalyzeOptions
        {
            Patterns = [Pattern("Foto", "^(foto)", priority: 1), Pattern("Kaputt", "(", priority: 2)],
            IncludeNonMediaFiles = true,
        });

        analysis.Groups.Select(g => g.Key).Should().Equal("foto", SorterGroup.UnsortedKey);
        analysis.Groups[1].Files.Select(f => f.Name).Should().Equal("notizen.txt", "p006_0001.jpg", "p006_0002.jpg", "p020_0001.jpg");
        analysis.Groups[1].Files.Single(f => f.Name == "notizen.txt").IsMedia.Should().BeFalse();
        analysis.Groups[1].Files.Single(f => f.Name == "notizen.txt").IsInCatalog.Should().BeFalse();
        analysis.Warnings.Should().ContainSingle().Which.Should().Contain("Kaputt");
    }

    [Fact]
    public async Task Analyze_MultiLevel_liefert_Baum_mit_Sub_Gruppen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0001_1_result.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0002_1_result_1.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0003.jpg"));
        await fx.ScanAsync(library.Id);

        var analysis = await s.Sorter.AnalyzeAsync(InboxPath(fx), new SorterAnalyzeOptions { MultiLevel = true });

        analysis.IsMultiLevel.Should().BeTrue();
        analysis.SubLevels.Should().ContainSingle().Which.Name.Should().Be("Variante");
        var p105 = analysis.Groups.Should().ContainSingle().Subject;
        p105.SubGroups.Select(sg => sg.Key).Should().Equal("result", "result_1", SorterGroup.OtherKey);
        p105.SubGroups.Should().OnlyContain(sg => sg.Level == 2);
    }

    // ---------------------------------------------------------------- Plan

    [Fact]
    public async Task BuildPlan_nutzt_Mappings_und_ueberspringt_unsorted_und_nicht_gemappte_Gruppen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "Set_006"), ("p020", "p020")));

        plan.Kind.Should().Be(SorterPlan.KindSort);
        plan.TargetRoot.Should().Be(inbox);
        plan.TargetInsideLibrary.Should().BeTrue();
        plan.DeleteEmptySourceFolders.Should().BeFalse();
        plan.Items.Should().HaveCount(3).And.OnlyContain(i => i.IsReady);
        plan.Items.Select(i => i.DestPath).Should().Equal(
            InboxPath(fx, "Set_006", "p006_0001.jpg"),
            InboxPath(fx, "Set_006", "p006_0002.jpg"),
            InboxPath(fx, "p020", "p020_0001.jpg"));
        plan.Items[0].SourcePath.Should().Be(InboxPath(fx, "p006_0001.jpg"));
        plan.Items[0].GroupPath.Should().Be("p006");
        plan.TargetFolders.Should().Equal(InboxPath(fx, "Set_006"), InboxPath(fx, "p020"));
        plan.SkippedCount.Should().Be(2, "foto.jpg (_unsorted) und p030_0001.jpg (nicht gemappt)");
    }

    [Fact]
    public async Task BuildPlan_markiert_Dateien_ohne_Katalogeintrag_statt_sie_zu_verschieben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p030", "p030")));

        var item = plan.Items.Should().ContainSingle().Subject;
        item.Status.Should().Be(SorterPlanItemStatus.NotInCatalog);
        item.FileId.Should().BeNull();
        plan.ReadyCount.Should().Be(0);
        plan.NotInCatalogCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildPlan_verschiebt_unsorted_in_eigenen_Ordner_wenn_gewuenscht()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(), new SorterPlanOptions { Unsorted = UnsortedHandling.MoveToUnsortedFolder });

        var item = plan.Items.Should().ContainSingle().Subject;
        item.FileName.Should().Be("foto.jpg");
        item.DestPath.Should().Be(InboxPath(fx, SorterGroup.UnsortedKey, "foto.jpg"));
        item.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task BuildPlan_SortToRoot_nutzt_Zielwurzel_und_plant_Quellordner_Aufraeumen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "p006")), new SorterPlanOptions
        {
            TargetRoot = fx.LibraryRoot,
            DeleteEmptySourceFolders = true,
        });

        plan.TargetRoot.Should().Be(fx.LibraryRoot);
        plan.Items.Should().OnlyContain(i => i.TargetFolder == Path.Combine(fx.LibraryRoot, "p006"));
        plan.DeleteEmptySourceFolders.Should().BeTrue();
        plan.SourceFoldersToClean.Should().Equal(inbox);
        plan.CleanupBoundary.Should().Be(fx.LibraryRoot);

        var outside = s.Sorter.BuildPlan(analysis, Mappings(("p006", "p006")), new SorterPlanOptions { TargetRoot = fx.OutsideDir });
        outside.TargetInsideLibrary.Should().BeFalse();
        outside.Items[0].DestPath.Should().Be(Path.Combine(fx.OutsideDir, "p006", "p006_0001.jpg"));
        outside.TargetError.Should().Contain("außerhalb der Wurzeln");
        outside.ReadyCount.Should().Be(0, "Ziele außerhalb der Bibliothek sind gesperrt (ADR-0024)");
        outside.Items.Should().OnlyContain(i => i.Status == SorterPlanItemStatus.InvalidTarget && i.Note == outside.TargetError);
        outside.DeleteEmptySourceFolders.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"..\evil")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("na:me")]
    public async Task BuildPlan_blockiert_Path_Traversal_und_ungueltige_Ordnernamen(string folderName)
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", folderName)));

        plan.Items.Should().HaveCount(2).And.OnlyContain(i => i.Status == SorterPlanItemStatus.InvalidTarget);
        plan.Items[0].Note.Should().NotBeNullOrEmpty();
        plan.ReadyCount.Should().Be(0);
        plan.InvalidCount.Should().Be(2);
    }

    [Fact]
    public async Task BuildPlan_markiert_Konflikte_mit_vorhandenen_Zieldateien()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        TempLibraryFixture.WriteFile(InboxPath(fx, "Set_006", "p006_0001.jpg"), 10);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "Set_006")));

        plan.Items.Single(i => i.FileName == "p006_0001.jpg").Status.Should().Be(SorterPlanItemStatus.Conflict);
        plan.Items.Single(i => i.FileName == "p006_0002.jpg").Status.Should().Be(SorterPlanItemStatus.Ready);
        plan.ConflictCount.Should().Be(1);
        plan.ReadyCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildPlan_MultiLevel_verschachtelt_oder_flach_wie_V2()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0001_1_result.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0002_1_result_1.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0003.jpg"));
        await fx.ScanAsync(library.Id);
        var analysis = await s.Sorter.AnalyzeAsync(InboxPath(fx), new SorterAnalyzeOptions { MultiLevel = true });

        var nested = s.Sorter.BuildPlan(analysis, Mappings(("p105", "P105")));
        var flat = s.Sorter.BuildPlan(analysis, Mappings(("p105", "P105")), new SorterPlanOptions { Layout = SubLevelLayout.Flat });

        nested.Items.Select(i => i.TargetFolder).Should().Equal(
            InboxPath(fx, "P105", "result"),
            InboxPath(fx, "P105", "result_1"),
            InboxPath(fx, "P105", SorterGroup.OtherKey));
        nested.Items.Select(i => i.GroupPath).Should().Equal("p105/result", "p105/result_1", "p105/_other");
        flat.Items.Select(i => i.TargetFolder).Should().Equal(
            InboxPath(fx, "P105-r0"),
            InboxPath(fx, "P105-r1"),
            InboxPath(fx, "P105-other"));
    }

    [Fact]
    public async Task BuildPlan_respektiert_abgewaehlte_Gruppen_und_geaenderte_Sub_Zielnamen()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0001_1_result.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0002_1_result_1.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p200_0001.jpg"));
        await fx.ScanAsync(library.Id);
        var analysis = await s.Sorter.AnalyzeAsync(InboxPath(fx), new SorterAnalyzeOptions { MultiLevel = true });
        analysis.Groups.Single(g => g.Key == "p200").IsSelected = false;
        var p105 = analysis.Groups.Single(g => g.Key == "p105");
        p105.SubGroups.Single(sg => sg.Key == "result").TargetFolder = "final";
        p105.SubGroups.Single(sg => sg.Key == "result_1").IsSelected = false;

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p105", "p105"), ("p200", "p200")));

        var item = plan.Items.Should().ContainSingle().Subject;
        item.TargetFolder.Should().Be(InboxPath(fx, "p105", "final"));
        plan.SkippedCount.Should().Be(2);
    }

    // ---------------------------------------------------------------- Ausführung + Undo

    [Fact]
    public async Task Execute_verschiebt_Dateien_protokolliert_Batches_und_Undo_stellt_wieder_her()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);
        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "Set_006"), ("p020", "p020")));
        var progress = new List<SorterProgress>();

        var result = await s.Sorter.ExecuteAsync(plan, new Progress<SorterProgress>(progress.Add));

        result.Moved.Should().Be(3);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(2);
        result.BatchIds.Should().HaveCount(2, "ein Move-Batch je Zielordner");
        result.CreatedFolders.Should().BeEquivalentTo(InboxPath(fx, "Set_006"), InboxPath(fx, "p020"));
        result.DeletedFolders.Should().BeEmpty();
        result.UndoAvailable.Should().BeTrue();
        result.Details.Count(d => d.Success).Should().Be(3);

        File.Exists(InboxPath(fx, "Set_006", "p006_0001.jpg")).Should().BeTrue();
        File.Exists(InboxPath(fx, "Set_006", "p006_0002.jpg")).Should().BeTrue();
        File.Exists(InboxPath(fx, "p020", "p020_0001.jpg")).Should().BeTrue();
        File.Exists(InboxPath(fx, "p006_0001.jpg")).Should().BeFalse();
        File.Exists(InboxPath(fx, "foto.jpg")).Should().BeTrue("_unsorted bleibt liegen");

        foreach (var batchId in result.BatchIds)
        {
            var batch = (await fx.GetBatchAsync(batchId))!;
            batch.OperationType.Should().Be(OperationType.Move);
            batch.Status.Should().Be(BatchStatus.Applied);
        }

        (await fx.GetOperationsAsync(result.BatchIds[0])).Should().HaveCount(2).And.OnlyContain(o => o.Status == FileOperationStatus.Applied);
        var moved = (await fx.FindFileAsync("p006_0001.jpg"))!;
        moved.FolderId.Should().Be((await fx.FindFolderAsync(InboxPath(fx, "Set_006")))!.Id);
        fx.Messages.OfKind(FilesChangeKind.Moved).Should().HaveCount(2);

        var run = (await s.Sorter.GetLastRunAsync())!;
        run.Kind.Should().Be(SorterPlan.KindSort);
        run.BatchIds.Should().Equal(result.BatchIds);
        run.Undone.Should().BeFalse();
        run.Moved.Should().Be(3);
        await using (var ctx = fx.CreateContext())
        {
            (await ctx.Settings.AnyAsync(x => x.Key == "sorter.lastRun")).Should().BeTrue();
        }

        progress.Should().NotBeEmpty();
        progress.Last().Completed.Should().Be(3);
        progress.Last().Total.Should().Be(3);

        var undo = (await s.Sorter.UndoLastRunAsync())!;

        undo.Restored.Should().Be(3);
        undo.Failed.Should().Be(0);
        undo.RemovedFolders.Should().BeEquivalentTo(InboxPath(fx, "Set_006"), InboxPath(fx, "p020"));
        File.Exists(InboxPath(fx, "p006_0001.jpg")).Should().BeTrue();
        File.Exists(InboxPath(fx, "p006_0002.jpg")).Should().BeTrue();
        File.Exists(InboxPath(fx, "p020_0001.jpg")).Should().BeTrue();
        Directory.Exists(InboxPath(fx, "Set_006")).Should().BeFalse();
        (await fx.FindFileAsync("p006_0001.jpg"))!.FolderId.Should().Be((await fx.FindFolderAsync(inbox))!.Id);
        (await fx.GetBatchAsync(result.BatchIds[0]))!.Status.Should().Be(BatchStatus.Undone);
        (await s.Sorter.GetLastRunAsync())!.Undone.Should().BeTrue();
        (await s.Sorter.UndoLastRunAsync()).Should().BeNull("der Lauf ist bereits rückgängig gemacht");
    }

    [Fact]
    public async Task Execute_SortToRoot_loescht_leeren_Quellordner_und_Undo_legt_ihn_wieder_an()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Inbox, "p006_0001.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p006_0002.jpg"));
        await fx.ScanAsync(library.Id);
        var inbox = InboxPath(fx);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);
        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "p006")), new SorterPlanOptions
        {
            TargetRoot = fx.LibraryRoot,
            DeleteEmptySourceFolders = true,
        });

        var result = await s.Sorter.ExecuteAsync(plan);

        result.Moved.Should().Be(2);
        result.DeletedFolders.Should().Equal(inbox);
        Directory.Exists(inbox).Should().BeFalse();
        Directory.Exists(fx.LibraryRoot).Should().BeTrue();
        File.Exists(Path.Combine(fx.LibraryRoot, "p006", "p006_0001.jpg")).Should().BeTrue();
        (await fx.FindFolderAsync(inbox)).Should().BeNull("die Folders-Zeile des gelöschten Ordners wird aufgeräumt");

        var undo = (await s.Sorter.UndoLastRunAsync())!;

        undo.Restored.Should().Be(2);
        Directory.Exists(inbox).Should().BeTrue();
        File.Exists(InboxPath(fx, "p006_0001.jpg")).Should().BeTrue();
        Directory.Exists(Path.Combine(fx.LibraryRoot, "p006")).Should().BeFalse();
        (await fx.FindFolderAsync(inbox)).Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_ueberspringt_Konflikte_und_meldet_sie_als_Details()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx);
        TempLibraryFixture.WriteFile(InboxPath(fx, "Set_006", "p006_0001.jpg"), 10);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);
        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "Set_006"), ("p030", "p030")));

        var result = await s.Sorter.ExecuteAsync(plan);

        result.Moved.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(2 + 2, "foto.jpg + p020 nicht gemappt; Konflikt + nicht im Katalog");
        result.BatchIds.Should().ContainSingle();
        File.Exists(InboxPath(fx, "p006_0001.jpg")).Should().BeTrue("Konflikt bleibt liegen");
        new FileInfo(InboxPath(fx, "Set_006", "p006_0001.jpg")).Length.Should().Be(10);
        result.Failures.Select(d => d.FileName).Should().BeEquivalentTo("p006_0001.jpg", "p030_0001.jpg");
        result.Failures.Single(d => d.FileName == "p030_0001.jpg").Message.Should().Contain("nicht im Katalog");
    }

    // ---------------------------------------------------------------- In-Memory-Operationen

    [Fact]
    public async Task MergeGroups_MoveFile_und_SplitGroup_arbeiten_auf_dem_Analyse_Ergebnis()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var inbox = await ArrangeInboxAsync(fx, addUnscanned: false);
        var analysis = await s.Sorter.AnalyzeAsync(inbox);

        s.Sorter.MergeGroups(analysis, "p020", "p006");

        analysis.Groups.Select(g => g.Key).Should().Equal("p006", SorterGroup.UnsortedKey);
        analysis.Groups[0].Files.Select(f => f.Name).Should().Equal("p006_0001.jpg", "p006_0002.jpg", "p020_0001.jpg");
        analysis.Groups[0].TotalSize.Should().Be(6000);

        s.Sorter.MoveFile(analysis, "foto.jpg", "Neu");

        analysis.Groups.Select(g => g.Key).Should().Equal("neu", "p006");
        analysis.Groups[0].PatternName.Should().Be(SorterGroup.ManualPatternName);
        analysis.Groups[0].Files.Should().ContainSingle().Which.Name.Should().Be("foto.jpg");

        s.Sorter.SplitGroup(analysis, "p006", ["p006_0001.jpg"], "p006_extra");

        analysis.Groups.Select(g => g.Key).Should().Equal("neu", "p006", "p006_extra");
        analysis.Groups[2].PatternName.Should().Be(SorterGroup.SplitPatternName);
        analysis.Groups[1].Files.Select(f => f.Name).Should().Equal("p006_0002.jpg", "p020_0001.jpg");

        var merge = () => s.Sorter.MergeGroups(analysis, "p006", "p006");
        var split = () => s.Sorter.SplitGroup(analysis, "p006", ["gibtsnicht.jpg"], "x");
        merge.Should().Throw<ArgumentException>().WithMessage("*identisch*");
        split.Should().Throw<ArgumentException>().WithMessage("*gibtsnicht.jpg*");

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("neu", "Neu"), ("p006_extra", "Extra")));
        plan.Items.Select(i => i.DestPath).Should().Equal(InboxPath(fx, "Neu", "foto.jpg"), InboxPath(fx, "Extra", "p006_0001.jpg"));
    }

    [Fact]
    public async Task In_Memory_Operationen_berechnen_Sub_Gruppen_neu()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var library = await fx.CreateLibraryAsync();
        fx.WriteLibraryFile(Path.Combine(Inbox, "p105_0001_1_result.jpg"));
        fx.WriteLibraryFile(Path.Combine(Inbox, "p200_0001_1_result_1.jpg"));
        await fx.ScanAsync(library.Id);
        var analysis = await s.Sorter.AnalyzeAsync(InboxPath(fx), new SorterAnalyzeOptions { MultiLevel = true });

        s.Sorter.MergeGroups(analysis, "p200", "p105");

        var p105 = analysis.Groups.Should().ContainSingle().Subject;
        p105.SubGroups.Select(sg => sg.Key).Should().Equal("result", "result_1");
    }
}

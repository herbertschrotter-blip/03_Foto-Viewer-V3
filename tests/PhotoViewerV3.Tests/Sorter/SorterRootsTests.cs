using Microsoft.EntityFrameworkCore;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Sorter;
using PhotoViewerV3.Tests.Fixtures;
using static PhotoViewerV3.Tests.Sorter.SorterTestHelper;

namespace PhotoViewerV3.Tests.Sorter;

/// <summary>
/// Sortierer und Flatten über mehrere Wurzeln (ADR-0020, ADR-0024): Quelle im Eingang, Ziel im Archiv, Katalog-Nachführung
/// (Folders.RootId, Status-Filter, FTS, stabile FileIds, Protokoll) samt Rückgängig; Ziele außerhalb der Wurzeln gesperrt.
/// </summary>
public sealed class SorterRootsTests
{
    private const string Handy = "Handy";

    /// <summary>Archiv-Wurzel + Eingang mit Handy\p006_0001.jpg, Handy\p006_0002.jpg, Handy\p020_0001.jpg — gescannt.</summary>
    private static async Task<(int LibraryId, int ArchiveRootId, int InboxRootId)> ArrangeInboxAsync(TempLibraryFixture fx)
    {
        var library = await fx.CreateLibraryAsync();
        var inbox = await fx.AddInboxRootAsync(library.Id);
        fx.WriteInboxFile(Path.Combine(Handy, "p006_0001.jpg"), 1000);
        fx.WriteInboxFile(Path.Combine(Handy, "p006_0002.jpg"), 2000);
        fx.WriteInboxFile(Path.Combine(Handy, "p020_0001.jpg"), 3000);
        await fx.ScanAsync(library.Id);
        var archive = (await fx.GetRootsAsync(library.Id)).Single(r => r.Role == LibraryRootRole.Archive);
        return (library.Id, archive.Id, inbox.Id);
    }

    private static async Task<int?> RootIdOfFileAsync(TempLibraryFixture fx, int fileId)
    {
        var file = (await fx.FindFileByIdAsync(fileId))!;
        return (await fx.FindFolderByIdAsync(file.FolderId))!.RootId;
    }

    /// <summary>Dateien unter Eingangs-Wurzeln — die Menge hinter dem Galerie-Filter „Nicht archiviert“.</summary>
    private static async Task<int> CountNotArchivedAsync(TempLibraryFixture fx)
    {
        await using var ctx = fx.CreateContext();
        return await ctx.Files.CountAsync(f => ctx.Folders.Any(d => d.Id == f.FolderId
            && ctx.LibraryRoots.Any(r => r.Id == d.RootId && r.Role == LibraryRootRole.Inbox)));
    }

    [Fact]
    public async Task Analyse_im_Eingang_kennt_Quellwurzel_und_alle_Wurzeln()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var (_, archiveId, inboxId) = await ArrangeInboxAsync(fx);

        var analysis = await s.Sorter.AnalyzeAsync(Path.Combine(fx.InboxDir, Handy));

        analysis.SourceRoot.Id.Should().Be(inboxId);
        analysis.SourceRoot.IsInbox.Should().BeTrue();
        analysis.SourceRoot.Path.Should().Be(fx.InboxDir);
        analysis.Roots.Select(r => r.Id).Should().Equal(new[] { archiveId, inboxId }, "Archiv zuerst");
        analysis.Groups.Select(g => g.Key).Should().Equal("p006", "p020");
        analysis.Groups.SelectMany(g => g.Files).Should().OnlyContain(f => f.IsInCatalog);

        var flatten = await s.Flatten.AnalyzeAsync(fx.InboxDir);
        flatten.SourceRoot.Id.Should().Be(inboxId);
        flatten.Roots.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetTargetRoot_prueft_vorhandene_Ordner_unter_der_Zielwurzel()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        await ArrangeInboxAsync(fx);
        fx.WriteLibraryFile(Path.Combine("p006", "alt.jpg"));

        var analysis = await s.Sorter.AnalyzeAsync(Path.Combine(fx.InboxDir, Handy), new SorterAnalyzeOptions { TargetRoot = fx.LibraryRoot });

        analysis.TargetRoot.Should().Be(fx.LibraryRoot);
        analysis.Groups.Single(g => g.Key == "p006").FolderExists.Should().BeTrue("Archiv\\p006 enthält bereits eine Datei");

        s.Sorter.SetTargetRoot(analysis, null);

        analysis.TargetRoot.Should().BeNull();
        analysis.Groups.Single(g => g.Key == "p006").FolderExists.Should().BeFalse("im Quellordner gibt es keinen Ordner p006");
    }

    [Fact]
    public async Task Sortieren_Eingang_nach_Archiv_fuehrt_Katalog_ueber_die_Wurzelgrenze_und_Undo_stellt_zurueck()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var (_, archiveId, inboxId) = await ArrangeInboxAsync(fx);
        var source = Path.Combine(fx.InboxDir, Handy);
        var ids = new Dictionary<string, int>();
        foreach (var name in new[] { "p006_0001.jpg", "p006_0002.jpg", "p020_0001.jpg" })
        {
            ids[name] = (await fx.FindFileAsync(name))!.Id;
        }

        (await CountNotArchivedAsync(fx)).Should().Be(3);
        var target = Path.Combine(fx.LibraryRoot, "2026");
        var analysis = await s.Sorter.AnalyzeAsync(source);
        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "p006"), ("p020", "p020")), new SorterPlanOptions
        {
            TargetRoot = target,
            DeleteEmptySourceFolders = true,
        });

        plan.TargetError.Should().BeNull();
        plan.TargetInsideLibrary.Should().BeTrue();
        plan.TargetLibraryRoot!.Id.Should().Be(archiveId);
        plan.SourceRoot.Id.Should().Be(inboxId);
        plan.CrossesRoots.Should().BeTrue();
        plan.ReadyCount.Should().Be(3);
        plan.DeleteEmptySourceFolders.Should().BeTrue();
        plan.SourceFoldersToClean.Should().Equal(source);

        var result = await s.Sorter.ExecuteAsync(plan);

        result.Moved.Should().Be(3);
        result.Failed.Should().Be(0);
        result.BatchIds.Should().HaveCount(2, "ein Move-Batch je Zielordner");
        result.CreatedFolders.Should().Contain(new[] { Path.Combine(target, "p006"), Path.Combine(target, "p020"), target });
        result.DeletedFolders.Should().Equal(source);
        Directory.Exists(fx.InboxDir).Should().BeTrue("eine Wurzel wird nie gelöscht");
        File.Exists(Path.Combine(target, "p006", "p006_0001.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(target, "p020", "p020_0001.jpg")).Should().BeTrue();

        foreach (var (name, id) in ids)
        {
            var file = (await fx.FindFileByIdAsync(id))!;
            file.Name.Should().Be(name, "FileIds bleiben stabil");
            (await RootIdOfFileAsync(fx, id)).Should().Be(archiveId, $"{name} liegt jetzt unter der Archiv-Wurzel");
        }

        var yearRow = (await fx.FindFolderAsync(target))!;
        yearRow.RootId.Should().Be(archiveId, "neu angelegte Zwischenordner bekommen die RootId der Zielwurzel");
        (await fx.FindFolderAsync(Path.Combine(target, "p006")))!.ParentId.Should().Be(yearRow.Id);
        (await fx.FindFolderAsync(source)).Should().BeNull("die Folders-Zeile des gelöschten Quellordners wird aufgeräumt");
        (await CountNotArchivedAsync(fx)).Should().Be(0, "Filter „Nicht archiviert“ ist danach leer");
        fx.FtsRowIds("p006").Should().BeEquivalentTo(new[] { ids["p006_0001.jpg"], ids["p006_0002.jpg"] });
        foreach (var batchId in result.BatchIds)
        {
            var batch = (await fx.GetBatchAsync(batchId))!;
            batch.OperationType.Should().Be(OperationType.Move);
            batch.Status.Should().Be(BatchStatus.Applied);
            (await fx.GetOperationsAsync(batchId)).Should().OnlyContain(o => o.Status == FileOperationStatus.Applied && o.FileId != null);
        }

        var undo = (await s.Sorter.UndoLastRunAsync())!;

        undo.Restored.Should().Be(3);
        undo.Failed.Should().Be(0);
        Directory.Exists(target).Should().BeFalse("angelegte, wieder leere Ordner werden entfernt");
        (await fx.FindFolderAsync(target)).Should().BeNull();
        File.Exists(Path.Combine(source, "p006_0001.jpg")).Should().BeTrue();
        foreach (var (name, id) in ids)
        {
            (await fx.FindFileByIdAsync(id))!.Name.Should().Be(name);
            (await RootIdOfFileAsync(fx, id)).Should().Be(inboxId, $"{name} ist zurück im Eingang");
        }

        (await CountNotArchivedAsync(fx)).Should().Be(3);
        fx.FtsRowIds("p020").Should().Equal(ids["p020_0001.jpg"]);
        foreach (var batchId in result.BatchIds)
        {
            (await fx.GetBatchAsync(batchId))!.Status.Should().Be(BatchStatus.Undone);
        }
    }

    [Fact]
    public async Task Flatten_in_einem_Eingang_bleibt_unter_der_Eingangs_Wurzel_und_Undo_stellt_zurueck()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var library = await fx.CreateLibraryAsync();
        var inbox = await fx.AddInboxRootAsync(library.Id);
        fx.WriteInboxFile("z.jpg");
        fx.WriteInboxFile(Path.Combine("a", "b", "x.jpg"));
        fx.WriteInboxFile(Path.Combine("c", "d", "y.jpg"));
        await fx.ScanAsync(library.Id);
        var xId = (await fx.FindFileAsync("x.jpg"))!.Id;
        var yId = (await fx.FindFileAsync("y.jpg"))!.Id;

        var analysis = await s.Flatten.AnalyzeAsync(fx.InboxDir);
        var plan = s.Flatten.BuildPlan(analysis, new FlattenPlanOptions { DeleteEmptySourceFolders = true });

        plan.TargetRoot.Should().Be(fx.InboxDir);
        plan.TargetLibraryRoot!.Id.Should().Be(inbox.Id);
        plan.CrossesRoots.Should().BeFalse();
        plan.ReadyCount.Should().Be(2, "z.jpg in der Quellwurzel bleibt standardmäßig liegen");

        var result = await s.Flatten.ExecuteAsync(plan);

        result.Moved.Should().Be(2);
        File.Exists(Path.Combine(fx.InboxDir, "a_b", "x.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(fx.InboxDir, "c_d", "y.jpg")).Should().BeTrue();
        Directory.Exists(Path.Combine(fx.InboxDir, "a")).Should().BeFalse();
        Directory.Exists(Path.Combine(fx.InboxDir, "c")).Should().BeFalse();
        Directory.Exists(fx.InboxDir).Should().BeTrue("die Eingangs-Wurzel bleibt");
        (await RootIdOfFileAsync(fx, xId)).Should().Be(inbox.Id);
        (await RootIdOfFileAsync(fx, yId)).Should().Be(inbox.Id);
        (await fx.FindFolderAsync(Path.Combine(fx.InboxDir, "a_b")))!.RootId.Should().Be(inbox.Id);
        (await CountNotArchivedAsync(fx)).Should().Be(3);

        var undo = (await s.Flatten.UndoLastRunAsync())!;

        undo.Restored.Should().Be(2);
        File.Exists(Path.Combine(fx.InboxDir, "a", "b", "x.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(fx.InboxDir, "c", "d", "y.jpg")).Should().BeTrue();
        Directory.Exists(Path.Combine(fx.InboxDir, "a_b")).Should().BeFalse();
        (await RootIdOfFileAsync(fx, xId)).Should().Be(inbox.Id);
        (await fx.FindFileByIdAsync(xId))!.FolderId.Should().Be((await fx.FindFolderAsync(Path.Combine(fx.InboxDir, "a", "b")))!.Id);
    }

    [Fact]
    public async Task Ziel_ausserhalb_der_Wurzeln_ist_gesperrt_und_es_wird_nichts_verschoben()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        await ArrangeInboxAsync(fx);
        var source = Path.Combine(fx.InboxDir, Handy);
        var analysis = await s.Sorter.AnalyzeAsync(source);

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "p006")), new SorterPlanOptions
        {
            TargetRoot = fx.OutsideDir,
            DeleteEmptySourceFolders = true,
        });

        plan.TargetInsideLibrary.Should().BeFalse();
        plan.TargetLibraryRoot.Should().BeNull();
        plan.TargetError.Should().Contain("außerhalb der Wurzeln").And.Contain(fx.OutsideDir);
        plan.ReadyCount.Should().Be(0);
        plan.InvalidCount.Should().Be(2);
        plan.DeleteEmptySourceFolders.Should().BeFalse();

        var result = await s.Sorter.ExecuteAsync(plan);

        result.Moved.Should().Be(0);
        result.BatchIds.Should().BeEmpty();
        result.Failures.Should().OnlyContain(d => d.Message == plan.TargetError);
        File.Exists(Path.Combine(source, "p006_0001.jpg")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(fx.OutsideDir).Should().BeEmpty();
        (await s.Sorter.GetLastRunAsync()).Should().BeNull("ohne Batch gibt es keinen Lauf");

        var flattenAnalysis = await s.Flatten.AnalyzeAsync(fx.InboxDir);
        var flattenPlan = s.Flatten.BuildPlan(flattenAnalysis, new FlattenPlanOptions { TargetRoot = fx.OutsideDir });
        flattenPlan.TargetError.Should().NotBeNull();
        flattenPlan.ReadyCount.Should().Be(0);
        flattenPlan.Items.Should().OnlyContain(i => i.Status == SorterPlanItemStatus.InvalidTarget);
    }

    [Fact]
    public async Task Ziel_unter_einer_Offline_Wurzel_ist_gesperrt()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        using var s = new SorterServices(fx);
        var (libraryId, _, _) = await ArrangeInboxAsync(fx);
        var offline = Path.Combine(fx.RootDir, "usb-stick");
        await fx.AddInboxRootAsync(libraryId, offline);
        var analysis = await s.Sorter.AnalyzeAsync(Path.Combine(fx.InboxDir, Handy));

        var plan = s.Sorter.BuildPlan(analysis, Mappings(("p006", "p006")), new SorterPlanOptions { TargetRoot = Path.Combine(offline, "neu") });

        analysis.Roots.Should().HaveCount(3);
        plan.TargetLibraryRoot!.Path.Should().Be(offline);
        plan.TargetError.Should().Contain("nicht erreichbar");
        plan.ReadyCount.Should().Be(0);
    }

    [Fact]
    public void SorterTargetCheck_waehlt_die_laengste_Wurzel_und_erkennt_Wurzelordner()
    {
        var roots = new[]
        {
            new SorterRoot(1, @"D:\Fotos", LibraryRootRole.Archive, "Fotos"),
            new SorterRoot(2, @"E:\Eingang", LibraryRootRole.Inbox, "Eingang"),
        };

        SorterTargetCheck.FindRoot(roots, @"e:\eingang\handy")!.Id.Should().Be(2);
        SorterTargetCheck.FindRoot(roots, @"D:\Fotos2").Should().BeNull("Präfix ohne Trennzeichen zählt nicht");
        SorterTargetCheck.IsRootFolder(roots, @"d:\fotos").Should().BeTrue();
        SorterTargetCheck.IsRootFolder(roots, @"D:\Fotos\2024").Should().BeFalse();
        SorterTargetCheck.Check(roots, @"F:\Anderswo", out var none).Should().Contain("außerhalb");
        none.Should().BeNull();
    }
}

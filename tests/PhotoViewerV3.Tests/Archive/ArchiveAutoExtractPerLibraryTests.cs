using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Archive;
using PhotoViewerV3.Services.Scanning;
using PhotoViewerV3.Services.Watch;
using PhotoViewerV3.Tests.Fixtures;
using static PhotoViewerV3.Tests.Archive.ArchiveTestHelper;

namespace PhotoViewerV3.Tests.Archive;

/// <summary>
/// Automatisches Entpacken je Bibliothek (ADR-0033): Einstellung je LibraryId in der Settings-Tabelle, einmalige Übernahme
/// der globalen Einstellung bis 0.2.30, Entpacken nur in Bibliotheken mit „an“, „Vorhandene Archive jetzt entpacken“.
/// Zwei Bibliotheken mit je einer Archiv- und einer Eingangs-Wurzel, echte Dienste.
/// </summary>
public sealed class ArchiveAutoExtractPerLibraryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private sealed class Env : IDisposable
    {
        private Env(TempLibraryFixture fx, int libraryA, int libraryB, string inboxB)
        {
            Fx = fx;
            LibraryA = libraryA;
            LibraryB = libraryB;
            InboxB = inboxB;
            Scanner = fx.CreateScanService(Gate);
            var fileOps = fx.CreateFileOperationService(new FakeRecycleBin(), Gate, Own);
            Archives = new ArchiveService(fx.Formats, NullLogger<ArchiveService>.Instance, Scanner, fileOps, fx.Messenger);
            Store = new ArchiveAutoExtractSettingsStore(fx.ContextFactory);
            Service = new ArchiveAutoExtractService(fx.ContextFactory, Store, Archives, Scanner, fileOps, Gate, Own,
                NullLogger<ArchiveAutoExtractService>.Instance, fx.Messenger);
        }

        public TempLibraryFixture Fx { get; }

        public int LibraryA { get; }

        public int LibraryB { get; }

        public string InboxA => Fx.InboxDir;

        public string InboxB { get; }

        public ScanGate Gate { get; } = new();

        public OwnChangeRegistry Own { get; } = new();

        public ScanService Scanner { get; }

        public ArchiveService Archives { get; }

        public ArchiveAutoExtractSettingsStore Store { get; }

        public ArchiveAutoExtractService Service { get; }

        /// <summary>Bibliothek A (Fixture-Ordner) und B (eigene Ordner), je mit Eingang; Dienst noch nicht gestartet, kein Scan.</summary>
        public static async Task<Env> CreateAsync()
        {
            var fx = await TempLibraryFixture.CreateAsync();
            var a = await fx.CreateLibraryAsync("A");
            await fx.AddInboxRootAsync(a.Id);
            var rootB = Path.Combine(fx.RootDir, "library-b");
            var inboxB = Path.Combine(fx.RootDir, "inbox-b");
            Directory.CreateDirectory(rootB);
            Directory.CreateDirectory(inboxB);
            int b;
            await using (var ctx = fx.CreateContext())
            {
                var library = new Library { Name = "B", RootPath = rootB, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow.AddMinutes(-1) };
                ctx.Libraries.Add(library);
                await ctx.SaveChangesAsync();
                ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = library.Id, Path = rootB, Role = LibraryRootRole.Archive, Name = "library-b", SortOrder = 0 });
                await ctx.SaveChangesAsync();
                b = library.Id;
            }

            await fx.AddInboxRootAsync(b, inboxB);
            fx.WriteLibraryFile(Path.Combine("2024", "a.jpg"));
            TempLibraryFixture.WriteFile(Path.Combine(rootB, "b.jpg"));
            return new Env(fx, a.Id, b, inboxB);
        }

        public async Task FreshScanBothAsync()
        {
            await Scanner.ScanAsync(LibraryA, ScanMode.Fresh, null, CancellationToken.None);
            await Scanner.ScanAsync(LibraryB, ScanMode.Fresh, null, CancellationToken.None);
            await Service.WhenIdleAsync().WaitAsync(Timeout);
        }

        public async Task IncrementalAndWaitAsync(int libraryId)
        {
            await Scanner.ScanAsync(libraryId, ScanMode.Incremental, null, CancellationToken.None);
            await Service.WhenIdleAsync().WaitAsync(Timeout);
        }

        public async Task<Dictionary<string, string?>> SettingsRowsAsync()
        {
            await using var ctx = Fx.CreateContext();
            return await ctx.Settings.AsNoTracking()
                .Where(s => s.Key.StartsWith("archives.autoExtract"))
                .ToDictionaryAsync(s => s.Key, s => s.Value);
        }

        public async Task WriteLegacyAsync(bool enabled, bool recycle)
        {
            await using var ctx = Fx.CreateContext();
            ctx.Settings.Add(new Setting { Key = ArchiveAutoExtractSettings.LegacyEnabledKey, Value = enabled ? "true" : "false" });
            ctx.Settings.Add(new Setting { Key = ArchiveAutoExtractSettings.LegacyRecycleKey, Value = recycle ? "true" : "false" });
            await ctx.SaveChangesAsync();
        }

        public void Dispose()
        {
            Service.Dispose();
            Fx.Dispose();
        }
    }

    [Fact]
    public async Task Store_speichert_je_Bibliothek_getrennt()
    {
        using var env = await Env.CreateAsync();

        await env.Store.SaveAsync(env.LibraryA, new ArchiveAutoExtractSettings(true, false));
        await env.Store.SaveAsync(env.LibraryB, new ArchiveAutoExtractSettings(false, true));

        (await env.Store.LoadAsync(env.LibraryA)).Should().Be(new ArchiveAutoExtractSettings(true, false));
        (await env.Store.LoadAsync(env.LibraryB)).Should().Be(new ArchiveAutoExtractSettings(false, true));
        (await env.Store.LoadAsync(env.LibraryB + 1)).Should().Be(ArchiveAutoExtractSettings.Default);
        var all = await env.Store.LoadAllAsync();
        all.Should().HaveCount(2);
        all[env.LibraryA].Should().Be(new ArchiveAutoExtractSettings(true, false));
        all[env.LibraryB].Should().Be(new ArchiveAutoExtractSettings(false, true));
        (await env.SettingsRowsAsync()).Keys.Should().BeEquivalentTo(
            ArchiveAutoExtractSettings.EnabledKeyFor(env.LibraryA), ArchiveAutoExtractSettings.RecycleKeyFor(env.LibraryA),
            ArchiveAutoExtractSettings.EnabledKeyFor(env.LibraryB), ArchiveAutoExtractSettings.RecycleKeyFor(env.LibraryB));
    }

    [Fact]
    public async Task Globale_Einstellung_wird_einmalig_als_Vorgabe_fuer_bestehende_Bibliotheken_uebernommen()
    {
        using var env = await Env.CreateAsync();
        await env.WriteLegacyAsync(enabled: true, recycle: true);
        await env.Store.SaveAsync(env.LibraryB, new ArchiveAutoExtractSettings(false, false)); // eigene Einstellung hat Vorrang

        (await env.Store.MigrateGlobalDefaultAsync()).Should().Be(1);

        (await env.Store.LoadAsync(env.LibraryA)).Should().Be(new ArchiveAutoExtractSettings(true, true));
        (await env.Store.LoadAsync(env.LibraryB)).Should().Be(new ArchiveAutoExtractSettings(false, false));
        var rows = await env.SettingsRowsAsync();
        rows.Should().NotContainKey(ArchiveAutoExtractSettings.LegacyEnabledKey).And.NotContainKey(ArchiveAutoExtractSettings.LegacyRecycleKey);

        // Zweiter Start: nichts mehr zu übernehmen, nichts verändert
        (await env.Store.MigrateGlobalDefaultAsync()).Should().Be(0);
        (await env.SettingsRowsAsync()).Should().BeEquivalentTo(rows);

        // Neue Bibliothek nach der Übernahme: Standard aus
        int c;
        await using (var ctx = env.Fx.CreateContext())
        {
            var library = new Library { Name = "C", RootPath = Path.Combine(env.Fx.RootDir, "library-c"), CreatedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            c = library.Id;
        }

        (await env.Store.LoadAsync(c)).Should().Be(ArchiveAutoExtractSettings.Default);
    }

    [Fact]
    public async Task Ohne_globale_Einstellung_bleibt_jede_Bibliothek_aus()
    {
        using var env = await Env.CreateAsync();

        (await env.Store.MigrateGlobalDefaultAsync()).Should().Be(0);
        await env.Service.StartAsync();

        (await env.SettingsRowsAsync()).Should().BeEmpty("ohne globale Schlüssel wird nichts geschrieben");
        env.Service.GetSettings(env.LibraryA).Should().Be(ArchiveAutoExtractSettings.Default);
        env.Service.GetSettings(env.LibraryB).Should().Be(ArchiveAutoExtractSettings.Default);
    }

    [Fact]
    public async Task Start_uebernimmt_die_globale_Einstellung_und_entpackt_danach_in_beiden_Bibliotheken()
    {
        using var env = await Env.CreateAsync();
        await env.WriteLegacyAsync(enabled: true, recycle: false);

        await env.Service.StartAsync();
        await env.FreshScanBothAsync();

        env.Service.GetSettings(env.LibraryA).Should().Be(new ArchiveAutoExtractSettings(true, false));
        env.Service.GetSettings(env.LibraryB).Should().Be(new ArchiveAutoExtractSettings(true, false));
        WriteZip(Path.Combine(env.InboxA, "a.zip"), ("a1.jpg", Jpeg(100)));
        WriteZip(Path.Combine(env.InboxB, "b.zip"), ("b1.jpg", Jpeg(100)));
        await env.IncrementalAndWaitAsync(env.LibraryA);
        await env.IncrementalAndWaitAsync(env.LibraryB);

        File.Exists(Path.Combine(env.InboxA, "a", "a1.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(env.InboxB, "b", "b1.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task Auto_Entpacken_greift_nur_fuer_die_Bibliothek_mit_an()
    {
        using var env = await Env.CreateAsync();
        await env.Service.StartAsync();
        await env.Service.SetEnabledAsync(env.LibraryA, true);
        await env.FreshScanBothAsync();

        WriteZip(Path.Combine(env.InboxA, "eins.zip"), ("a1.jpg", Jpeg(100)));
        WriteZip(Path.Combine(env.InboxB, "zwei.zip"), ("b1.jpg", Jpeg(100)));
        await env.IncrementalAndWaitAsync(env.LibraryA);
        await env.IncrementalAndWaitAsync(env.LibraryB);

        File.Exists(Path.Combine(env.InboxA, "eins", "a1.jpg")).Should().BeTrue("Bibliothek A: an");
        Directory.Exists(Path.Combine(env.InboxB, "zwei")).Should().BeFalse("Bibliothek B: aus (Standard)");
        env.Service.LastSummary!.LibraryId.Should().Be(env.LibraryA);
        (await env.Store.LoadAsync(env.LibraryA)).Enabled.Should().BeTrue("der Schalter ist gespeichert");
        (await env.Store.LoadAsync(env.LibraryB)).Enabled.Should().BeFalse();

        // Umschalten: B an, A aus — gilt sofort und nur für die jeweilige Bibliothek
        await env.Service.SetEnabledAsync(env.LibraryB, true);
        await env.Service.SetEnabledAsync(env.LibraryA, false);
        WriteZip(Path.Combine(env.InboxA, "drei.zip"), ("a2.jpg", Jpeg(100)));
        WriteZip(Path.Combine(env.InboxB, "vier.zip"), ("b2.jpg", Jpeg(100)));
        await env.IncrementalAndWaitAsync(env.LibraryA);
        await env.IncrementalAndWaitAsync(env.LibraryB);

        Directory.Exists(Path.Combine(env.InboxA, "drei")).Should().BeFalse();
        File.Exists(Path.Combine(env.InboxB, "vier", "b2.jpg")).Should().BeTrue();
        Directory.Exists(Path.Combine(env.InboxB, "zwei")).Should().BeFalse("vor dem Einschalten katalogisiert — bleibt manuell");
        env.Service.GetSettings(env.LibraryA).Enabled.Should().BeFalse();
        env.Service.GetSettings(env.LibraryB).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Papierkorb_Schalter_gilt_je_Bibliothek()
    {
        using var env = await Env.CreateAsync();
        await env.Service.StartAsync();
        await env.Service.SetEnabledAsync(env.LibraryA, true);
        await env.Service.SetRecycleAfterExtractAsync(env.LibraryA, true);
        await env.Service.SetEnabledAsync(env.LibraryB, true);
        await env.FreshScanBothAsync();

        var zipA = WriteZip(Path.Combine(env.InboxA, "eins.zip"), ("a1.jpg", Jpeg(100)));
        var zipB = WriteZip(Path.Combine(env.InboxB, "zwei.zip"), ("b1.jpg", Jpeg(100)));
        await env.IncrementalAndWaitAsync(env.LibraryA);
        await env.IncrementalAndWaitAsync(env.LibraryB);

        File.Exists(zipA).Should().BeFalse("A: Papierkorb danach");
        File.Exists(zipB).Should().BeTrue("B: Archiv bleibt liegen");
        File.Exists(Path.Combine(env.InboxB, "zwei", "b1.jpg")).Should().BeTrue();
        (await env.Store.LoadAsync(env.LibraryA)).Should().Be(new ArchiveAutoExtractSettings(true, true));
        (await env.Store.LoadAsync(env.LibraryB)).Should().Be(new ArchiveAutoExtractSettings(true, false));
    }

    [Fact]
    public async Task Vorhandene_Archive_lassen_sich_nachholen_nur_bei_an_und_ohne_schon_entpackte()
    {
        using var env = await Env.CreateAsync();
        await env.Service.StartAsync();
        var old = WriteZip(Path.Combine(env.InboxA, "alt.zip"), ("alt.jpg", Jpeg(100)));
        WriteZip(Path.Combine(env.InboxA, "erledigt.zip"), ("x.jpg", Jpeg(100)));
        Directory.CreateDirectory(Path.Combine(env.InboxA, "erledigt")); // schon (manuell) entpackt
        WriteZip(Path.Combine(env.Fx.LibraryRoot, "archiv.zip"), ("y.jpg", Jpeg(100))); // unter der Archiv-Wurzel
        WriteZip(Path.Combine(env.InboxB, "andere.zip"), ("z.jpg", Jpeg(100))); // andere Bibliothek
        await env.FreshScanBothAsync();

        // Aus: zählt, reiht aber nichts ein
        (await env.Service.CountExistingAsync(env.LibraryA)).Should().Be(1);
        (await env.Service.ExtractExistingAsync(env.LibraryA)).Should().Be(0);
        await env.Service.WhenIdleAsync().WaitAsync(Timeout);
        Directory.Exists(Path.Combine(env.InboxA, "alt")).Should().BeFalse();

        // An: nur „alt.zip“ (Eingang, Zielordner fehlt, Bibliothek A)
        await env.Service.SetEnabledAsync(env.LibraryA, true);
        (await env.Service.ExtractExistingAsync(env.LibraryA)).Should().Be(1);
        await env.Service.WhenIdleAsync().WaitAsync(Timeout);

        File.Exists(Path.Combine(env.InboxA, "alt", "alt.jpg")).Should().BeTrue();
        File.Exists(old).Should().BeTrue("Papierkorb aus");
        Directory.EnumerateFileSystemEntries(Path.Combine(env.InboxA, "erledigt")).Should().BeEmpty();
        Directory.Exists(Path.Combine(env.Fx.LibraryRoot, "archiv")).Should().BeFalse();
        Directory.Exists(Path.Combine(env.InboxB, "andere")).Should().BeFalse();
        var summary = env.Service.LastSummary!;
        summary.Items.Should().ContainSingle().Which.Name.Should().Be("alt.zip");
        summary.HasProblems.Should().BeFalse("schon entpackte Archive werden nicht als „übersprungen“ gemeldet");
        summary.Scan.Should().NotBeNull();
        (await env.Fx.FindFileAsync("alt.jpg")).Should().NotBeNull("der Folge-Scan nimmt die Dateien auf");
        (await env.Service.CountExistingAsync(env.LibraryA)).Should().Be(0, "danach existiert der Zielordner");
    }
}

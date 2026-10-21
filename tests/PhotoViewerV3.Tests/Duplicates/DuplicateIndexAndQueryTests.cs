using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Services.Duplicates;
using PhotoViewerV3.Services.Messaging;

namespace PhotoViewerV3.Tests.Duplicates;

/// <summary>
/// Hintergrund-Index (Vorfilter Größe, gleich/ungleich, veraltete Prüfsummen, Scan-Sperre, Bild-Fingerabdrücke) und
/// Abfragen (Gruppen exakt/ähnlich mit Behalten-Vorschlag, Eingang-Filter, Bereich je Bibliothek, Prüfsummen-Abfrage).
/// </summary>
public sealed class DuplicateIndexAndQueryTests : IAsyncLifetime
{
    private DuplicateWorld _w = null!;

    public async Task InitializeAsync() => _w = await DuplicateWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private DuplicateQuery Exact(bool inboxOnly = false) => new([_w.Library.Id], DuplicateMode.Exact, InboxOnly: inboxOnly);

    [Fact]
    public async Task Vorfilter_hasht_nur_gleich_grosse_Dateien_und_trennt_gleich_von_ungleich()
    {
        _w.Write("a/eins.jpg", DuplicateWorld.Content(1));
        _w.Write("b/zwei.jpg", DuplicateWorld.Content(1));
        _w.Write("c/anders-gleich-gross.jpg", DuplicateWorld.Content(2));
        _w.Write("d/einzeln.jpg", DuplicateWorld.Content(3, 5000));
        await _w.ScanAsync();

        var report = await _w.Index.RunPassAsync();

        report.Hashed.Should().Be(3, "nur die drei Dateien mit 4096 Bytes");
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints WHERE ContentHash IS NOT NULL").Should().Be(3);
        var single = await _w.IdAsync("einzeln.jpg");
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints WHERE FileId = $id AND ContentHash IS NOT NULL", ("$id", single)).Should().Be(0, "einmalige Größe → kein Duplikat möglich");

        var result = await _w.Queries.GetGroupsAsync(Exact());
        result.Groups.Should().ContainSingle();
        result.Groups[0].Members.Select(m => m.Name).Should().BeEquivalentTo("eins.jpg", "zwei.jpg");
        result.PendingContent.Should().Be(0);

        (await _w.Index.RunPassAsync()).Hashed.Should().Be(0, "nichts mehr offen");
    }

    [Fact]
    public async Task Veraenderte_Datei_bekommt_neue_Pruefsumme_und_faellt_bis_dahin_heraus()
    {
        _w.Write("a/eins.jpg", DuplicateWorld.Content(1));
        _w.Write("b/zwei.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        await _w.Index.RunPassAsync();
        var id = await _w.IdAsync("zwei.jpg");

        _w.Exec("UPDATE Files SET ModifiedAtUtc = ModifiedAtUtc + 60 WHERE Id = $id", ("$id", id)); // wie nach einem Incremental-Scan
        (await _w.Queries.GetGroupsAsync(Exact())).Groups.Should().BeEmpty("die Prüfsumme gilt nur für die gelesene Dateiversion");

        (await _w.Index.RunPassAsync()).Hashed.Should().Be(1);
        (await _w.Queries.GetGroupsAsync(Exact())).Groups.Should().ContainSingle();
    }

    [Fact]
    public async Task Eingang_Hinweis_und_Vorschlag_behalten_in_der_Bibliothek()
    {
        _w.Write("2024/foto.jpg", DuplicateWorld.Content(7));
        _w.Write("handy/foto-kopie.jpg", DuplicateWorld.Content(7), inInbox: true);
        _w.Write("handy/neu.jpg", DuplicateWorld.Content(8, 3000), inInbox: true);
        await _w.ScanAsync();
        var hints = new List<InboxDuplicatesFoundMessage>();
        _w.Fx.Messenger.Register<InboxDuplicatesFoundMessage>(this, (_, m) => hints.Add(m));

        await _w.Index.RunPassAsync();

        hints.Should().ContainSingle().Which.Should().Be(new InboxDuplicatesFoundMessage(_w.Library.Id, 1));
        (await _w.Queries.CountInboxAlreadyInLibraryAsync([_w.Library.Id])).Should().Be(1);
        var group = (await _w.Queries.GetGroupsAsync(Exact(inboxOnly: true))).Groups.Should().ContainSingle().Subject;
        group.Members[0].Name.Should().Be("foto.jpg");
        group.Members[0].IsInInbox.Should().BeFalse();
        group.KeeperFileId.Should().Be(group.Members[0].FileId);
        group.KeeperReason.Should().Be("liegt schon in der Bibliothek");

        _w.Exec("UPDATE Files SET ModifiedAtUtc = ModifiedAtUtc + 1 WHERE Name = 'foto-kopie.jpg'");
        await _w.Index.RunPassAsync();
        hints.Should().ContainSingle("gleiche Zahl → kein neuer Hinweis");
    }

    [Fact]
    public async Task Doppelte_nur_im_Bereich_der_Bibliothek()
    {
        _w.Write("a/foto.jpg", DuplicateWorld.Content(5));
        await _w.ScanAsync();
        var otherRoot = Path.Combine(_w.Fx.RootDir, "zweite");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllBytes(Path.Combine(otherRoot, "kopie.jpg"), DuplicateWorld.Content(5));
        int other;
        await using (var ctx = _w.Fx.CreateContext())
        {
            var library = new Library { Name = "Zweite", RootPath = otherRoot, CreatedAt = DateTime.UtcNow, LastOpenedAt = DateTime.UtcNow };
            ctx.Libraries.Add(library);
            await ctx.SaveChangesAsync();
            ctx.LibraryRoots.Add(new LibraryRoot { LibraryId = library.Id, Path = otherRoot, Role = LibraryRootRole.Archive, Name = "zweite", SortOrder = 0 });
            await ctx.SaveChangesAsync();
            other = library.Id;
        }

        await _w.Fx.ScanAsync(other);
        await _w.Index.RunPassAsync();

        (await _w.Queries.GetGroupsAsync(Exact())).Groups.Should().BeEmpty("die Kopie liegt in einer anderen Bibliothek");
        (await _w.Queries.GetGroupsAsync(new DuplicateQuery([other], DuplicateMode.Exact))).Groups.Should().BeEmpty();
        (await _w.Queries.GetGroupsAsync(new DuplicateQuery([_w.Library.Id, other], DuplicateMode.Exact))).Groups
            .Should().ContainSingle("„Alle verknüpften“ über die Gruppe").Which.Members.Select(m => m.LibraryName).Should().BeEquivalentTo("Test", "Zweite");
    }

    [Fact]
    public async Task Pruefsumme_abfragen_rechnet_gleich_grosse_Dateien_sofort()
    {
        var content = DuplicateWorld.Content(9, 6000);
        _w.Write("x/handy.jpg", content);
        await _w.ScanAsync();
        var id = await _w.IdAsync("handy.jpg");
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints").Should().Be(0, "ohne Durchgang noch keine Prüfsumme");

        var hits = await _w.Queries.FindByContentAsync([_w.Library.Id], content.Length, System.Security.Cryptography.SHA256.HashData(content));
        hits.Should().Equal(id);
        (await _w.Queries.FindByContentAsync([_w.Library.Id], content.Length, new byte[32])).Should().BeEmpty();
        (await _w.Queries.FindByContentAsync([_w.Library.Id], 1234, System.Security.Cryptography.SHA256.HashData(content))).Should().BeEmpty();
    }

    [Fact]
    public async Task Durchgang_ruht_solange_die_Bibliothek_belegt_ist()
    {
        _w.Write("a/1.jpg", DuplicateWorld.Content(1));
        _w.Write("a/2.jpg", DuplicateWorld.Content(1));
        await _w.ScanAsync();
        var lease = await _w.Gate.AcquireAsync(_w.Library.Id, CancellationToken.None);
        var pass = _w.Index.RunPassAsync();
        await Task.Delay(600);
        pass.IsCompleted.Should().BeFalse("Scan bzw. Datei-Batch belegt die Bibliothek");

        lease.Dispose();
        (await pass.WaitAsync(TimeSpan.FromSeconds(15))).Hashed.Should().Be(2);
    }

    [Fact]
    public async Task Bild_Fingerabdruecke_und_aehnliche_Gruppen()
    {
        var original = SyntheticImages.Scene(21);
        _w.Loader
            .Add("original.jpg", original)
            .Add("klein.jpg", SyntheticImages.Downscale(original, 320, 240))
            .Add("whatsapp.jpg", SyntheticImages.Recode(SyntheticImages.Downscale(original, 240, 180), 3))
            .Add("anderes.jpg", SyntheticImages.Scene(99))
            .Add("schwarz.jpg", SyntheticImages.Flat(2))
            .Add("schwarz2.jpg", SyntheticImages.Flat(3));
        _w.Write("a/original.jpg", DuplicateWorld.Content(1, 9000));
        _w.Write("a/klein.jpg", DuplicateWorld.Content(2, 5000));
        _w.Write("a/whatsapp.jpg", DuplicateWorld.Content(3, 3000));
        _w.Write("a/anderes.jpg", DuplicateWorld.Content(4, 7000));
        _w.Write("a/schwarz.jpg", DuplicateWorld.Content(5, 1000));
        _w.Write("a/schwarz2.jpg", DuplicateWorld.Content(6, 1100));
        _w.Write("a/kaputt.jpg", DuplicateWorld.Content(7, 1200));
        await _w.ScanAsync();
        _w.Exec("UPDATE Files SET Width = 640, Height = 480 WHERE Name = 'original.jpg'");
        _w.Exec("UPDATE Files SET Width = 320, Height = 240 WHERE Name = 'klein.jpg'");
        _w.Exec("UPDATE Files SET Width = 240, Height = 180 WHERE Name = 'whatsapp.jpg'");

        var report = await _w.Index.RunPassAsync();

        report.Fingerprinted.Should().Be(6);
        report.Failed.Should().Be(1, "kaputt.jpg ist nicht dekodierbar");
        _w.Scalar<string>("SELECT h.ImageError FROM FileFingerprints h JOIN Files f ON f.Id = h.FileId WHERE f.Name = 'kaputt.jpg'").Should().Be("decode");
        _w.Scalar<int>("SELECT COUNT(*) FROM FileFingerprints WHERE ImageLowDetail = 1").Should().Be(2);
        (await _w.Index.RunPassAsync()).Fingerprinted.Should().Be(0, "kein neuer Versuch mit derselben Version");

        var result = await _w.Queries.GetGroupsAsync(new DuplicateQuery([_w.Library.Id], DuplicateMode.Similar, DuplicateSettings.DefaultSimilarThreshold));
        var group = result.Groups.Should().ContainSingle("einfarbige Bilder zählen nicht, das andere Motiv ist fern").Subject;
        group.Members.Select(m => m.Name).Should().Equal("original.jpg", "klein.jpg", "whatsapp.jpg");
        group.KeeperReason.Should().Be("höhere Auflösung (640 × 480 statt 320 × 240)");
        group.Members[0].Distance.Should().Be(0, "Abstand zum Vorschlag");
        group.Members.Skip(1).Should().OnlyContain(m => m.Distance <= 2 * DuplicateSettings.DefaultSimilarThreshold && !m.IsExactCopy);
        result.PendingImages.Should().Be(0);
    }

    [Fact]
    public async Task Videos_nur_ueber_ihr_Vorschaubild()
    {
        _w.Write("v/film.mp4", DuplicateWorld.Content(1, 8000));
        await _w.ScanAsync();
        (await _w.Index.RunPassAsync()).Fingerprinted.Should().Be(0, "ohne Vorschaubild kein Fingerabdruck");
        _w.Loader.Loaded.Should().BeEmpty("das Video selbst wird nie geöffnet");
    }
}

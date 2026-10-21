using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Data.Migrations;
using Lumora.Domain.Entities;
using Lumora.Services.Library;
using Lumora.Services.Motifs;
using Lumora.Services.Places;
using Lumora.Tests.Fixtures;
using Lumora.Tests.Motifs;
using static Lumora.Tests.Motifs.MotifIndexServiceTests;

namespace Lumora.Tests.Libraries;

/// <summary>
/// Zusammenbau 0.2.56 (ADR-0048 Nachtrag): Bibliotheks-Trennung auch für Motive (ADR-0052) und Orte (ADR-0050/0051).
/// Zwei Bibliotheken mit eigenen Motiv-Begriffen, Bild-Vektoren und verorteten Fotos: nichts aus A in B (Begriffe,
/// Zähler, Treffer, Bildinhalt-Suche, Kartenpunkte, Ortszähler); verknüpft → beides, gleichnamige Begriffe eins, fehlende
/// Treffer werden aus den gespeicherten Vektoren nachgerechnet; gelöst → wieder getrennt, Begriffe in beiden; Bibliothek
/// entfernen nimmt gemeinsame Begriffe nicht mit (Anker). Migration <c>MotifTermsByGroup</c> führt Duplikate zusammen.
/// </summary>
public sealed class MotifPlacesScopingTests
{
    private const string Version = "fake-motif/v1";
    private static readonly ReverseGeocoder Geocoder = new();

    private sealed class World : IDisposable
    {
        private World(TempLibraryFixture fx, Library a, Library b, int fileA, int fileB)
        {
            Fx = fx;
            A = a.Id;
            B = b.Id;
            FileA = fileA;
            FileB = fileB;
            Scope = new LibraryScope(fx.ContextFactory, new TestCatalogContext(), fx.Messenger);
            Store = new MotifStore(fx.ContextFactory);
            Terms = new MotifTermService(Store, new FakeMotifEncoderFactory(Version), new MemoryMotifSettingsStore(MotifSettings.Default),
                NullLogger<MotifTermService>.Instance, fx.Messenger, Scope);
            Libraries = fx.CreateLibraryService();
            Linking = new LibraryLinkService(fx.ContextFactory, new TestCatalogContext(), NullLogger<LibraryLinkService>.Instance, fx.Messenger);
            Geo = new GeoQueryService(fx.ContextFactory, Geocoder, Geocoder);
        }

        public TempLibraryFixture Fx { get; }

        public int A { get; }

        public int B { get; }

        public int FileA { get; }

        public int FileB { get; }

        public LibraryScope Scope { get; }

        public MotifStore Store { get; }

        public MotifTermService Terms { get; }

        public LibraryService Libraries { get; }

        public LibraryLinkService Linking { get; }

        public GeoQueryService Geo { get; }

        /// <summary>A: „a.jpg“ (Hund, Wien), B: „b.jpg“ (Hund, Graz); beide analysiert, jede Bibliothek mit eigener Gruppe.</summary>
        public static async Task<World> CreateAsync()
        {
            var fx = await TempLibraryFixture.CreateAsync();
            var a = await fx.CreateLibraryAsync("A");
            fx.WriteLibraryFile("a.jpg");
            await fx.ScanAsync(a.Id);
            var b = await CreateSecondLibraryAsync(fx);
            TempLibraryFixture.WriteFile(Path.Combine(fx.OutsideDir, "b.jpg"));
            await fx.ScanAsync(b.Id);
            var w = new World(fx, a, b, (await fx.FindFileAsync("a.jpg"))!.Id, (await fx.FindFileAsync("b.jpg"))!.Id);

            await w.Libraries.SetActiveAsync(w.A);
            (await w.Terms.GetTermsAsync(w.A)).Should().NotBeEmpty("Start-Liste in A");
            await w.Terms.AddTermAsync(w.A, "Leuchtturm-A", "a dog");
            await w.Terms.GetTermsAsync(w.B);
            await w.Terms.EnsureTextVectorsAsync(w.B);
            await w.Store.WriteAsync(w.A, [new(w.FileA, Version, FakeMotifEncoderFactory.Unit(1))], MotifScoring.Siglip2Base, 0.01, Version, CancellationToken.None);
            await w.Store.WriteAsync(w.B, [new(w.FileB, Version, FakeMotifEncoderFactory.Unit(1))], MotifScoring.Siglip2Base, 0.01, Version, CancellationToken.None);
            w.Locate(w.FileA, 48.2082, 16.3738);
            w.Locate(w.FileB, 47.0707, 15.4395);
            return w;
        }

        public void Locate(int fileId, double lat, double lon)
            => Exec(Fx, string.Create(CultureInfo.InvariantCulture,
                $"UPDATE Files SET Latitude = {lat}, Longitude = {lon}, GeoSource = 3, PlaceId = {Geocoder.Nearest(lat, lon)!.PlaceId} WHERE Id = {fileId};"));

        public async Task<IReadOnlyList<int>> CurrentLibrariesAsync() => (await Scope.GetCurrentAsync()).LibraryIds;

        public async Task<List<int>> PointsAsync() => (await Geo.GetPointsAsync(await CurrentLibrariesAsync(), null, CancellationToken.None)).Select(p => p.FileId).ToList();

        public async Task<int> PlacedAsync() => (await Geo.GetPlaceCountsAsync(await CurrentLibrariesAsync())).Places.Sum(p => p.Count);

        public async Task<List<int>> SearchAsync(int libraryId) => (await Terms.SearchAsync(libraryId, "dog", 10)).Select(h => h.FileId).ToList();

        public void Dispose()
        {
            Terms.Dispose();
            Linking.Dispose();
            Fx.Dispose();
        }
    }

    [Fact]
    public async Task Motive_und_Orte_aus_A_nicht_in_B_verknuepft_in_beiden_geloest_wieder_getrennt()
    {
        using var w = await World.CreateAsync();

        // ---- getrennt: B sieht nichts aus A (und umgekehrt)
        await w.Libraries.SetActiveAsync(w.B);
        var termsB = await w.Terms.GetTermsAsync(w.B);
        var termsA = await w.Terms.GetTermsAsync(w.A);
        termsB.Should().NotContain(t => t.Name == "Leuchtturm-A", "eigener Begriff aus A ist in B unsichtbar");
        termsB.Select(t => t.Id).Should().NotIntersectWith(termsA.Select(t => t.Id), "jede Gruppe hat ihre eigenen Begriffe");
        termsB.Single(t => t.Name == "Hund").FileCount.Should().Be(1, "nur das Foto aus B");
        Hits(w.Fx, "Leuchtturm-A").Should().Equal("a.jpg");
        (await w.SearchAsync(w.B)).Should().Equal(w.FileB);
        (await w.PointsAsync()).Should().Equal(w.FileB);
        (await w.PlacedAsync()).Should().Be(1);
        await w.Libraries.SetActiveAsync(w.A);
        (await w.PointsAsync()).Should().Equal(w.FileA);
        (await w.SearchAsync(w.A)).Should().Equal(w.FileA);
        AssertConsistent(w.Fx);

        // ---- verknüpft (A nimmt B auf): gleichnamige eins, „Leuchtturm-A“ wird für b.jpg nachgerechnet
        var link = await w.Linking.LinkAsync(w.A, w.B);
        link.MotifTermsMerged.Should().Be(MotifDefaults.Terms.Count, "die Start-Listen beider Seiten werden eins");
        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms WHERE Name = 'Hund'").Should().Be(1);
        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms WHERE Name = 'Hund' AND TextEmbedding IS NOT NULL").Should().Be(1, "gleicher Begriff auf beiden Seiten → Vektor bleibt");
        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms WHERE Name = 'Leuchtturm-A' AND TextEmbedding IS NULL").Should().Be(1, "b.jpg wurde nie gegen ihn verglichen");
        Hits(w.Fx, "Hund").Should().Equal("a.jpg", "b.jpg");
        (await w.Terms.EnsureTextVectorsAsync(w.B)).Should().Be(1);
        Hits(w.Fx, "Leuchtturm-A").Should().Equal("a.jpg", "b.jpg");
        await w.Libraries.SetActiveAsync(w.B);
        var linkedB = await w.Terms.GetTermsAsync(w.B);
        linkedB.Should().Contain(t => t.Name == "Leuchtturm-A", "verknüpft: Begriff aus A in B sichtbar");
        linkedB.Single(t => t.Name == "Hund").FileCount.Should().Be(1, "B einzeln zählt nur seine Treffer");
        (await w.SearchAsync(w.B)).Should().Equal([w.FileB], "B einzeln sucht nur in B");
        (await w.PointsAsync()).Should().Equal([w.FileB], "B einzeln zeigt nur seine Orte");
        AssertConsistent(w.Fx);

        // ---- „Alle verknüpften“: beides, Zähler = Summe
        await w.Scope.ShowAllLinkedAsync((await w.Scope.GetForLibraryAsync(w.B)).GroupId);
        var current = await w.Scope.GetCurrentAsync();
        current.IsAllLinked.Should().BeTrue();
        (await w.Terms.GetTermsAsync(current.LibraryId)).Single(t => t.Name == "Hund").FileCount.Should().Be(2);
        (await w.SearchAsync(current.LibraryId)).Should().BeEquivalentTo([w.FileA, w.FileB]);
        (await w.PointsAsync()).Should().Equal(w.FileA, w.FileB);
        (await w.PlacedAsync()).Should().Be(2);

        // ---- gelöst: wieder getrennt, Begriffe (mit Vektor) in beiden, Treffer von b.jpg an den Kopien
        var unlink = await w.Linking.UnlinkAsync(w.B);
        unlink.MotifTermsCopied.Should().Be(MotifDefaults.Terms.Count + 1);
        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms WHERE Name = 'Leuchtturm-A' AND TextEmbedding IS NOT NULL").Should().Be(2);
        Hits(w.Fx, "Leuchtturm-A").Should().Equal("a.jpg", "b.jpg");
        AssertConsistent(w.Fx);
        await w.Libraries.SetActiveAsync(w.A);
        (await w.Terms.GetTermsAsync(w.A)).Single(t => t.Name == "Leuchtturm-A").FileCount.Should().Be(1);
        (await w.PointsAsync()).Should().Equal(w.FileA);
        (await w.SearchAsync(w.A)).Should().Equal(w.FileA);
        await w.Libraries.SetActiveAsync(w.B);
        var afterB = await w.Terms.GetTermsAsync(w.B);
        afterB.Single(t => t.Name == "Leuchtturm-A").FileCount.Should().Be(1);
        afterB.Select(t => t.Id).Should().NotIntersectWith((await w.Terms.GetTermsAsync(w.A)).Select(t => t.Id));
        (await w.PointsAsync()).Should().Equal(w.FileB);

        // Neue Begriffe nach dem Lösen bleiben getrennt; die Start-Liste kommt in B nicht wieder (Merker kopiert)
        Scalar(w.Fx, $"SELECT COUNT(*) FROM Settings WHERE Key = 'motifs.seeded.g' || (SELECT GroupId FROM Libraries WHERE Id = {w.B})").Should().Be(1);
        await w.Terms.DeleteTermAsync(afterB.Single(t => t.Name == "Katze").Id);
        (await w.Terms.GetTermsAsync(w.B)).Should().NotContain(t => t.Name == "Katze", "gelöschter Standard-Begriff kommt nicht wieder");
        await w.Terms.AddTermAsync(w.B, "Nur-B");
        (await w.Terms.GetTermsAsync(w.A)).Should().NotContain(t => t.Name == "Nur-B");
        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms WHERE Name = 'Hund'").Should().Be(2);
    }

    [Fact]
    public async Task Bibliothek_entfernen_laesst_gemeinsame_Motiv_Begriffe_der_Gruppe()
    {
        using var w = await World.CreateAsync();
        await w.Linking.LinkAsync(w.A, w.B);
        var before = Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms");
        Scalar(w.Fx, $"SELECT COUNT(*) FROM MotifTerms WHERE LibraryId = {w.A}").Should().BeGreaterThan(0, "Begriffe aus A haben A als Anker");

        await w.Libraries.DeleteAsync(w.A);

        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms").Should().Be(before, "die Kaskade des Ankers nimmt nichts mit");
        Scalar(w.Fx, $"SELECT COUNT(*) FROM MotifTerms WHERE LibraryId <> {w.B}").Should().Be(0);
        Hits(w.Fx, "Hund").Should().Equal("b.jpg");
        (await w.Terms.GetTermsAsync(w.B)).Should().Contain(t => t.Name == "Leuchtturm-A");

        // Letzte Bibliothek der Gruppe → Gruppe samt Begriffen und Merker weg
        await w.Libraries.DeleteAsync(w.B);
        Scalar(w.Fx, "SELECT COUNT(*) FROM MotifTerms").Should().Be(0);
        Scalar(w.Fx, "SELECT COUNT(*) FROM Settings WHERE Key LIKE 'motifs.seeded.%'").Should().Be(0);
    }

    [Fact]
    public async Task Migration_fuehrt_gleichnamige_Begriffe_einer_Gruppe_zusammen_und_merkt_die_Start_Liste_je_Gruppe()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var a = await fx.CreateLibraryAsync("A");
        fx.WriteLibraryFile("a.jpg");
        await fx.ScanAsync(a.Id);
        var b = await CreateSecondLibraryAsync(fx);
        TempLibraryFixture.WriteFile(Path.Combine(fx.OutsideDir, "b.jpg"));
        await fx.ScanAsync(b.Id);
        var c = await fx.CreateLibraryAsync("C");
        var fileA = (await fx.FindFileAsync("a.jpg"))!.Id;
        var fileB = (await fx.FindFileAsync("b.jpg"))!.Id;

        // Stand vor der Migration: A und B schon verknüpft (Gruppe 1), C allein; Begriffe noch je Bibliothek
        Exec(fx, $"""
            DROP INDEX UX_MotifTerms_Group_Name;
            INSERT INTO LibraryGroups (Id, Name, CreatedAtUtc) VALUES (1, 'AB', 0), (2, 'C', 0);
            UPDATE Libraries SET GroupId = 1 WHERE Id IN ({a.Id}, {b.Id});
            UPDATE Libraries SET GroupId = 2 WHERE Id = {c.Id};
            INSERT INTO MotifTerms (Id, LibraryId, GroupId, Name, SortOrder, TextEmbedding, TextModelVersion, CreatedAtUtc) VALUES
                (10, {a.Id}, 0, 'Hund', 0, X'00', '{Version}', 0),
                (11, {b.Id}, 0, 'hund', 0, X'00', '{Version}', 0),
                (12, {b.Id}, 0, 'Katze', 1, X'00', '{Version}', 0),
                (13, {c.Id}, 0, 'Hund', 0, X'00', '{Version}', 0);
            INSERT INTO FileMotifs (FileId, TermId, Score) VALUES ({fileA}, 10, 0.2), ({fileB}, 11, 0.3), ({fileB}, 12, 0.4);
            INSERT INTO Settings (Key, Value) VALUES ('motifs.seeded.{a.Id}', 'true'), ('motifs.seeded.{c.Id}', 'true');
            """);

        Exec(fx, MotifTermsByGroup.GroupMotifTermsSql + "CREATE UNIQUE INDEX UX_MotifTerms_Group_Name ON MotifTerms (GroupId, Name);");

        Scalar(fx, "SELECT GroupId FROM MotifTerms WHERE Id = 10").Should().Be(1);
        Scalar(fx, "SELECT COUNT(*) FROM MotifTerms WHERE Id = 11").Should().Be(0, "„hund“ aus B geht in „Hund“ (kleinste Id) auf");
        Scalar(fx, "SELECT GroupId FROM MotifTerms WHERE Id = 13").Should().Be(2, "C bleibt getrennt");
        Hits(fx, "Hund").Should().Equal("a.jpg", "b.jpg");
        Scalar(fx, $"SELECT Score > 0.29 FROM FileMotifs WHERE FileId = {fileB} AND TermId = 10").Should().Be(1, "Treffer von B umgehängt");
        Scalar(fx, "SELECT COUNT(*) FROM MotifTerms WHERE GroupId = 1 AND TextEmbedding IS NOT NULL").Should().Be(0, "verknüpfte Gruppe: Vektoren werden neu gerechnet");
        Scalar(fx, "SELECT COUNT(*) FROM MotifTerms WHERE Id = 13 AND TextEmbedding IS NOT NULL").Should().Be(1, "einzelne Bibliothek unverändert");
        Scalar(fx, "SELECT COUNT(*) FROM Settings WHERE Key IN ('motifs.seeded.g1', 'motifs.seeded.g2')").Should().Be(2);
        Scalar(fx, "SELECT COUNT(*) FROM Settings WHERE Key LIKE 'motifs.seeded.%' AND Key NOT LIKE 'motifs.seeded.g%'").Should().Be(0);
        AssertConsistent(fx);
    }

    /// <summary>Jeder Treffer gehört zu einem Begriff der Gruppe seiner Datei; jeder Anker liegt in der Gruppe des Begriffs.</summary>
    private static void AssertConsistent(TempLibraryFixture fx)
    {
        Scalar(fx, """
            SELECT COUNT(*) FROM FileMotifs m JOIN MotifTerms t ON t.Id = m.TermId JOIN Files f ON f.Id = m.FileId
            WHERE t.GroupId <> (SELECT l.GroupId FROM Libraries l WHERE l.Id = f.LibraryId)
            """).Should().Be(0, "kein Treffer an einem Begriff einer anderen Gruppe");
        Scalar(fx, """
            SELECT COUNT(*) FROM MotifTerms t
            WHERE NOT EXISTS (SELECT 1 FROM Libraries l WHERE l.Id = t.LibraryId AND l.GroupId = t.GroupId)
            """).Should().Be(0, "Anker-Bibliothek in der Gruppe des Begriffs");
    }

    private static void Exec(TempLibraryFixture fx, string sql)
    {
        using SqliteConnection connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

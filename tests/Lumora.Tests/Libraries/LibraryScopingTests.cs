using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Categories;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Library;
using Lumora.Services.Scanning;
using Lumora.Services.Tags;
using Lumora.Tests.Faces.Assignment;
using Lumora.Tests.Fixtures;

namespace Lumora.Tests.Libraries;

/// <summary>
/// Bibliotheks-Trennung (ADR-0048): zwei Bibliotheken mit gleich benannten und verschiedenen Tags, Kategorien, Personen
/// und Beziehungen — nichts aus A erscheint in B (Listen, Zähler, Suche nach Namen, Vorschläge, Lernen, Zuweisen über
/// Bibliotheksgrenzen). Verknüpfen führt gleichnamige Begriffe zusammen (Personen nach Wahl), Lösen kopiert alles
/// zurück — beides verlustfrei. Ansicht „Alle verknüpften“ zählt über beide. Versteckter Katalog: Verknüpfen gesperrt.
/// </summary>
public sealed class LibraryScopingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>A = Bibliothek der Welt (LibraryRoot), B = zweite Bibliothek (OutsideDir) mit eigenen Fotos.</summary>
    private sealed class TwoLibraries : IDisposable
    {
        private int _bCounter;

        private TwoLibraries(FaceAssignmentWorld world, int a, int b, LibraryScope scope)
        {
            World = world;
            A = a;
            B = b;
            Scope = scope;
            Libraries = world.Fx.CreateLibraryService();
            Tags = new TagService(world.Fx.ContextFactory, NullLogger<TagService>.Instance, world.Fx.Messenger, scope);
            Categories = new CategoryService(world.Fx.ContextFactory, NullLogger<CategoryService>.Instance, world.Fx.Messenger, scope);
            Catalog = new TestCatalogContext();
            Linking = new LibraryLinkService(world.Fx.ContextFactory, Catalog, NullLogger<LibraryLinkService>.Instance, world.Fx.Messenger);
        }

        public FaceAssignmentWorld World { get; }

        public TempLibraryFixture Fx => World.Fx;

        public int A { get; }

        public int B { get; }

        public LibraryScope Scope { get; }

        public LibraryService Libraries { get; }

        public TagService Tags { get; }

        public CategoryService Categories { get; }

        public TestCatalogContext Catalog { get; }

        public LibraryLinkService Linking { get; }

        public static async Task<TwoLibraries> CreateAsync()
        {
            var world = await FaceAssignmentWorld.CreateAsync();
            var scope = new LibraryScope(world.Fx.ContextFactory, new TestCatalogContext(), world.Fx.Messenger);
            (await scope.GetForLibraryAsync(world.LibraryId)).GroupId.Should().NotBe(0, "A bekommt beim ersten Zugriff ihre Gruppe");
            var b = await world.Fx.CreateLibraryService().CreateAsync("Zweite", world.Fx.OutsideDir);
            return new TwoLibraries(world, world.LibraryId, b.Id, scope);
        }

        /// <summary>Fotos in B (Datei unter OutsideDir + Scan von B).</summary>
        public async Task<List<int>> CreateFilesInBAsync(int count)
        {
            var names = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var name = $"zweite{++_bCounter:D4}.jpg";
                TempLibraryFixture.WriteFile(Path.Combine(Fx.OutsideDir, name));
                names.Add(name);
            }

            await Fx.CreateScanService().ScanAsync(B, ScanMode.Incremental, null, CancellationToken.None);
            var ids = new List<int>();
            foreach (var name in names)
            {
                ids.Add((await Fx.FindFileAsync(name))!.Id);
            }

            return ids;
        }

        public long Scalar(string sql, params (string, object)[] parameters) => World.Scalar(sql, parameters);

        public void Dispose()
        {
            Tags.Dispose();
            Categories.Dispose();
            Linking.Dispose();
            World.Dispose();
        }
    }

    [Fact]
    public async Task Bibliothek_B_sieht_nichts_aus_A_weder_Begriffe_noch_Personen_noch_Vorschlaege()
    {
        using var t = await TwoLibraries.CreateAsync();
        using var faces = t.World.CreateService();
        var annaFacesA = await t.World.AddFacesAsync("anna", 6);
        var filesA = annaFacesA.Select(id => t.World.Face(id).FileId).ToList();
        var filesB = await t.CreateFilesInBAsync(4);
        var facesB = filesB.Select(f => t.World.InsertFace(f, t.World.Embedding("anna"))).ToList();

        // A: Tags, Kategorie, Person mit Beziehung, gelernt
        await t.Libraries.SetActiveAsync(t.A);
        var urlaubA = await t.Tags.CreateAsync("Urlaub");
        var nurA = await t.Tags.CreateAsync("NurA");
        (await t.Tags.AssignAsync([filesA[0]], [urlaubA.Id, nurA.Id])).Should().Be(2);
        var projekt = await t.Categories.CreateAsync("Projekt", ["Nord"]);
        (await t.Categories.SetValueAsync([filesA[0]], projekt.Id, projekt.Values[0].Id)).Should().Be(1);
        var annaId = await faces.CreatePersonAsync(new PersonDetailsInput("Anna", "Muster", Relation: "Schwester"));
        await faces.ConfirmManyAsync(annaFacesA.Take(4).ToList(), annaId);
        await faces.WaitForIdleAsync().WaitAsync(Wait);
        t.World.Face(annaFacesA[5]).SuggestedPersonId.Should().Be(annaId, "in A lernt die Erkennung aus A");

        // B: nichts aus A
        await t.Libraries.SetActiveAsync(t.B);
        faces.RequestRetrain();
        await faces.WaitForIdleAsync().WaitAsync(Wait);
        (await t.Tags.GetAllAsync()).Should().BeEmpty("Tags aus A sind in B unsichtbar");
        (await t.Tags.GetFileCountsAsync()).Should().BeEmpty();
        (await t.Categories.GetAllAsync()).Should().BeEmpty();
        (await t.Categories.GetFileCountsAsync()).Should().BeEmpty();
        (await faces.GetPersonOverviewsAsync()).Should().BeEmpty("Personen aus A sind in B unsichtbar");
        (await faces.GetAllPersonDetailsAsync()).Should().BeEmpty();
        (await faces.GetRelationsAsync()).Should().BeEmpty();
        (await faces.FindPersonByNameAsync("Anna Muster")).Should().BeNull();
        (await faces.GetPersonFacesAsync(annaId)).Should().BeNull();
        (await faces.GetPersonDetailsAsync(annaId)).Should().BeNull();
        foreach (var id in facesB)
        {
            t.World.Face(id).Should().Match<FaceRow>(f => f.State == FaceState.Unconfirmed && f.SuggestedPersonId == null,
                "kein Vorschlag aus A, obwohl das Gesicht Anna gleicht");
        }

        var groups = await faces.GetGroupsAsync(refresh: true);
        groups.Groups.SelectMany(g => g.Members).Select(m => m.FaceId).Should().OnlyContain(id => facesB.Contains(id), "Gruppen nur aus B");
        groups.Groups.Should().OnlyContain(g => g.SuggestedPersonId == null, "keine Person aus A als Gruppen-Vorschlag");
        (await faces.CheckOutliersAsync([facesB[0]], null)).Should().OnlyContain(r => !r.Check.IsOutlier && r.Check.CloserPersonId == null, "keine Rückfrage „ähnelt Anna“");

        // Zuweisen über Bibliotheksgrenzen wirkt nicht (Datenbank-Schutz)
        (await t.Tags.AssignAsync([filesB[0]], [urlaubA.Id])).Should().Be(0);
        (await t.Categories.SetValueAsync([filesB[0]], projekt.Id, projekt.Values[0].Id)).Should().Be(0);
        await faces.ConfirmManyAsync([facesB[0]], annaId);
        t.World.Face(facesB[0]).PersonId.Should().BeNull("Person aus A nie an einem Gesicht in B");

        // gleiche Namen in B erlaubt, eigene Begriffe
        var urlaubB = await t.Tags.CreateAsync("Urlaub");
        urlaubB.Id.Should().NotBe(urlaubA.Id);
        var annaB = await faces.CreatePersonAsync(new PersonDetailsInput("Anna", "Muster", Relation: "Schwester"));
        annaB.Should().NotBe(annaId);
        (await faces.GetRelationsAsync()).Should().ContainSingle(r => r.Name == "Schwester");
        t.Scalar("SELECT COUNT(*) FROM PersonRelations WHERE Name = 'Schwester'").Should().Be(2, "je Gruppe eine eigene Beziehung");

        // Zurück in A: dort unverändert
        await t.Libraries.SetActiveAsync(t.A);
        (await t.Tags.GetAllAsync()).Select(x => x.Name).Should().Equal("Urlaub", "NurA");
        (await t.Tags.GetFileCountsAsync())[urlaubA.Id].Should().Be(1);
        (await faces.GetPersonOverviewsAsync()).Should().ContainSingle(p => p.Person.Id == annaId);
    }

    [Fact]
    public async Task Verknuepfen_und_Loesen_sind_verlustfrei_und_Alle_verknuepften_zaehlt_ueber_beide()
    {
        using var t = await TwoLibraries.CreateAsync();
        using var faces = t.World.CreateService(start: false);
        var annaFaceA = (await t.World.AddFacesAsync("anna", 1))[0];
        var fileA = t.World.Face(annaFaceA).FileId;
        var filesB = await t.CreateFilesInBAsync(2);
        var annaFaceB = t.World.InsertFace(filesB[0], t.World.Embedding("anna"));
        var bobFaceB = t.World.InsertFace(filesB[1], t.World.Embedding("bob"));

        await t.Libraries.SetActiveAsync(t.A);
        var urlaubA = await t.Tags.CreateAsync("Urlaub");
        var nurA = await t.Tags.CreateAsync("NurA");
        await t.Tags.AssignAsync([fileA], [urlaubA.Id, nurA.Id]);
        var projektA = await t.Categories.CreateAsync("Projekt", ["Nord", "Süd"]);
        await t.Categories.SetValueAsync([fileA], projektA.Id, projektA.Values[0].Id);
        var annaA = await faces.CreatePersonAsync(new PersonDetailsInput("Anna", "Muster", Relation: "Schwester", IsMe: true));
        await faces.ConfirmManyAsync([annaFaceA], annaA);
        var bobA = await faces.CreatePersonAsync("Bob");

        await t.Libraries.SetActiveAsync(t.B);
        var urlaubB = await t.Tags.CreateAsync("urlaub"); // andere Schreibweise: wird trotzdem eins
        var nurB = await t.Tags.CreateAsync("NurB");
        await t.Tags.AssignAsync([filesB[0]], [urlaubB.Id, nurB.Id]);
        var projektB = await t.Categories.CreateAsync("Projekt", ["Süd", "West"]);
        await t.Categories.SetValueAsync([filesB[0]], projektB.Id, projektB.Values[1].Id); // West
        await t.Categories.SetValueAsync([filesB[1]], projektB.Id, projektB.Values[0].Id); // Süd
        var annaB = await faces.CreatePersonAsync(new PersonDetailsInput("Anna", "Muster", Relation: "Schwester", IsMe: true));
        await faces.ConfirmManyAsync([annaFaceB], annaB);
        var bobB = await faces.CreatePersonAsync("Bob");
        await faces.ConfirmManyAsync([bobFaceB], bobB);
        await faces.RejectSuggestionsAsync([]); // nichts
        const string fileTagsSql = "SELECT COUNT(*) FROM FileTags";
        const string fileValuesSql = "SELECT COUNT(*) FROM FileCategoryValues";
        var fileTagsBefore = t.Scalar(fileTagsSql);
        var fileValuesBefore = t.Scalar(fileValuesSql);

        // Vorschau
        var preview = await t.Linking.PreviewLinkAsync(t.A, t.B);
        preview.TagsMerged.Should().Be(1);
        preview.TagsMoved.Should().Be(1);
        preview.CategoriesMerged.Should().Be(1);
        preview.RelationsMerged.Should().Be(1);
        preview.PersonConflicts.Select(c => c.Name).Should().BeEquivalentTo(["Anna Muster", "Bob"]);

        // Verknüpfen: Anna zusammenführen (Standard), Bob getrennt halten
        var groupsChanged = 0;
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Register<LibraryGroupsChangedMessage>(t.Fx.Messenger, this, (_, _) => Interlocked.Increment(ref groupsChanged));
        var result = await t.Linking.LinkAsync(t.A, t.B, [bobB]);
        groupsChanged.Should().Be(1);
        result.PersonsMerged.Should().Be(1);
        result.PersonsKeptApart.Should().Be(1);
        var group = (await t.Scope.GetForLibraryAsync(t.A)).GroupId;
        (await t.Scope.GetForLibraryAsync(t.B)).GroupId.Should().Be(group);
        (await t.Scope.GetForLibraryAsync(t.A)).GroupLibraryIds.Should().BeEquivalentTo([t.A, t.B]);
        t.Scalar(fileTagsSql).Should().Be(fileTagsBefore, "keine Tag-Zuordnung geht verloren");
        t.Scalar(fileValuesSql).Should().Be(fileValuesBefore, "keine Kategorie-Zuordnung geht verloren");
        (await t.Tags.GetAllAsync()).Select(x => x.Name).Should().BeEquivalentTo(["Urlaub", "NurA", "NurB"]);
        t.Scalar("SELECT TagId FROM FileTags WHERE FileId = $f AND TagId = $t", ("$f", filesB[0]), ("$t", urlaubA.Id)).Should().Be(urlaubA.Id, "B-Datei trägt das gemeinsame Tag");
        var projekt = (await t.Categories.GetAllAsync()).Should().ContainSingle().Subject;
        projekt.Values.Select(v => v.Name).Should().Equal("Nord", "Süd", "West");
        var values = await t.Categories.GetValuesForFilesAsync([fileA, filesB[0], filesB[1]]);
        projekt.FindValue(values[fileA][projekt.Id])!.Name.Should().Be("Nord");
        projekt.FindValue(values[filesB[0]][projekt.Id])!.Name.Should().Be("West");
        projekt.FindValue(values[filesB[1]][projekt.Id])!.Name.Should().Be("Süd", "gleichnamiger Wert wird eins");
        t.World.Face(annaFaceB).PersonId.Should().Be(annaA, "gleichnamige Person zusammengeführt");
        t.World.Face(bobFaceB).PersonId.Should().Be(bobB, "getrennt gehaltene Person bleibt");
        var persons = await faces.GetAllPersonDetailsAsync();
        persons.Select(p => Lumora.Domain.Entities.PersonNames.DisplayName(p.FirstName, p.LastName, p.Nickname))
            .Should().BeEquivalentTo(["Anna Muster", "Bob", "Bob (Zweite)"]);
        persons.Count(p => p.IsMe).Should().Be(1, "„Das bin ich“ höchstens einmal je Gruppe");
        (await faces.GetRelationsAsync()).Should().ContainSingle(r => r.Name == "Schwester" && r.PersonCount == 1);

        // Ansicht „Alle verknüpften“: Bereich = beide Bibliotheken, Zähler = Summe
        await t.Scope.ShowAllLinkedAsync(group);
        var all = await t.Scope.GetCurrentAsync();
        all.IsAllLinked.Should().BeTrue();
        all.LibraryIds.Should().BeEquivalentTo([t.A, t.B]);
        (await t.Tags.GetFileCountsAsync())[urlaubA.Id].Should().Be(2, "Zähler über beide Bibliotheken");
        (await faces.GetPersonOverviewsAsync()).Single(p => p.Person.Id == annaA).Person.FaceCount.Should().Be(2);
        await t.Libraries.SetActiveAsync(t.A);
        (await t.Scope.GetCurrentAsync()).IsAllLinked.Should().BeFalse("eine einzelne Bibliothek öffnen beendet die gemeinsame Ansicht");
        (await t.Tags.GetFileCountsAsync())[urlaubA.Id].Should().Be(1);

        // Lösen: B bekommt Kopien von allem, Zuordnungen bleiben bei ihrer Bibliothek
        var unlink = await t.Linking.UnlinkAsync(t.B);
        unlink.TagsCopied.Should().Be(3);
        unlink.PersonsCopied.Should().Be(3);
        var groupB = (await t.Scope.GetForLibraryAsync(t.B)).GroupId;
        groupB.Should().NotBe(group);
        t.Scalar(fileTagsSql).Should().Be(fileTagsBefore);
        t.Scalar(fileValuesSql).Should().Be(fileValuesBefore);
        (await t.Tags.GetAllAsync()).Select(x => x.Name).Should().BeEquivalentTo(["Urlaub", "NurA", "NurB"], "A behält alles");
        await t.Libraries.SetActiveAsync(t.B);
        var tagsB = await t.Tags.GetAllAsync();
        tagsB.Select(x => x.Name).Should().BeEquivalentTo(["Urlaub", "NurA", "NurB"], "B bekommt Kopien aller gemeinsamen Begriffe");
        tagsB.Select(x => x.Id).Should().NotContain([urlaubA.Id, nurA.Id]);
        (await t.Tags.GetTagIdsForFilesAsync([filesB[0]]))[filesB[0]].Should().BeSubsetOf(tagsB.Select(x => x.Id), "B-Dateien zeigen auf die Kopien");
        (await t.Tags.GetFileCountsAsync()).Values.Sum().Should().Be(2);
        var personsB = await faces.GetPersonOverviewsAsync();
        personsB.Should().HaveCount(3);
        var annaCopy = personsB.Single(p => p.Person.Name == "Anna Muster").Person.Id;
        annaCopy.Should().NotBe(annaA);
        t.World.Face(annaFaceB).PersonId.Should().Be(annaCopy, "Gesichter von B zeigen auf die Kopie");
        t.World.Face(annaFaceA).PersonId.Should().Be(annaA, "Gesichter von A bleiben");
        (await faces.GetRelationsAsync()).Should().ContainSingle(r => r.Name == "Schwester" && r.PersonCount == 1);
        (await t.Scope.GetCurrentAsync()).IsLinked.Should().BeFalse();
        (await t.Categories.GetAllAsync()).Should().ContainSingle(c => c.Name == "Projekt" && c.Values.Count == 3);
    }

    [Fact]
    public async Task Versteckter_Katalog_verbietet_Verknuepfen_und_gemeinsame_Ansicht()
    {
        using var t = await TwoLibraries.CreateAsync();
        t.Catalog.SetHidden(true);
        t.Linking.IsLinkingAllowed.Should().BeFalse();
        await ((Func<Task>)(() => t.Linking.LinkAsync(t.A, t.B))).Should().ThrowAsync<LibraryLinkNotAllowedException>();
        await ((Func<Task>)(() => t.Linking.PreviewLinkAsync(t.A, t.B))).Should().ThrowAsync<LibraryLinkNotAllowedException>();
        await ((Func<Task>)(() => t.Linking.UnlinkAsync(t.A))).Should().ThrowAsync<LibraryLinkNotAllowedException>();
        var hiddenScope = new LibraryScope(t.Fx.ContextFactory, t.Catalog, t.Fx.Messenger);
        await ((Func<Task>)(() => hiddenScope.ShowAllLinkedAsync(1))).Should().ThrowAsync<LibraryLinkNotAllowedException>();
        (await t.Scope.GetForLibraryAsync(t.A)).GroupId.Should().NotBe((await t.Scope.GetForLibraryAsync(t.B)).GroupId);
    }

    [Fact]
    public async Task Neue_Bibliothek_uebernimmt_Begriffe_ohne_Bibliothek_und_Entfernen_raeumt_die_Gruppe_auf()
    {
        using var fx = await TempLibraryFixture.CreateAsync();
        var scope = new LibraryScope(fx.ContextFactory, new TestCatalogContext(), fx.Messenger);
        using var tags = new TagService(fx.ContextFactory, NullLogger<TagService>.Instance, fx.Messenger, scope);
        var early = await tags.CreateAsync("Früh"); // noch keine Bibliothek → Gruppe 0
        var libraries = fx.CreateLibraryService();
        var first = await libraries.CreateAsync("Erste", fx.LibraryRoot);
        (await tags.GetAllAsync()).Should().ContainSingle(x => x.Id == early.Id, "die erste Bibliothek übernimmt Begriffe ohne Bibliothek");
        var second = await libraries.CreateAsync("Zweite", fx.OutsideDir);
        (await tags.GetAllAsync()).Should().BeEmpty("neue Bibliothek: eigene, leere Gruppe");
        await tags.CreateAsync("NurZweite");

        await libraries.DeleteAsync(second.Id);
        using var connection = fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM Tags WHERE Name = 'NurZweite') * 10 + (SELECT COUNT(*) FROM LibraryGroups);";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(1, "Gruppe der entfernten Bibliothek samt Begriffen weg, die der ersten bleibt");
        (await scope.GetCurrentAsync()).LibraryId.Should().Be(first.Id);
        (await tags.GetAllAsync()).Should().ContainSingle(x => x.Name == "Früh");
    }
}

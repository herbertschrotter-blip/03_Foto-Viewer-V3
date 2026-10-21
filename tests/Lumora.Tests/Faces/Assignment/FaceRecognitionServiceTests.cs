using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;
using Lumora.Services.Faces.Recognition;

namespace Lumora.Tests.Faces.Assignment;

/// <summary>
/// Zuordnungsdienst (ADR-0037) gegen eine migrierte Test-DB mit synthetischen Fingerabdrücken: Bestätigen → Training →
/// Vorschläge, Ablehnen → nächstbester, Entfernen → Neu-Lernen, Zusammenführen, Löschen, Ausreißer-Prüfung, Gruppen
/// ohne bestätigte/ignorierte Gesichter, Index-Meldung → neue Vorschläge, Thread-Verhalten.
/// </summary>
public sealed class FaceRecognitionServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Bestaetigen_trainiert_und_schreibt_Vorschlaege_nur_fuer_die_bekannte_Person()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 7);
        var bert = await world.AddFacesAsync("bert", 4);
        var changes = new List<int>();
        world.Fx.Messenger.Register<List<int>, PersonsChangedMessage>(changes, (r, m) => { lock (r) { r.Add(m.LibraryId); } });
        using var service = world.CreateService();
        await service.WaitForIdleAsync().WaitAsync(Wait);
        service.State.PersonsLearned.Should().Be(0);

        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        service.State.PersonsLearned.Should().Be(1);
        service.State.Examples.Should().Be(3);
        foreach (var id in anna.Take(3))
        {
            world.Face(id).Should().Match<FaceRow>(f => f.State == FaceState.Confirmed && f.PersonId == annaId && f.SuggestedPersonId == null);
        }

        foreach (var id in anna.Skip(3))
        {
            var face = world.Face(id);
            face.State.Should().Be(FaceState.Suggested, "Gesicht derselben Person wird vorgeschlagen");
            face.SuggestedPersonId.Should().Be(annaId);
            face.SuggestionScore.Should().BeInRange(0.45f, 1f);
        }

        foreach (var id in bert)
        {
            world.Face(id).Should().Match<FaceRow>(f => f.State == FaceState.Unconfirmed && f.SuggestedPersonId == null, "fremde Person bleibt unbekannt");
        }

        lock (changes)
        {
            changes.Should().Contain(world.LibraryId);
        }

        var faces = await service.GetPersonFacesAsync(annaId);
        faces!.Confirmed.Should().HaveCount(3);
        faces.Suggested.Should().HaveCount(4);
        faces.Suggested.Select(f => f.SuggestionScore!.Value).Should().BeInDescendingOrder();
        var persons = await service.GetPersonOverviewsAsync();
        persons.Should().ContainSingle(p => p.Person.Name == "Anna" && p.Person.FaceCount == 3 && p.Person.FileCount == 3 && p.SuggestionCount == 4);
        persons[0].Person.CoverFaceId.Should().NotBeNull("ohne gesetztes Titelbild das beste bestätigte Gesicht");

        var byFile = await service.GetFacesForFilesAsync([world.Face(anna[0]).FileId, world.Face(anna[5]).FileId, world.Face(bert[0]).FileId], CancellationToken.None);
        byFile[world.Face(anna[0]).FileId].Should().ContainSingle(f => f.PersonId == annaId && f.PersonName == "Anna" && f.State == FaceState.Confirmed);
        byFile[world.Face(anna[5]).FileId].Should().ContainSingle(f => f.SuggestedPersonName == "Anna" && f.PersonId == null && f.State == FaceState.Suggested);
        byFile[world.Face(bert[0]).FileId].Should().ContainSingle(f => f.State == FaceState.Unconfirmed && f.PersonName == null);
        byFile[world.Face(anna[0]).FileId][0].BoxRelative.Width.Should().BeApproximately(0.3f, 1e-6f);
    }

    [Fact]
    public async Task Vorschlag_bestaetigen_ueber_die_P2_Schnittstelle_und_neue_Person_mit_vorhandenem_Namen()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 2);
        using var service = world.CreateService();

        await service.ConfirmNewPersonAsync(anna[0], "  Anna ");
        var annaId = (await service.FindPersonByNameAsync("anna"))!.Value;
        world.Face(anna[0]).PersonId.Should().Be(annaId);

        await service.ConfirmNewPersonAsync(anna[1], "ANNA"); // gibt es schon → dieselbe Person
        world.Face(anna[1]).PersonId.Should().Be(annaId);
        world.Scalar("SELECT COUNT(*) FROM Persons").Should().Be(1);

        await FluentActions.Awaiting(() => service.CreatePersonAsync("anna")).Should().ThrowAsync<PersonNameTakenException>();
        var bertId = await service.CreatePersonAsync("Bert");
        await FluentActions.Awaiting(() => service.RenamePersonAsync(bertId, "Anna")).Should().ThrowAsync<PersonNameTakenException>();
        await service.RenamePersonAsync(bertId, "Berthold");
        (await service.GetPersonsAsync(CancellationToken.None)).Select(p => p.Name).Should().Equal("Anna", "Berthold");
    }

    [Fact]
    public async Task Ablehnen_schlaegt_sofort_den_naechstbesten_vor_und_bleibt_nach_dem_Neu_Lernen()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 3);
        var bert = await world.AddFacesAsync("bert", 3);
        var file = (await world.CreateFilesAsync(1))[0];
        var both = world.InsertFace(file, world.Mix("anna", 0.66f, "bert", 0.6f));
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        var bertId = await service.CreatePersonAsync("Bert");
        await service.ConfirmManyAsync(anna, annaId);
        await service.ConfirmManyAsync(bert, bertId);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        var first = world.Face(both);
        first.State.Should().Be(FaceState.Suggested);
        var second = first.SuggestedPersonId == annaId ? bertId : annaId;

        await service.RejectSuggestionAsync(both);
        var after = world.Face(both);
        after.State.Should().Be(FaceState.Suggested, "der nächstbeste Kandidat rückt sofort nach");
        after.SuggestedPersonId.Should().Be(second);
        world.Scalar("SELECT COUNT(*) FROM FaceRejections WHERE FaceId = $f AND PersonId = $p", ("$f", both), ("$p", first.SuggestedPersonId!.Value)).Should().Be(1);

        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(both).SuggestedPersonId.Should().Be(second, "nach dem Neu-Lernen kommt die abgelehnte Person nicht wieder");

        await service.RejectSuggestionsAsync([both]);
        world.Face(both).Should().Match<FaceRow>(f => f.State == FaceState.Unconfirmed && f.SuggestedPersonId == null, "keiner mehr übrig");
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(both).State.Should().Be(FaceState.Unconfirmed);
    }

    [Fact]
    public async Task Entfernen_lernt_neu_und_nimmt_die_darauf_beruhenden_Vorschlaege_zurueck()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var bert = await world.AddFacesAsync("bert", 6);
        using var service = world.CreateService();
        var wrong = await service.CreatePersonAsync("Falsch benannt");
        await service.ConfirmManyAsync(bert.Take(3).ToList(), wrong); // falsche Gruppe benannt
        await service.WaitForIdleAsync().WaitAsync(Wait);
        bert.Skip(3).Should().OnlyContain(id => world.Face(id).SuggestedPersonId == wrong);

        await service.RemoveFromPersonAsync(bert.Take(3).ToList());
        await service.WaitForIdleAsync().WaitAsync(Wait);

        service.State.Examples.Should().Be(0, "nur aus den verbleibenden Beispielen gelernt");
        bert.Should().OnlyContain(id => world.Face(id).State == FaceState.Unconfirmed && world.Face(id).SuggestedPersonId == null);
        world.Scalar("SELECT COUNT(*) FROM FaceRejections WHERE PersonId = $p", ("$p", wrong)).Should().Be(3, "entfernte Gesichter werden der Person nicht wieder vorgeschlagen");
    }

    [Fact]
    public async Task Zusammenfuehren_uebernimmt_Gesichter_Vorschlaege_und_Ablehnungen()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 8);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        var dupId = await service.CreatePersonAsync("Anna (Handy)");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.ConfirmManyAsync(anna.Skip(3).Take(3).ToList(), dupId);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(anna[6]).State.Should().Be(FaceState.Suggested);
        await service.SetCoverAsync(dupId, anna[3]);
        // Ablehnung der Duplikat-Person auf einem Vorschlag
        if (world.Face(anna[7]).SuggestedPersonId == dupId)
        {
            await service.RejectSuggestionAsync(anna[7]);
        }
        else
        {
            using var connection = world.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO FaceRejections (FaceId, PersonId) VALUES ({anna[7]}, {dupId});";
            cmd.ExecuteNonQuery();
        }

        await service.MergePersonsAsync(dupId, annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        world.Scalar("SELECT COUNT(*) FROM Persons").Should().Be(1);
        anna.Take(6).Should().OnlyContain(id => world.Face(id).PersonId == annaId && world.Face(id).State == FaceState.Confirmed);
        world.Scalar("SELECT COUNT(*) FROM FaceRejections WHERE PersonId = $p AND FaceId = $f", ("$p", annaId), ("$f", anna[7])).Should().Be(1, "„nicht Duplikat“ heißt jetzt „nicht Anna“");
        world.Face(anna[7]).SuggestedPersonId.Should().NotBe(annaId);
        world.Face(anna[6]).SuggestedPersonId.Should().Be(annaId);
        var summary = (await service.GetPersonsAsync(CancellationToken.None)).Single();
        summary.FaceCount.Should().Be(6);
        summary.CoverFaceId.Should().Be(anna[3], "Titelbild der Quelle, weil das Ziel keins hatte");
        await FluentActions.Awaiting(() => service.MergePersonsAsync(annaId, annaId)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Loeschen_gibt_Gesichter_frei_und_entfernt_Ablehnungen_und_Vorschlaege()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 6);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        await service.RejectSuggestionAsync(anna[5]);
        world.Scalar("SELECT COUNT(*) FROM FaceRejections").Should().Be(1);

        await service.DeletePersonAsync(annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        world.Scalar("SELECT COUNT(*) FROM Persons").Should().Be(0);
        world.Scalar("SELECT COUNT(*) FROM FaceRejections").Should().Be(0, "Kaskade");
        anna.Should().OnlyContain(id => world.Face(id).State == FaceState.Unconfirmed && world.Face(id).PersonId == null && world.Face(id).SuggestedPersonId == null);
        (await service.GetGroupsAsync()).Groups.Should().ContainSingle().Which.Count.Should().Be(6, "freigegebene Gesichter sind wieder gruppierbar");
    }

    [Fact]
    public async Task Ausreisser_Pruefung_vor_dem_Bestaetigen()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 7);
        var bert = await world.AddFacesAsync("bert", 1);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna.Take(5).ToList(), annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        var checks = await service.CheckOutliersAsync([anna[5], bert[0]], annaId);
        checks.Should().HaveCount(2);
        checks.Single(c => c.FaceId == anna[5]).IsOutlier.Should().BeFalse();
        var outlier = checks.Single(c => c.FaceId == bert[0]);
        outlier.IsOutlier.Should().BeTrue("ein fremdes Gesicht passt nicht zu Annas Beispielen");
        outlier.Check.Kind.Should().Be(OutlierReason.FarFromExamples);
        outlier.Check.Reason.Should().Contain("Wirklich");

        var forNew = await service.CheckOutliersAsync([anna[6], bert[0]], null);
        forNew.Single(c => c.FaceId == anna[6]).Check.Should().Match<OutlierCheck>(c => c.IsOutlier && c.CloserPersonId == annaId && c.Reason.Contains("Anna"));
        forNew.Single(c => c.FaceId == bert[0]).IsOutlier.Should().BeFalse();
    }

    [Fact]
    public async Task Gruppen_nur_aus_unbekannten_Gesichtern_Ausreisser_am_Ende_und_sofort_aktualisiert()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 4);
        var bert = await world.AddFacesAsync("bert", 8);
        var carl = await world.AddFacesAsync("carl", 5);
        var file = (await world.CreateFilesAsync(1))[0];
        var bertOutlier = world.InsertFace(file, world.Embedding("bert", 0.68f));
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna, annaId);
        await service.IgnoreFacesAsync([carl[0]]);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        var result = await service.GetGroupsAsync();
        result.Groups.Should().HaveCount(2);
        var all = result.Groups.SelectMany(g => g.Members).Select(m => m.FaceId).ToList();
        all.Should().NotIntersectWith(anna, "bestätigte Gesichter gehören nicht in Gruppen");
        all.Should().NotContain(carl[0], "ignorierte Gesichter gehören nicht in Gruppen");
        var bertGroup = result.Groups[0];
        bertGroup.Members.Select(m => m.FaceId).Should().BeEquivalentTo(bert.Append(bertOutlier), "größte Gruppe zuerst");
        bertGroup.Members[^1].FaceId.Should().Be(bertOutlier, "Ausreißer am Ende");
        bertGroup.Members[^1].IsOutlier.Should().BeTrue();
        bertGroup.Members.Take(8).Should().OnlyContain(m => !m.IsOutlier);
        bertGroup.Members.Select(m => m.Similarity).Should().BeInDescendingOrder();
        result.Groups[1].Members.Select(m => m.FaceId).Should().BeEquivalentTo(carl.Skip(1));

        // Benennen nimmt die Gruppe sofort heraus (ohne neu zu gruppieren)
        var bertId = await service.CreatePersonAsync("Bert");
        await service.ConfirmManyAsync(bert, bertId);
        var after = await service.GetGroupsAsync();
        after.Groups.Should().ContainSingle().Which.Members.Select(m => m.FaceId).Should().BeEquivalentTo(carl.Skip(1));

        // Gruppenzentrum → Namensvorschlag einer bekannten Person. Erst warten, bis das Neu-Lernen nach „Bert“ samt
        // Vorschlagsdurchlauf fertig ist — sonst bekommen die neuen Anna-Gesichter unter Last schon einen Vorschlag
        // (State 1) und stehen nicht mehr in einer unbenannten Gruppe.
        await service.WaitForIdleAsync().WaitAsync(Wait);
        var moreAnna = await world.AddFacesAsync("anna", 4);
        var fresh = await service.GetGroupsAsync(refresh: true);
        fresh.Groups.Should().Contain(g => g.SuggestedPersonId == annaId && g.SuggestedPersonName == "Anna"
                                           && g.Members.Select(m => m.FaceId).Intersect(moreAnna).Count() == 4);
    }

    [Fact]
    public async Task Index_Meldung_erzeugt_Vorschlaege_fuer_die_neuen_Gesichter()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 3);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna, annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        var generation = service.State.Generation;

        var file = (await world.CreateFilesAsync(1))[0];
        var neu = world.InsertFace(file, world.Embedding("anna"));
        world.Fx.Messenger.Send(new FacesChangedMessage(world.LibraryId, [file]));
        await service.WaitForIdleAsync().WaitAsync(Wait);

        world.Face(neu).Should().Match<FaceRow>(f => f.State == FaceState.Suggested && f.SuggestedPersonId == annaId);
        service.State.Generation.Should().Be(generation, "nur Vorschläge für die Dateien, kein Neu-Lernen");
    }

    [Fact]
    public async Task Ignorierte_Gesichter_bekommen_keine_Vorschlaege_und_lernen_nicht_mit()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 5);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.SetCoverAsync(annaId, anna[0]);
        await service.IgnoreFaceAsync(anna[0]);
        await service.IgnoreFaceAsync(anna[4]);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        world.Face(anna[0]).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.PersonId == null);
        world.Face(anna[4]).State.Should().Be(FaceState.Ignored);
        service.State.Examples.Should().Be(2);
        world.Face(anna[3]).State.Should().Be(FaceState.Suggested);
        world.Scalar("SELECT COUNT(*) FROM Persons WHERE CoverFaceId IS NOT NULL").Should().Be(0, "Titelbild auf ein ignoriertes Gesicht gelöst");
        await FluentActions.Awaiting(() => service.SetCoverAsync(annaId, anna[3])).Should().ThrowAsync<InvalidOperationException>("nur bestätigte Gesichter");
    }

    [Fact]
    public async Task Training_und_Schreiben_laufen_nie_auf_dem_Aufrufer_Thread()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 4);
        var spy = new SpyRecognizer();
        using var service = world.CreateService(spy);
        var annaId = await service.CreatePersonAsync("Anna");

        // Katalog gesperrt (anderer Schreiber): der Aufruf kehrt sofort zurück und wartet asynchron
        using var blocker = world.OpenConnection();
        using var tx = blocker.BeginTransaction();
        using (var cmd = blocker.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Persons SET IsHidden = 0;";
            cmd.ExecuteNonQuery();
        }

        var started = Environment.TickCount64;
        var confirm = service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        (Environment.TickCount64 - started).Should().BeLessThan(1000, "der Aufrufer (UI-Thread) wartet nicht auf die Schreibsperre");
        await Task.Delay(300);
        confirm.IsCompleted.Should().BeFalse("wartet auf die Sperre");
        tx.Rollback();
        await confirm.WaitAsync(Wait);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        world.Face(anna[0]).State.Should().Be(FaceState.Confirmed);
        spy.Calls.Should().BeGreaterThan(0);
        spy.OnPoolThread.Should().BeTrue("Training läuft auf dem Thread-Pool");
    }

    [Fact]
    public async Task Bestaetigung_gewinnt_gegen_einen_veralteten_Hintergrund_Vorschlag()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 4);
        var bert = await world.AddFacesAsync("bert", 1);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        var bertId = await service.CreatePersonAsync("Bert");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(anna[3]).SuggestedPersonId.Should().Be(annaId);

        // Herbert ordnet das Gesicht bewusst Bert zu; ein späterer Durchgang darf das nicht überschreiben.
        await service.ConfirmManyAsync([anna[3]], bertId);
        service.RequestRetrain();
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(anna[3]).Should().Match<FaceRow>(f => f.State == FaceState.Confirmed && f.PersonId == bertId);
        world.Face(bert[0]).State.Should().Be(FaceState.Unconfirmed);
    }

    private sealed class SpyRecognizer : IFaceRecognizer
    {
        private readonly FaceRecognizer _inner = new();
        private int _calls;
        private int _offPool;

        public int Calls => Volatile.Read(ref _calls);

        public bool OnPoolThread => Volatile.Read(ref _offPool) == 0;

        public IFaceModel Train(IReadOnlyList<FaceSample> confirmed, IReadOnlyCollection<FaceRejection> rejections, FaceRecognizerOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (!Thread.CurrentThread.IsThreadPoolThread)
            {
                Interlocked.Exchange(ref _offPool, 1);
            }

            return _inner.Train(confirmed, rejections, options, cancellationToken);
        }

        public OutlierCheck CheckOutlier(FaceSample face, int personId, IFaceModel model) => _inner.CheckOutlier(face, personId, model);
    }
}

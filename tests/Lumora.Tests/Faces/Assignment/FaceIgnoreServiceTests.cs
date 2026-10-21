using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Faces.Indexing;

namespace Lumora.Tests.Faces.Assignment;

/// <summary>
/// Ignorierte Gesichter (ADR-0040) am echten Zuordnungsdienst: Zurückholen → Vorschläge, ganze Gruppe ignorieren (auch
/// Ausreißer), seitenweises Lesen, Mindestgröße anwenden/zurücknehmen (bestätigte und manuell ignorierte bleiben),
/// einmalige Anwendung des Standards beim Start, Index fügt zu kleine Gesichter gleich ignoriert ein.
/// </summary>
public sealed class FaceIgnoreServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Zurueckholen_macht_ignorierte_wieder_offen_und_berechnet_Vorschlaege()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 6);
        using var service = world.CreateService();
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.IgnoreFacesAsync([anna[4], anna[5]]);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(anna[4]).State.Should().Be(FaceState.Ignored);

        var count = await service.UnignoreFacesAsync([anna[4], anna[3]]); // anna[3] ist nicht ignoriert
        await service.WaitForIdleAsync().WaitAsync(Wait);

        count.Should().Be(1);
        world.Face(anna[4]).Should().Match<FaceRow>(f => f.State == FaceState.Suggested && f.SuggestedPersonId == annaId, "nach dem Zurückholen neu vorgeschlagen");
        world.Face(anna[5]).State.Should().Be(FaceState.Ignored, "nicht ausgewählt");
        var page = await service.GetIgnoredFacesAsync(0, 10);
        page.Total.Should().Be(1);
        page.Faces.Should().ContainSingle().Which.Should().Match<IgnoredFaceInfo>(f => f.FaceId == anna[5] && !f.IsAutomatic && f.Size == 100);
    }

    [Fact]
    public async Task Ganze_Gruppe_ignorieren_nimmt_auch_die_Ausreisser_und_die_Gruppe_verschwindet()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var bert = await world.AddFacesAsync("bert", 8);
        var file = (await world.CreateFilesAsync(1))[0];
        var outlier = world.InsertFace(file, world.Embedding("bert", 0.68f));
        var carl = await world.AddFacesAsync("carl", 5);
        using var service = world.CreateService();
        await service.WaitForIdleAsync().WaitAsync(Wait);
        var groups = await service.GetGroupsAsync();
        var bertGroup = groups.Groups.Single(g => g.Members.Any(m => m.FaceId == bert[0]));
        bertGroup.Members.Should().Contain(m => m.FaceId == outlier && m.IsOutlier);

        var count = await service.IgnoreGroupAsync(bertGroup.GroupId);

        count.Should().Be(9);
        foreach (var id in bert.Append(outlier))
        {
            world.Face(id).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.SuggestionScore == null, "manuell ignoriert, auch der Ausreißer");
        }

        var after = await service.GetGroupsAsync();
        after.Groups.Should().ContainSingle().Which.Members.Select(m => m.FaceId).Should().BeEquivalentTo(carl);
        (await service.IgnoreGroupAsync(bertGroup.GroupId)).Should().Be(0, "die Gruppe gibt es nicht mehr");
        world.Face(carl[0]).State.Should().Be(FaceState.Unconfirmed);
    }

    [Fact]
    public async Task Ganze_Gruppe_ignorieren_laesst_inzwischen_bestaetigte_Gesichter_bestaetigt()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var bert = await world.AddFacesAsync("bert", 6);
        using var service = world.CreateService();
        var group = (await service.GetGroupsAsync()).Groups.Single();
        // Bestätigung an der Gruppe vorbei (z. B. Lightbox) — direkt in der DB, damit der Zwischenspeicher sie nicht kennt
        var personId = await service.CreatePersonAsync("Bert");
        world.Scalar("UPDATE Faces SET State = 2, PersonId = $p WHERE Id = $f", ("$p", personId), ("$f", bert[0]));

        var count = await service.IgnoreGroupAsync(group.GroupId);

        count.Should().Be(5);
        world.Face(bert[0]).Should().Match<FaceRow>(f => f.State == FaceState.Confirmed && f.PersonId == personId);
    }

    [Fact]
    public async Task Mindestgroesse_ignoriert_offene_und_holt_sie_beim_Ausschalten_zurueck_Bestaetigte_und_Manuelle_bleiben()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var anna = await world.AddFacesAsync("anna", 6);
        var bert = await world.AddFacesAsync("bert", 3);
        using var service = world.CreateService();
        await service.SetMinFaceSizeAsync(0); // Ausgangslage ohne Regel
        await service.WaitForIdleAsync().WaitAsync(Wait);
        var annaId = await service.CreatePersonAsync("Anna");
        await service.ConfirmManyAsync(anna.Take(3).ToList(), annaId);
        await service.IgnoreFacesAsync([bert[2]]);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(anna[3]).State.Should().Be(FaceState.Suggested);
        // klein: anna[0] (bestätigt), anna[3] (Vorschlag), bert[0] (offen), bert[2] (manuell ignoriert); groß: der Rest
        foreach (var id in new[] { anna[0], anna[3], bert[0], bert[2] })
        {
            SetSize(world, id, 18);
        }

        SetSize(world, bert[1], 30);

        await service.SetMinFaceSizeAsync(24);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        (await service.GetMinFaceSizeAsync()).Should().Be(24);
        world.Face(anna[0]).Should().Match<FaceRow>(f => f.State == FaceState.Confirmed && f.PersonId == annaId, "bestätigte nie automatisch ignorieren");
        world.Face(anna[3]).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.SuggestedPersonId == null && f.SuggestionScore == FaceAutoIgnore.AutoMarker);
        world.Face(bert[0]).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.SuggestionScore == FaceAutoIgnore.AutoMarker);
        world.Face(bert[1]).State.Should().Be(FaceState.Unconfirmed, "30 px ≥ 24 px");
        world.Face(bert[2]).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.SuggestionScore == null, "manuell ignoriert bleibt manuell");
        var page = await service.GetIgnoredFacesAsync(0, 10);
        page.Total.Should().Be(3);
        page.AutomaticCount.Should().Be(2);
        page.Faces.Where(f => f.IsAutomatic).Select(f => f.FaceId).Should().BeEquivalentTo(new[] { anna[3], bert[0] });
        (await service.GetGroupsAsync(refresh: true)).Groups.SelectMany(g => g.Members).Select(m => m.FaceId)
            .Should().NotContain(bert[0], "automatisch ignorierte gehören nicht in Gruppen");

        // Schwelle senken: 18 px < 20 bleibt ignoriert; ausschalten: alle automatischen zurück, Vorschlag kommt wieder
        await service.SetMinFaceSizeAsync(16);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(bert[0]).State.Should().Be(FaceState.Unconfirmed, "18 px ≥ 16 px → zurückgeholt");
        world.Face(anna[3]).Should().Match<FaceRow>(f => f.State == FaceState.Suggested && f.SuggestedPersonId == annaId, "Vorschlag neu berechnet");

        await service.SetMinFaceSizeAsync(24);
        await service.WaitForIdleAsync().WaitAsync(Wait);
        await service.SetMinFaceSizeAsync(0);
        await service.WaitForIdleAsync().WaitAsync(Wait);

        world.Face(anna[3]).Should().Match<FaceRow>(f => f.State == FaceState.Suggested && f.SuggestedPersonId == annaId);
        world.Face(bert[0]).State.Should().Be(FaceState.Unconfirmed);
        world.Face(bert[2]).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.SuggestionScore == null, "Ausschalten holt manuell ignorierte nicht zurück");
        world.Face(anna[0]).State.Should().Be(FaceState.Confirmed);
        world.Scalar("SELECT Value FROM Settings WHERE Key = $k", ("$k", FaceAutoIgnore.AppliedKey)).Should().Be(0);
    }

    [Fact]
    public async Task Standard_wird_beim_Start_einmal_angewandt_zurueckgeholte_bleiben_beim_naechsten_Start()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var faces = await world.AddFacesAsync("anna", 3);
        SetSize(world, faces[0], 10);
        SetSize(world, faces[1], 10);
        using (var first = world.CreateService())
        {
            await first.WaitForIdleAsync().WaitAsync(Wait);
            (await first.GetMinFaceSizeAsync()).Should().Be(FaceAutoIgnore.DefaultMinSize);
            world.Face(faces[0]).State.Should().Be(FaceState.Ignored, "Standard 24 px beim ersten Start angewandt");
            world.Face(faces[2]).State.Should().Be(FaceState.Unconfirmed);
            await first.UnignoreFacesAsync([faces[0]]);
            await first.WaitForIdleAsync().WaitAsync(Wait);
        }

        using var second = world.CreateService();
        await second.WaitForIdleAsync().WaitAsync(Wait);
        world.Face(faces[0]).State.Should().Be(FaceState.Unconfirmed, "zurückgeholt bleibt zurückgeholt — die Schwelle hat sich nicht geändert");
        world.Face(faces[1]).Should().Match<FaceRow>(f => f.State == FaceState.Ignored && f.SuggestionScore == FaceAutoIgnore.AutoMarker);
    }

    [Fact]
    public async Task Ignorierte_seitenweise_neueste_Fotos_zuerst()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var faces = await world.AddFacesAsync("anna", 7);
        using var service = world.CreateService();
        await service.IgnoreFacesAsync(faces);

        var first = await service.GetIgnoredFacesAsync(0, 3);
        var rest = await service.GetIgnoredFacesAsync(3, 10);

        first.Total.Should().Be(7);
        first.Faces.Should().HaveCount(3);
        rest.Faces.Should().HaveCount(4);
        first.Faces.Concat(rest.Faces).Select(f => f.FileId).Should().BeInDescendingOrder();
        first.Faces.Concat(rest.Faces).Select(f => f.FaceId).Should().BeEquivalentTo(faces);
        (await service.GetIgnoredFacesAsync(0, 0)).Should().Match<IgnoredFacesPage>(p => p.Total == 7 && p.Faces.Count == 0, "nur zählen");
    }

    [Fact]
    public async Task Index_fuegt_zu_kleine_Gesichter_gleich_automatisch_ignoriert_ein()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var files = await world.CreateFilesAsync(1);
        var store = new FaceIndexStore(world.Fx.ContextFactory, NullLogger.Instance);
        FaceDetection Box(float x, float side) => new(x, 10, x + side, 10 + side, 0.9f, [.. Enumerable.Range(0, 5).Select(i => new FacePoint(x + i, 12))]);
        AnalyzedFace Face(float x, float side) => new(Box(x, side), world.Embedding("x"), new FaceQualityScore(0.9f, side, 100, 0.5f));

        await store.WriteAsync([new FaceFileResult(files[0], "fake/v1", new FaceAnalysis(1000, 800, [Face(10, 20), Face(200, 60)]))], CancellationToken.None);

        world.Scalar("SELECT COUNT(*) FROM Faces WHERE Size = 20 AND State = 3 AND SuggestionScore < 0").Should().Be(1, "20 px < Standard 24 px");
        world.Scalar("SELECT COUNT(*) FROM Faces WHERE Size = 60 AND State = 0 AND SuggestionScore IS NULL").Should().Be(1);

        using (var connection = world.OpenConnection())
        using (var tx = connection.BeginTransaction())
        {
            FaceAutoIgnore.WriteSetting(connection, tx, FaceAutoIgnore.MinSizeKey, 0);
            tx.Commit();
        }

        var more = await world.CreateFilesAsync(1);
        await store.WriteAsync([new FaceFileResult(more[0], "fake/v1", new FaceAnalysis(1000, 800, [Face(10, 20)]))], CancellationToken.None);
        world.Scalar("SELECT COUNT(*) FROM Faces WHERE FileId = $f AND State = 0", ("$f", more[0])).Should().Be(1, "Regel aus");
    }

    private static void SetSize(FaceAssignmentWorld world, long faceId, float size)
        => world.Scalar("UPDATE Faces SET Size = $s WHERE Id = $f", ("$s", size), ("$f", faceId));
}

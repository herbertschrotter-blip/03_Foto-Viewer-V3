using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Domain.Entities;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Tests.Faces.Assignment;

/// <summary>
/// Personen-Details und Beziehungen (0.2.47, ADR-0045) im Zuordnungsdienst gegen eine migrierte Test-DB: Anzeigename =
/// Rufname bzw. „Vorname Nachname“ (synchron in Persons.Name, eindeutig), Beziehung als Freitext (ohne Groß-/
/// Kleinschreibung wiederverwendet, Schreibweise der ersten Verwendung), Umbenennen/Zusammenführen/Löschen, „Das bin
/// ich“ eindeutig, Geburtsdatum ungefähr, Zusammenführen zweier Personen übernimmt fehlende Details.
/// </summary>
public sealed class PersonDetailsServiceTests
{
    [Fact]
    public async Task Anlegen_mit_allen_Feldern_setzt_Anzeigenamen_und_Details()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        using var service = world.CreateService(start: false);
        var changes = 0;
        var recipient = new object();
        world.Fx.Messenger.Register<object, PersonsChangedMessage>(recipient, (_, _) => Interlocked.Increment(ref changes));

        var franz = await service.CreatePersonAsync(new PersonDetailsInput(
            " Franz ", "Huber", " Opa Franz ", "Großvater", PersonBirthDate.Create(1931, 5, null), IsMe: false));
        var anna = await service.CreatePersonAsync(new PersonDetailsInput("Anna", "Muster", null, "  ", null));

        var details = (await service.GetPersonDetailsAsync(franz))!;
        details.Should().Match<PersonDetails>(d => d.FirstName == "Franz" && d.LastName == "Huber" && d.Nickname == "Opa Franz"
                                                   && d.RelationName == "Großvater" && d.BirthDate!.Value.Year == 1931
                                                   && d.BirthDate.Value.Month == 5 && d.BirthDate.Value.Day == null);
        details.DisplayName.Should().Be("Opa Franz");
        details.SearchText.Should().Contain("Franz").And.Contain("Huber").And.Contain("Großvater");
        (await service.GetPersonDetailsAsync(anna))!.RelationId.Should().BeNull("leere Beziehung = keine");
        world.Scalar("SELECT COUNT(*) FROM PersonRelations").Should().Be(1);

        var persons = await service.GetPersonsAsync(CancellationToken.None);
        persons.Select(p => p.Name).Should().Equal("Anna Muster", "Opa Franz");
        persons[1].Details!.RelationName.Should().Be("Großvater", "Übersicht liefert die Details mit");
        persons[1].SearchText.Should().Contain("Huber");
        changes.Should().BeGreaterThan(0, "Galerie, Lightbox und Personen-Seite laden neu");

        await FluentActions.Awaiting(() => service.CreatePersonAsync(new PersonDetailsInput("anna", "MUSTER")))
            .Should().ThrowAsync<PersonNameTakenException>("Anzeigename ist ohne Groß-/Kleinschreibung eindeutig");
        await FluentActions.Awaiting(() => service.CreatePersonAsync(new PersonDetailsInput(" ", "", " ")))
            .Should().ThrowAsync<ArgumentException>();
        (await service.FindPersonByNameAsync("opa franz")).Should().Be(franz);
        GC.KeepAlive(recipient);
    }

    [Fact]
    public async Task Beziehung_wird_ohne_Gross_Kleinschreibung_wiederverwendet_und_behaelt_die_erste_Schreibweise()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        using var service = world.CreateService(start: false);

        var anna = await service.CreatePersonAsync(new PersonDetailsInput("Anna", "", Relation: "Schwester"));
        var berta = await service.CreatePersonAsync(new PersonDetailsInput("Berta", "", Relation: "SCHWESTER "));

        world.Scalar("SELECT COUNT(*) FROM PersonRelations").Should().Be(1);
        (await service.GetPersonDetailsAsync(berta))!.RelationName.Should().Be("Schwester");
        (await service.GetRelationsAsync()).Should().ContainSingle().Which.Should().Be(
            new PersonRelationInfo((await service.GetPersonDetailsAsync(anna))!.RelationId!.Value, "Schwester", 2));
    }

    [Fact]
    public async Task Bearbeiten_zieht_den_Anzeigenamen_nach_und_prueft_ihn()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        using var service = world.CreateService(start: false);
        var anna = await service.CreatePersonAsync("Anna Muster");
        var bert = await service.CreatePersonAsync("Bert");

        await service.UpdatePersonDetailsAsync(anna, new PersonDetailsInput("Anna", "Muster", "Annerl", "Cousine", PersonBirthDate.Create(1980, null, null)));

        (await service.GetPersonsAsync(CancellationToken.None)).Select(p => p.Name).Should().Equal("Annerl", "Bert");
        world.Scalar("SELECT COUNT(*) FROM Persons WHERE Name = 'Annerl' AND FirstName = 'Anna' AND LastName = 'Muster' AND BirthYear = 1980 AND BirthMonth IS NULL")
            .Should().Be(1);
        await FluentActions.Awaiting(() => service.UpdatePersonDetailsAsync(bert, new PersonDetailsInput("x", "", "annerl")))
            .Should().ThrowAsync<PersonNameTakenException>();
        await service.UpdatePersonDetailsAsync(anna, new PersonDetailsInput("Anna", "Muster", "ANNERL", "Cousine")); // sich selbst: erlaubt

        // Alte Schnittstelle (Einzelname): teilt am ersten Leerzeichen, Rufname entfällt
        await service.RenamePersonAsync(anna, "Anna Maria Muster");
        var renamed = (await service.GetPersonDetailsAsync(anna))!;
        (renamed.FirstName, renamed.LastName, renamed.Nickname, renamed.DisplayName).Should().Be(("Anna", "Maria Muster", null, "Anna Maria Muster"));
        renamed.RelationName.Should().Be("Cousine", "Umbenennen lässt die übrigen Details stehen");

        // Mehrfache Leerzeichen: gespeicherter Name = geprüfter Name = Anzeigename
        await FluentActions.Awaiting(() => service.CreatePersonAsync("Anna   Maria  Muster")).Should().ThrowAsync<PersonNameTakenException>();
        await service.RenamePersonAsync(bert, " Bert   Huber ");
        var bertDetails = (await service.GetPersonDetailsAsync(bert))!;
        (bertDetails.FirstName, bertDetails.LastName, bertDetails.DisplayName).Should().Be(("Bert", "Huber", "Bert Huber"));
        world.Scalar("SELECT COUNT(*) FROM Persons WHERE Name = 'Bert Huber'").Should().Be(1);
    }

    [Fact]
    public async Task Das_bin_ich_ist_hoechstens_eine_Person()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        using var service = world.CreateService(start: false);
        var herbert = await service.CreatePersonAsync(new PersonDetailsInput("Herbert", "S", IsMe: true));
        var anna = await service.CreatePersonAsync(new PersonDetailsInput("Anna", "", IsMe: true));

        world.Scalar("SELECT COUNT(*) FROM Persons WHERE IsMe = 1").Should().Be(1);
        (await service.GetPersonDetailsAsync(anna))!.IsMe.Should().BeTrue("die neue Markierung gewinnt");
        await service.UpdatePersonDetailsAsync(herbert, new PersonDetailsInput("Herbert", "S", IsMe: true));
        (await service.GetAllPersonDetailsAsync()).Where(d => d.IsMe).Select(d => d.Id).Should().Equal(herbert);

        using var connection = world.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE Persons SET IsMe = 1 WHERE Id = {anna};";
        FluentActions.Invoking(() => cmd.ExecuteNonQuery()).Should().Throw<Microsoft.Data.Sqlite.SqliteException>("eindeutiger Teilindex IX_Persons_IsMe");
    }

    [Fact]
    public async Task Beziehungen_umbenennen_zusammenfuehren_loeschen()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        using var service = world.CreateService(start: false);
        var anna = await service.CreatePersonAsync(new PersonDetailsInput("Anna", "", Relation: "Schwster"));
        var berta = await service.CreatePersonAsync(new PersonDetailsInput("Berta", "", Relation: "Schwester"));
        var carl = await service.CreatePersonAsync(new PersonDetailsInput("Carl", "", Relation: "Taufpate"));
        int Rel(string name) => (int)world.Scalar("SELECT Id FROM PersonRelations WHERE Name = $n", ("$n", name));

        var sister = Rel("Schwester");
        await FluentActions.Awaiting(() => service.RenameRelationAsync(Rel("Schwster"), "schwester"))
            .Should().ThrowAsync<RelationNameTakenException>().Where(e => e.ExistingRelationId == sister);
        await service.MergeRelationsAsync(Rel("Schwster"), Rel("Schwester"));
        (await service.GetPersonDetailsAsync(anna))!.RelationName.Should().Be("Schwester");
        (await service.GetRelationsAsync()).Select(r => (r.Name, r.PersonCount)).Should().Equal(("Schwester", 2), ("Taufpate", 1));

        await service.RenameRelationAsync(Rel("Taufpate"), "Patenonkel");
        (await service.GetPersonDetailsAsync(carl))!.RelationName.Should().Be("Patenonkel", "Umbenennen wirkt bei allen Personen");
        (await service.GetPersonsAsync(CancellationToken.None)).Single(p => p.Id == carl).SearchText.Should().Contain("Patenonkel");

        await service.DeleteRelationAsync(Rel("Schwester"));
        (await service.GetPersonDetailsAsync(berta))!.RelationId.Should().BeNull("Personen behalten eine leere Beziehung");
        (await service.GetPersonDetailsAsync(berta))!.FirstName.Should().Be("Berta");
        world.Scalar("SELECT COUNT(*) FROM Persons").Should().Be(3);
    }

    [Fact]
    public async Task Personen_zusammenfuehren_uebernimmt_fehlende_Details_und_die_Ich_Markierung()
    {
        using var world = await FaceAssignmentWorld.CreateAsync();
        var faces = await world.AddFacesAsync("anna", 2);
        using var service = world.CreateService();
        var source = await service.CreatePersonAsync(new PersonDetailsInput("Anni", "", Relation: "Schwester", BirthDate: PersonBirthDate.Create(1975, 3, 9), IsMe: true));
        var target = await service.CreatePersonAsync(new PersonDetailsInput("Anna", "Muster"));
        await service.ConfirmManyAsync([faces[0]], source);
        await service.ConfirmManyAsync([faces[1]], target);

        await service.MergePersonsAsync(source, target);

        var merged = (await service.GetPersonDetailsAsync(target))!;
        merged.DisplayName.Should().Be("Anna Muster", "das Ziel behält seine Namen");
        merged.RelationName.Should().Be("Schwester");
        merged.BirthDate!.Value.Format().Should().Be("09.03.1975");
        merged.IsMe.Should().BeTrue();
        world.Face(faces[0]).PersonId.Should().Be(target);
        (await service.GetPersonDetailsAsync(source)).Should().BeNull();
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Gallery.Data;
using Lumora.Gallery.ViewModels;
using Lumora.Persons;
using Lumora.Persons.ViewModels;
using Lumora.Services.Faces;
using Lumora.Services.Faces.Assignment;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Personen-Details und Beziehungen“ (0.2.47, ADR-0045) vor „Ordnerüberwachung“. Die Gesichtsdaten
/// hat „Personen in Galerie/Lightbox“ gelöscht — der Schritt schreibt drei unbenannte Gesichter (eins je Foto, zufällige
/// Fingerabdrücke) und benennt sie über die Gruppenansicht der Personen-Seite mit dem Personen-Dialog (Rauchtest-Fake):
/// Vor-/Nachname + Beziehung „Schwester“ + Geburtsjahr/-monat, beim zweiten Mal „schwester“ getippt → Vorschlag und
/// dieselbe Beziehung, dritte Person „Das bin ich“. Danach: Übersicht nach Beziehung gruppiert, Detailzeile, Filter
/// „Beziehung“ in der Galerie (Treffer, „Es gilt“), Suche nach Nachname/Beziehung, Umbenennen der Beziehung in den
/// Einstellungen wirkt in Galerie und Personen-Seite, „Das bin ich“ bleibt eindeutig.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task PersonDetailsAsync()
    {
        Check(_personsPage is not null && _recognition is IPersonDetailsService && _personsDialogs is not null, "Personen-Seite, Details-Dienst und Rauchtest-Dialoge vorhanden");
        var page = _personsPage!;
        var recognition = _recognition!;
        var details = (IPersonDetailsService)recognition;
        var dialogs = _personsDialogs!;
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        try
        {
            await PersonDetailsCoreAsync(page, recognition, details, dialogs).ConfigureAwait(true);
        }
        finally
        {
            page.GroupByRelation = false;
            page.PersonSearch = null;
            dialogs.NextInput = null;
            if (_gallery.HasRelationFilter || _gallery.HasPersonFilter)
            {
                await _gallery.ClearPeopleFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
                await WaitForGalleryAsync("Personen-/Beziehungs-Filter nach dem Schritt zurückgesetzt").ConfigureAwait(true);
            }
        }
    }

    private async Task PersonDetailsCoreAsync(PersonsViewModel page, IFaceRecognitionService recognition, IPersonDetailsService details, SmokePersonsDialogs dialogs)
    {
        // (0) Drei unbenannte Gesichter auf drei Fotos
        var fileRows = await DbRowsAsync(
            "SELECT Id FROM Files WHERE LibraryId = $lib AND MediaType = 0 AND Missing = 0 ORDER BY Id LIMIT 3", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(fileRows.Count == 3, $"{fileRows.Count} Fotos für die Gesichter");
        var random = new Random(4747);
        var faces = new (long FaceId, int FileId)[3];
        for (var i = 0; i < 3; i++)
        {
            var fileId = int.Parse(fileRows[i], System.Globalization.CultureInfo.InvariantCulture);
            faces[i].FileId = fileId;
            faces[i].FaceId = await DbScalarAsync<long>("""
                INSERT INTO Faces (FileId, BoxX, BoxY, BoxW, BoxH, Landmarks, DetScore, Size, Sharpness, Quality, Embedding,
                                   ModelVersion, PersonId, State, SuggestedPersonId, SuggestionScore, CreatedAtUtc)
                VALUES ($f, 0.3, 0.2, 0.3, 0.4, $lm, 0.9, 200, 50, 0.9, $e, 'smoke/details', NULL, 0, NULL, NULL, 1);
                SELECT last_insert_rowid();
                """, ("$f", fileId), ("$lm", new byte[40]),
                ("$e", FaceEmbeddingCodec.Encode(RandomUnit(random)))).ConfigureAwait(true);
        }

        recognition.InvalidateAll();
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Persons);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Persons, ShortWait, "Personen-Seite").ConfigureAwait(true);
        await page.LoadOverviewAsync().ConfigureAwait(true);

        // (1) Benennen mit Vor-/Nachname, Beziehung und ungefährem Geburtsdatum
        await NameSingleFaceAsync(page, dialogs, faces[0], new PersonDetailsInput("Lena", "Smoketest", null, "Schwester", PersonBirthDate.Create(1975, 3, null))).ConfigureAwait(true);
        var lena = await recognition.FindPersonByNameAsync("Lena Smoketest").ConfigureAwait(true);
        Check(lena is not null, $"Person „Lena Smoketest“ angelegt ({page.Message})");
        var lenaRow = await DbRowsAsync("SELECT FirstName, LastName, BirthYear, BirthMonth, (SELECT Name FROM PersonRelations r WHERE r.Id = p.RelationId) FROM Persons p WHERE Id = $p", ("$p", lena!.Value)).ConfigureAwait(true);
        Check(lenaRow.SingleOrDefault() == "Lena | Smoketest | 1975 | 3 | Schwester", $"DB: {lenaRow.SingleOrDefault()}");

        // (2) Zweite Person: „schwester“ getippt → Vorschlag „Schwester“, keine zweite Beziehung
        await NameSingleFaceAsync(page, dialogs, faces[1], new PersonDetailsInput("Mia", "Smoketest", null, "schwester")).ConfigureAwait(true);
        Check(dialogs.LastEditRequest?.Relations.Contains("Schwester") == true, $"Vorschlagsliste beim zweiten Mal: {string.Join(", ", dialogs.LastEditRequest?.Relations ?? [])}");
        var mia = await recognition.FindPersonByNameAsync("Mia Smoketest").ConfigureAwait(true);
        var relations = await details.GetRelationsAsync().ConfigureAwait(true);
        Check(mia is not null && relations.Count(r => string.Equals(r.Name, "schwester", StringComparison.OrdinalIgnoreCase)) == 1
              && relations.Single(r => r.Name == "Schwester").PersonCount == 2,
            $"eine Beziehung „Schwester“ mit 2 Personen ({string.Join(", ", relations.Select(r => $"{r.Name} {r.PersonCount}"))})");

        // (3) Dritte Person mit Rufname und „Das bin ich“
        await NameSingleFaceAsync(page, dialogs, faces[2], new PersonDetailsInput("Herbert", "Smoketest", "Smoke-Ich", null, PersonBirthDate.Create(1970, null, null), IsMe: true)).ConfigureAwait(true);
        var me = await recognition.FindPersonByNameAsync("Smoke-Ich").ConfigureAwait(true);
        Check(me is not null, "Person mit Rufname „Smoke-Ich“ angelegt (Anzeigename = Rufname)");

        // (4) Übersicht nach Beziehung gruppiert, Detailzeile
        await page.LoadOverviewAsync().ConfigureAwait(true);
        page.GroupByRelation = true;
        var headers = page.RelationGroups.Select(g => g.Header).ToList();
        Check(headers.SequenceEqual(["Schwester · 2", "Ohne Beziehung · 1"]), $"gruppiert: {string.Join(" / ", headers)}");
        page.PersonSearch = "smoketest";
        Check(page.Persons.Count == 3, $"Suche „smoketest“ (Nachname hinter dem Rufnamen): {page.Persons.Count}");
        page.PersonSearch = null;
        await WaitUntilAsync(() => page.Persons.Where(p => p.PersonId == lena.Value || p.PersonId == mia!.Value).All(p => p.Image is not null), ShortWait, "Titelbilder der Schwestern").ConfigureAwait(true);
        await SnapshotAsync("35-personen-nach-beziehung").ConfigureAwait(true);
        page.GroupByRelation = false;

        await page.LoadPersonAsync(lena.Value, keepState: false).ConfigureAwait(true);
        Check(page.PersonInfoText?.StartsWith("Schwester · geboren März 1975 (", StringComparison.Ordinal) == true, $"Detailzeile: {page.PersonInfoText}");
        await SnapshotAsync("36-personen-detail-beziehung").ConfigureAwait(true);

        // (5) „Das bin ich“ eindeutig: Mia als ich markieren (Bearbeiten) → Smoke-Ich verliert die Markierung
        await page.LoadPersonAsync(mia!.Value, keepState: false).ConfigureAwait(true);
        dialogs.NextInput = new PersonDetailsInput("Mia", "Smoketest", null, "Schwester", null, IsMe: true);
        await page.EditPersonCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(dialogs.LastEditRequest?.CurrentMeName == "Smoke-Ich", $"Dialog nennt die bisherige „Ich“-Person: {dialogs.LastEditRequest?.CurrentMeName}");
        var meIds = await DbRowsAsync("SELECT Id FROM Persons WHERE IsMe = 1").ConfigureAwait(true);
        Check(meIds.SequenceEqual([mia.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]), $"genau eine „Ich“-Person: {string.Join(", ", meIds)}");
        await page.BackCommand.ExecuteAsync(null).ConfigureAwait(true);

        // (6) Galerie: Filter „Beziehung“, „Es gilt“, Suche nach Beziehung
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "Galerie aktiv").ConfigureAwait(true);
        if (_gallery.HasFolderFilter)
        {
            await _gallery.ClearFolderFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        if (_gallery.FilterState.IsActive)
        {
            _gallery.FilterState = GalleryFilterState.None;
        }

        await WaitForGalleryAsync("Galerie ohne Filter").ConfigureAwait(true);
        _messenger.Send(new PersonsChangedMessage(_libraryId));
        await WaitUntilAsync(
            () => _gallery.RelationFilterEntries.Any(e => e.Name == "Schwester" && e.FileCount == 2) && _gallery.ShowPersonFilter,
            ShortWait,
            "Abschnitt „Beziehung“ mit „Schwester (2)“").ConfigureAwait(true);
        _gallery.RelationFilterEntries.First(e => e.Name == "Schwester").IsSelected = true;
        await _gallery.ApplyRelationFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Filter Beziehung Schwester").ConfigureAwait(true);
        var labels = string.Join(" UND ", _gallery.ActiveConditions.Select(c => c.Label));
        Check(_gallery.TotalCount == 2 && _gallery.HasPeopleFilter && labels == "Beziehung „Schwester“",
            $"Filter „Schwester“: {_gallery.TotalCount} Treffer (erwartet 2), Es gilt: {labels}, Chip „{_gallery.PersonFilterValueText}“");
        await SnapshotAsync("37-galerie-filter-beziehung").ConfigureAwait(true);

        _gallery.PersonFilterEntries.First(e => e.PersonId == mia.Value).IsSelected = true;
        await _gallery.ApplyPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Person Mia UND Beziehung Schwester").ConfigureAwait(true);
        Check(_gallery.TotalCount == 1 && _gallery.ActiveConditions.Count == 2, $"Mia UND Schwester: {_gallery.TotalCount} (erwartet 1), {_gallery.ActiveConditions.Count} Bedingungen");
        await _gallery.ClearPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true); // × an „Person …“: Beziehung bleibt
        await WaitForGalleryAsync("Personen-Teil entfernt").ConfigureAwait(true);
        Check(_gallery.HasRelationFilter && _gallery.TotalCount == 2, $"nach × an der Person bleibt die Beziehung: {_gallery.TotalCount}");

        // (7) Einstellungen: Beziehung umbenennen → wirkt in Galerie (Abschnitt, „Es gilt“) und Personen-Seite
        await OpenSettingsSectionAsync(NavigationTags.SettingsFaces, "Einstellungen › Gesichter").ConfigureAwait(true);
        var settings = _settings.Relations;
        Check(settings is not null, "Karte „Personen & Beziehungen“ vorhanden");
        await settings!.LoadAsync().ConfigureAwait(true);
        var row = settings.Relations.FirstOrDefault(r => r.Name == "Schwester");
        Check(row is { PersonCount: 2 }, $"Einstellungen: {string.Join(", ", settings.Relations.Select(r => $"{r.Name} ({r.CountText})"))}");
        settings.SelectedRelation = row;
        await settings.ApplyRenameAsync(row!, "Smokeschwester").ConfigureAwait(true);
        Check(settings.Message?.Contains("Smokeschwester", StringComparison.Ordinal) == true && !settings.MessageIsError, $"Meldung: {settings.Message}");
        await WaitUntilAsync(() => settings.Relations.Any(r => r.Name == "Smokeschwester"), ShortWait, "Liste in den Einstellungen umbenannt").ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        var card = FindByAutomationName(System.Windows.Application.Current.MainWindow, "Personen und Beziehungen", typeof(System.Windows.Controls.Border));
        Check(card is not null, "Karte „Personen & Beziehungen“ sichtbar");
        card!.BringIntoView();
        await Task.Delay(300).ConfigureAwait(true);
        await SnapshotAsync("38-einstellungen-beziehungen").ConfigureAwait(true);

        await WaitUntilAsync(
            () => _gallery.RelationFilterEntries.Any(e => e.Name == "Smokeschwester" && e.IsSelected)
                  && _gallery.ActiveConditions.Any(c => c.Label == "Beziehung „Smokeschwester“"),
            ShortWait,
            "Galerie: umbenannte Beziehung im Abschnitt und unter „Es gilt“ (Auswahl bleibt)").ConfigureAwait(true);
        await WaitForGalleryAsync("Galerie nach dem Umbenennen").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2, $"Filter wirkt weiter: {_gallery.TotalCount}");
        await _gallery.ClearRelationFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Beziehungs-Filter entfernt").ConfigureAwait(true);

        _gallery.SearchTerm = "smokeschwester";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nach Beziehung").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null && _gallery.TotalCount == 2, $"Suche „smokeschwester“ (Beziehung): {_gallery.TotalCount} (erwartet 2)");
        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);

        await page.LoadOverviewAsync().ConfigureAwait(true);
        var tile = page.Persons.FirstOrDefault(p => p.PersonId == lena.Value);
        Check(tile?.Details.StartsWith("Smokeschwester · ", StringComparison.Ordinal) == true, $"Personen-Seite: Kachel „{tile?.Details}“");
        _logger.LogInformation("Rauchtest   Personen-Details: 3 Personen, Beziehungen {Relations}", string.Join(", ", (await details.GetRelationsAsync().ConfigureAwait(true)).Select(r => r.Name)));

        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "zurück in der Galerie").ConfigureAwait(true);
    }

    /// <summary>Öffnet eine Ein-Gesicht-Gruppe in der Gruppenansicht und benennt sie mit der vorgegebenen Dialog-Antwort.</summary>
    private static async Task NameSingleFaceAsync(PersonsViewModel page, SmokePersonsDialogs dialogs, (long FaceId, int FileId) face, PersonDetailsInput input)
    {
        page.ShowGroup(new FaceGroupInfo(-face.FaceId, [new FaceGroupMemberInfo(face.FaceId, face.FileId, 1f, false)], 1f, null, null, null));
        dialogs.NextInput = input;
        dialogs.OutlierDecision = OutlierDecision.ConfirmAll;
        await page.NameGroupCommand.ExecuteAsync(null).ConfigureAwait(true);
    }
}

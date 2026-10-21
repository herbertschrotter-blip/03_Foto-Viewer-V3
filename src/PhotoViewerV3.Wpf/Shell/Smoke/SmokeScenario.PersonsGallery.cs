using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ListView;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Lightbox.Controls;
using PhotoViewerV3.Lightbox.Faces;
using PhotoViewerV3.Services.Faces.Assignment;
using PhotoViewerV3.Services.Faces.Indexing;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Personen in Galerie/Lightbox“ (0.2.38) — direkt nach „Gesichter“: der Smoke-Fake-Analysator hat
/// jedem Foto ein Gesicht gegeben (unbestätigt). Bestätigte Personen und ein Vorschlag werden hier per SQL vorgeschrieben
/// (reproduzierbar, unabhängig vom Lernen); die Lightbox bestätigt über den registrierten
/// <see cref="IFaceRecognitionService"/> (Zuordnungsdienst, ADR-0037).
/// Geprüft: Personen-Filter (ODER, Vorschläge, ohne Person, unbenannte
/// Gesichter), Suche nach Personennamen, Listenspalte, Kachel-Daten, Lightbox-Rahmen (Taste F) mit „Ist X“ und
/// „Gesichtsdaten löschen“ in den Einstellungen.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task PersonsGalleryAsync()
    {
        // Der Zuordnungsdienst (ADR-0037) rechnet nach dem Schritt „Personen“ noch Vorschläge im Hintergrund nach und
        // würde die hier per SQL vorgeschriebenen Zuordnungen überschreiben → erst warten, bis er ruht.
        if (_recognition is not null)
        {
            await _recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        }

        try
        {
            await PersonsGalleryCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            // Auch nach einem Fehlschlag keinen Personen-Filter stehen lassen — sonst scheitern die folgenden Schritte mit.
            if (_gallery.HasPersonFilter || _gallery.PersonFilterIncludeSuggestions)
            {
                _gallery.PersonFilterIncludeSuggestions = false;
                await _gallery.ClearPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
                await WaitForGalleryAsync("Personen-Filter nach dem Schritt zurückgesetzt").ConfigureAwait(true);
            }
        }
    }

    private async Task PersonsGalleryCoreAsync()
    {
        var faceRows = await DbRowsAsync(
            "SELECT fa.Id, fa.FileId FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE f.LibraryId = $lib AND f.MediaType = 0 ORDER BY f.Id LIMIT 3",
            ("$lib", _libraryId)).ConfigureAwait(true);
        Check(faceRows.Count == 3, $"{faceRows.Count} Fotos mit Gesicht aus dem Schritt „Gesichter“");
        var faces = faceRows.Select(r => r.Split(" | ")).Select(c => (FaceId: long.Parse(c[0]), FileId: int.Parse(c[1]))).ToList();

        // Personen und Zuordnungen vorschreiben (ohne P1-Dienst): Anna auf 2 Fotos bestätigt, Bert auf dem dritten vorgeschlagen.
        var anna = await DbScalarAsync<int>("INSERT INTO Persons (Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES ('Smokeperson Anna', 1, NULL, 0); SELECT last_insert_rowid();").ConfigureAwait(true);
        var bert = await DbScalarAsync<int>("INSERT INTO Persons (Name, CreatedAtUtc, CoverFaceId, IsHidden) VALUES ('Smokeperson Bert', 1, NULL, 0); SELECT last_insert_rowid();").ConfigureAwait(true);
        var updated = await DbScalarAsync<int>(
            "UPDATE Faces SET State = 2, PersonId = $p WHERE Id IN ($a, $b); SELECT changes();",
            ("$p", anna), ("$a", faces[0].FaceId), ("$b", faces[1].FaceId)).ConfigureAwait(true);
        updated += await DbScalarAsync<int>(
            "UPDATE Faces SET State = 1, SuggestedPersonId = $p, SuggestionScore = 0.7 WHERE Id = $c; SELECT changes();",
            ("$p", bert), ("$c", faces[2].FaceId)).ConfigureAwait(true);
        Check(updated == 3, $"{updated} Gesichter zugeordnet");

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
        var baseline = _gallery.TotalCount;
        _messenger.Send(new PersonsChangedMessage(_libraryId)); // wie nach einer Bestätigung durch den Zuordnungsdienst
        await WaitUntilAsync(
            () => _gallery.ShowPersonFilter && _gallery.PersonFilterEntries.Any(e => e.PersonId == anna && e.FileCount == 2),
            ShortWait,
            "Personen-Filter mit „Smokeperson Anna (2)“").ConfigureAwait(true);
        var annaItem = _gallery.CurrentPage.FirstOrDefault(i => i.Id == faces[0].FileId);
        var bertItem = _gallery.CurrentPage.FirstOrDefault(i => i.Id == faces[2].FileId);
        Check(annaItem is not null && bertItem is not null, "Fotos mit Personen auf der ersten Seite");
        await WaitUntilAsync(() => annaItem!.Persons.HasConfirmed && bertItem!.Persons.HasSuggestions, ShortWait, "Personen der Kacheln geladen").ConfigureAwait(true);
        Check(annaItem!.Persons.ConfirmedText == "Smokeperson Anna" && annaItem.Persons.ConfirmedCount == 1, $"Kachel: Personen-Symbol 1, Tooltip „{annaItem.Persons.ToolTip}“");
        Check(!bertItem!.Persons.HasConfirmed && bertItem.Persons.SuggestionsText == "Smokeperson Bert?", $"Vorschlag: „{bertItem.Persons.SuggestionsText}“, kein Kachel-Symbol");
        await CheckUnnamedFaceMarksAsync(annaItem, bertItem).ConfigureAwait(true); // 0.2.39

        // Filter: ODER, mit Vorschlägen, ohne Person, unbenannte Gesichter
        _gallery.PersonFilterEntries.First(e => e.PersonId == anna).IsSelected = true;
        await _gallery.ApplyPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Personen-Filter Anna").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2 && _gallery.HasPersonFilter, $"Personen-Filter „Anna“: {_gallery.TotalCount} Treffer (erwartet 2), Knopf „{_gallery.PersonFilterText}“");

        _gallery.PersonFilterEntries.First(e => e.PersonId == bert).IsSelected = true;
        await _gallery.ApplyPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Personen-Filter Anna ODER Bert").ConfigureAwait(true);
        Check(_gallery.TotalCount == 2, $"„Anna ODER Bert“ ohne Vorschläge: {_gallery.TotalCount} (erwartet 2 — Bert ist nur vorgeschlagen)");
        _gallery.PersonFilterIncludeSuggestions = true;
        await WaitForGalleryAsync("Personen-Filter mit Vorschlägen").ConfigureAwait(true);
        Check(_gallery.TotalCount == 3, $"„Anna ODER Bert“ + Vorschläge: {_gallery.TotalCount} (erwartet 3), „{_gallery.PersonFilterText}“");
        await SnapshotAsync("22-galerie-personen-filter").ConfigureAwait(true);

        await _gallery.ClearPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Personen-Filter zurückgesetzt").ConfigureAwait(true);
        Check(!_gallery.HasPersonFilter && !_gallery.PersonFilterIncludeSuggestions && _gallery.TotalCount == baseline, "Personen-Filter zurückgesetzt");

        // Erwartung aus dem DB-Stand: auch der Schritt „Personen“ davor hat Fotos bestätigt (nicht nur die zwei von Anna).
        var photosWithoutPerson = await DbScalarAsync<int>(
            "SELECT COUNT(*) FROM Files f WHERE f.LibraryId = $lib AND f.MediaType = 0 AND NOT EXISTS (SELECT 1 FROM Faces fa WHERE fa.FileId = f.Id AND fa.State = 2 AND fa.PersonId IS NOT NULL)",
            ("$lib", _libraryId)).ConfigureAwait(true);
        _gallery.PersonFilterMode = GalleryPersonMode.NoPerson;
        await WaitForGalleryAsync("ohne Person").ConfigureAwait(true);
        Check(_gallery.TotalCount == photosWithoutPerson, $"„nur Fotos ohne Person“: {_gallery.TotalCount} (erwartet {photosWithoutPerson}; Videos/Archive zählen nicht)");

        var unnamedExpected = await DbScalarAsync<int>(
            "SELECT COUNT(DISTINCT fa.FileId) FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE f.LibraryId = $lib AND fa.State IN (0, 1)",
            ("$lib", _libraryId)).ConfigureAwait(true);
        _gallery.PersonFilterMode = GalleryPersonMode.UnnamedFaces;
        await WaitForGalleryAsync("unbenannte Gesichter").ConfigureAwait(true);
        Check(_gallery.TotalCount == unnamedExpected && unnamedExpected > 0, $"„unbenannte Gesichter“: {_gallery.TotalCount} (erwartet {unnamedExpected})");
        await CheckAnyFaceFilterAsync().ConfigureAwait(true); // 0.2.39
        await _gallery.ClearPersonFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Personen-Filter zurückgesetzt").ConfigureAwait(true);

        // Suche nach Personennamen (kein Dateiname enthält „Smokeperson“)
        _gallery.SearchTerm = "smokeperson anna";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nach Personennamen").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null && _gallery.TotalCount == 2, $"Suche „smokeperson anna“: {_gallery.TotalCount} (erwartet 2)");
        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);

        // Listenspalte „Personen“
        var columnsBefore = _gallery.ListColumnSettings;
        var modeBefore = _gallery.ViewMode;
        _gallery.ListColumnSettings = "Name,Persons,Tags,Modified"; // Personen gleich neben dem Namen (Screenshot)
        _gallery.ViewMode = GalleryViewMode.List;
        await Task.Delay(400).ConfigureAwait(true);
        var listView = FindVisualChild<GalleryListView>(Application.Current.MainWindow);
        Check(listView is not null && listView.VisibleColumnKeysInOrder().Contains(GalleryListColumnKey.Persons), "Listenansicht mit Spalte „Personen“");
        await SnapshotAsync("23-galerie-liste-personen").ConfigureAwait(true);
        _gallery.ViewMode = modeBefore;
        _gallery.ListColumnSettings = columnsBefore;
        await Task.Delay(300).ConfigureAwait(true);

        // Lightbox: Rahmen (Taste F) und „Ist Bert“
        bertItem = _gallery.CurrentPage.First(i => i.Id == faces[2].FileId);
        _gallery.OpenInLightboxCommand.Execute(bertItem);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var lightbox = window.ViewModel;
        await WaitUntilAsync(() => lightbox.CurrentItem?.FileId == bertItem.Id && lightbox.ImageSource is not null, ShortWait, "Foto in der Lightbox").ConfigureAwait(true);
        Check(lightbox.Faces.IsAvailable, "Zuordnungsdienst registriert");
        await lightbox.ToggleFacesCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => lightbox.Faces.Boxes.Count == 1, ShortWait, "Gesichtsrahmen geladen").ConfigureAwait(true);
        var box = lightbox.Faces.Boxes[0];
        Check(box.Kind == LightboxFaceKind.Suggested && box.Label == "Vorschlag: Smokeperson Bert?", $"Rahmen: „{box.Label}“");
        var layer = window.FindName("FaceBoxes") as FaceBoxesLayer;
        await WaitUntilAsync(() => layer is { FrameCount: 1, IsVisible: true }, ShortWait, "Rahmen gezeichnet").ConfigureAwait(true);
        await SnapshotAsync("24-lightbox-gesichter", window).ConfigureAwait(true);

        var menu = await lightbox.Faces.BuildMenuAsync(box).ConfigureAwait(true);
        Check(menu.Select(m => m.Header).Contains("Nicht Smokeperson Bert") && menu.Any(m => m.Header == "Kein Gesicht"), $"Menü: {string.Join(" / ", menu.Where(m => !m.IsSeparator).Select(m => m.Header))}");
        await menu.First(m => m.Header == "Ist Smokeperson Bert").Execute!().ConfigureAwait(true);
        await WaitUntilAsync(() => lightbox.Faces.Boxes is [{ Kind: LightboxFaceKind.Confirmed, Label: "Smokeperson Bert" }], ShortWait, "Rahmen nach „Ist Bert“").ConfigureAwait(true);
        var confirmed = await DbScalarAsync<int>("SELECT COUNT(*) FROM Faces WHERE Id = $id AND State = 2 AND PersonId = $p", ("$id", faces[2].FaceId), ("$p", bert)).ConfigureAwait(true);
        Check(confirmed == 1, "DB: Gesicht als Bert bestätigt");
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
        await WaitUntilAsync(
            () => _gallery.PersonFilterEntries.Any(e => e.PersonId == bert && e.FileCount == 1) && bertItem.Persons.HasConfirmed,
            ShortWait,
            "Galerie: Bert bestätigt (Filter-Zähler, Kachel)").ConfigureAwait(true);
        await CheckLightboxFacesRememberedAsync(bertItem).ConfigureAwait(true); // 0.2.39

        // Einstellungen: „Gesichtsdaten löschen“ (Bestätigung ist ein echter Dialog — hier direkt nach der Bestätigung)
        await OpenSettingsSectionAsync(NavigationTags.SettingsFaces, "Einstellungen").ConfigureAwait(true);
        Check(_settings.FaceData is not null, "Abschnitt „Gesichtsdaten löschen“ vorhanden");
        await Task.Delay(300).ConfigureAwait(true);
        var eraseButton = FindByAutomationName(Application.Current.MainWindow, "Gesichtsdaten löschen", typeof(Wpf.Ui.Controls.Button));
        Check(eraseButton is not null, "Knopf „Gesichtsdaten löschen…“ sichtbar");
        eraseButton!.BringIntoView();
        await _settings.FaceData!.EraseConfirmedAsync(_libraryId).ConfigureAwait(true);
        Check(_settings.FaceData.MessageIsError == false, $"Meldung: {_settings.FaceData.Message}");
        var left = await DbScalarAsync<int>(
            "SELECT (SELECT COUNT(*) FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE f.LibraryId = $lib) + (SELECT COUNT(*) FROM FaceScans s JOIN Files f ON f.Id = s.FileId WHERE f.LibraryId = $lib) + (SELECT COUNT(*) FROM Persons WHERE Name LIKE 'Smokeperson%')",
            ("$lib", _libraryId)).ConfigureAwait(true);
        Check(left == 0, $"DB: Gesichter, Analyse-Vermerke und Personen der Bibliothek gelöscht ({left} übrig)");
        Check(_faceIndex?.Status.State == FaceIndexState.Disabled && _settings.Faces?.IsEnabled == false, "Gesichtserkennung danach aus");
        await SnapshotAsync("25-einstellungen-gesichtsdaten-geloescht").ConfigureAwait(true);
        await WaitUntilAsync(() => !_gallery.ShowPersonFilter && !_gallery.HasPersons, ShortWait, "Galerie: Personen-Filter ohne Personen/Gesichter ausgeblendet").ConfigureAwait(true);
        var files = await DbScalarAsync<int>("SELECT COUNT(*) FROM Files WHERE LibraryId = $lib", ("$lib", _libraryId)).ConfigureAwait(true);
        Check(files >= baseline, $"Fotos bleiben ({files} Dateien im Katalog)");
    }
}

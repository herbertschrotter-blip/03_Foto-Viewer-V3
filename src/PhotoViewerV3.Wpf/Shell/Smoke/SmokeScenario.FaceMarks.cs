using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PhotoViewerV3.Gallery.Data;
using PhotoViewerV3.Gallery.ViewModels;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Lightbox.Faces;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Teil des Rauchtest-Schritts „Personen in Galerie/Lightbox“ (0.2.39, „Gesichter sichtbar machen“): Kachel-Markierung
/// für unbenannte Gesichter, Filter „alle Fotos mit Gesichtern“ und die gemerkte Taste F der Lightbox. Erwartungen
/// werden aus dem DB-Stand berechnet — die Schritte davor ändern Gesichter und Personen.
/// </summary>
internal sealed partial class SmokeScenario
{
    /// <summary>Unbenannt wie in Filter und Kachel: unbestätigt, Vorschlag oder bestätigt ohne Person (ignorierte nicht).</summary>
    private const string UnnamedFaceSql = "(fa.State IN (0, 1) OR (fa.State = 2 AND fa.PersonId IS NULL))";

    private async Task CheckUnnamedFaceMarksAsync(GalleryItem annaItem, GalleryItem bertItem)
    {
        foreach (var item in new[] { annaItem, bertItem })
        {
            var expected = await DbScalarAsync<int>(
                $"SELECT COUNT(*) FROM Faces fa WHERE fa.FileId = $f AND {UnnamedFaceSql}", ("$f", item.Id)).ConfigureAwait(true);
            await WaitUntilAsync(() => item.Persons.UnnamedCount == expected, ShortWait, $"Kachel: {expected} unbenannte Gesichter").ConfigureAwait(true);
        }

        var tip = bertItem.Persons.UnnamedToolTip.Replace(Environment.NewLine, " / ", StringComparison.Ordinal);
        Check(
            bertItem.Persons.HasUnnamed && tip.Contains("noch nicht benannt", StringComparison.Ordinal) && tip.Contains("Smokeperson Bert?", StringComparison.Ordinal),
            $"Kachel: Gesichts-Symbol {bertItem.Persons.UnnamedCount}, Tooltip „{tip}“");
    }

    private async Task CheckAnyFaceFilterAsync()
    {
        var expected = await DbScalarAsync<int>(
            "SELECT COUNT(DISTINCT fa.FileId) FROM Faces fa JOIN Files f ON f.Id = fa.FileId WHERE f.LibraryId = $lib AND fa.State IN (0, 1, 2)",
            ("$lib", _libraryId)).ConfigureAwait(true);
        _gallery.PersonFilterMode = GalleryPersonMode.AnyFace;
        await WaitForGalleryAsync("alle Fotos mit Gesichtern").ConfigureAwait(true);
        Check(
            _gallery.TotalCount == expected && expected > 0 && _gallery.PersonFilterText == "Personen: alle Fotos mit Gesichtern",
            $"„alle Fotos mit Gesichtern“: {_gallery.TotalCount} (erwartet {expected}; ignorierte zählen nicht)");
    }

    /// <summary>Taste F war beim Schließen an → neue Lightbox zeigt die Rahmen sofort; beim Blättern bleiben sie an.</summary>
    private async Task CheckLightboxFacesRememberedAsync(GalleryItem bertItem)
    {
        const string StoredSql = "SELECT COUNT(*) FROM Settings WHERE Key = $k AND Value = $v";
        var on = await DbScalarAsync<int>(StoredSql, ("$k", LightboxFacesViewModel.ShowFacesKey), ("$v", "true")).ConfigureAwait(true);
        Check(on == 1, $"Taste F gemerkt ({LightboxFacesViewModel.ShowFacesKey} = true)");

        _gallery.OpenInLightboxCommand.Execute(bertItem);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox erneut geöffnet").ConfigureAwait(true);
        var lightbox = Application.Current.Windows.OfType<LightboxWindow>().First().ViewModel;
        await WaitUntilAsync(
            () => lightbox.CurrentItem?.FileId == bertItem.Id && lightbox.Faces.IsVisible && lightbox.Faces.Boxes.Count > 0,
            ShortWait,
            "Lightbox öffnet mit Gesichtsrahmen (gemerkt)").ConfigureAwait(true);
        if (lightbox.CanGoNext)
        {
            lightbox.NextCommand.Execute(null);
            await WaitUntilAsync(() => lightbox.CurrentItem?.FileId != bertItem.Id, ShortWait, "Lightbox weitergeblättert").ConfigureAwait(true);
            Check(lightbox.Faces.IsVisible, "Gesichtsrahmen bleiben beim Blättern eingeschaltet");
        }

        await lightbox.ToggleFacesCommand.ExecuteAsync(null).ConfigureAwait(true);
        var off = await DbScalarAsync<int>(StoredSql, ("$k", LightboxFacesViewModel.ShowFacesKey), ("$v", "false")).ConfigureAwait(true);
        Check(!lightbox.Faces.IsVisible && off == 1, "Taste F aus → gemerkt (false)");
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
    }
}

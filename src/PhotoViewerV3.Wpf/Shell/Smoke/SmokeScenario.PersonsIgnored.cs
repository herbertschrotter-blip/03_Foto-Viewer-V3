using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Persons.ViewModels;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Block „Ignorierte Gesichter“ im Rauchtest-Schritt „Personen“ (0.2.40, ADR-0040): ein Gesicht manuell ignorieren und in
/// der Ansicht finden, Mindestgröße über alle Gesichter (400 px &gt; 200 px der Fake-Gesichter) → alle offenen automatisch
/// ignoriert, bestätigte und das manuell ignorierte bleiben; Schwelle zurück → automatisch ignorierte kommen zurück;
/// „Zurückholen“ über die Ansicht. Erwartungen aus dem DB-Stand, vorher jeweils WaitForIdleAsync.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string LibraryFaces = "FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.LibraryId = $lib";

    private async Task PersonsIgnoredAsync(PersonsViewModel page, IFaceRecognitionService recognition)
    {
        Check(recognition is IFaceIgnoreService, "Zuordnungsdienst bietet „Ignorierte Gesichter“ (IFaceIgnoreService)");
        var ignore = (IFaceIgnoreService)recognition;
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var originalMin = await ignore.GetMinFaceSizeAsync().ConfigureAwait(true);
        Task<long> Count(string where) => DbScalarAsync<long>($"SELECT COUNT(*) {LibraryFaces} AND {where}", ("$lib", _libraryId));
        var open0 = await Count("x.State IN (0, 1)").ConfigureAwait(true);
        var confirmed0 = await Count("x.State = 2").ConfigureAwait(true);
        var auto0 = await Count("x.State = 3 AND x.SuggestionScore < 0").ConfigureAwait(true);
        Check(open0 >= 2, $"Ausgangslage: {open0} offene, {confirmed0} bestätigte, {auto0} automatisch ignorierte Gesichter (Schwelle {originalMin} px)");

        // (a) manuell ignorieren → in der Ansicht, nicht als „zu klein“ markiert
        var manual = await DbScalarAsync<long>($"SELECT x.Id {LibraryFaces} AND x.State IN (0, 1) ORDER BY x.Id DESC LIMIT 1", ("$lib", _libraryId)).ConfigureAwait(true);
        await recognition.IgnoreFacesAsync([manual]).ConfigureAwait(true);
        await page.ShowIgnoredCommand.ExecuteAsync(null).ConfigureAwait(true);
        Check(page.Mode == PersonsPageMode.Ignored && page.Ignored.Any(t => t.FaceId == manual && !t.IsAutoIgnored),
            $"Ansicht „Ignorierte Gesichter“: {page.IgnoredSummary}");

        // (b) Mindestgröße über alle Fake-Gesichter → alle offenen automatisch ignoriert, Bestätigte und Manuelles bleiben
        await ignore.SetMinFaceSizeAsync(FaceAutoIgnore.MaxMinSize).ConfigureAwait(true);
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var autoAll = await Count("x.State = 3 AND x.SuggestionScore < 0").ConfigureAwait(true);
        var openAll = await Count("x.State IN (0, 1)").ConfigureAwait(true);
        var confirmedAll = await Count("x.State = 2").ConfigureAwait(true);
        var manualMarker = await DbScalarAsync<long>("SELECT COUNT(*) FROM Faces WHERE Id = $f AND State = 3 AND SuggestionScore IS NULL", ("$f", manual)).ConfigureAwait(true);
        Check(autoAll == auto0 + open0 - 1 && openAll == 0 && confirmedAll == confirmed0 && manualMarker == 1,
            $"Mindestgröße {FaceAutoIgnore.MaxMinSize} px: {autoAll} automatisch ignoriert, {openAll} offen, {confirmedAll} bestätigt, manuell ignoriertes unverändert");
        await page.LoadIgnoredAsync(keepState: false).ConfigureAwait(true);
        Check(page.Ignored.Count(t => t.IsAutoIgnored) == autoAll, $"Ansicht markiert {page.Ignored.Count(t => t.IsAutoIgnored)} als „zu klein“ ({page.IgnoredRangeText})");
        await WaitUntilAsync(() => page.Ignored.Take(4).All(t => t.Image is not null), ShortWait, "Ausschnitte der ignorierten Gesichter").ConfigureAwait(true);
        await SnapshotAsync("23b-personen-ignorierte").ConfigureAwait(true);

        // (c) Schwelle zurück → automatisch ignorierte kommen zurück, das manuell ignorierte nicht
        await ignore.SetMinFaceSizeAsync(originalMin).ConfigureAwait(true);
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var autoBack = await Count("x.State = 3 AND x.SuggestionScore < 0").ConfigureAwait(true);
        var openBack = await Count("x.State IN (0, 1)").ConfigureAwait(true);
        Check(autoBack == auto0 && openBack == open0 - 1, $"Schwelle {originalMin} px: {autoBack} automatisch ignoriert, {openBack} offen (manuell ignoriertes bleibt)");

        // (d) „Zurückholen“ über die Ansicht
        await page.LoadIgnoredAsync(keepState: false).ConfigureAwait(true);
        var tile = page.Ignored.FirstOrDefault(t => t.FaceId == manual);
        Check(tile is not null, "manuell ignoriertes Gesicht in der Ansicht");
        page.ToggleTileCommand.Execute(tile);
        await page.RestoreIgnoredCommand.ExecuteAsync(null).ConfigureAwait(true);
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var state = await DbScalarAsync<long>("SELECT State FROM Faces WHERE Id = $f", ("$f", manual)).ConfigureAwait(true);
        var openEnd = await Count("x.State IN (0, 1)").ConfigureAwait(true);
        Check(state is (long)FaceState.Unconfirmed or (long)FaceState.Suggested && openEnd == open0 && !page.Ignored.Any(t => t.FaceId == manual),
            $"zurückgeholt: State {state}, {openEnd} offene Gesichter wie vorher");
        _logger.LogInformation("Rauchtest   Ignorierte Gesichter: {Auto} automatisch bei {Max} px, zurück bei {Min} px", autoAll, FaceAutoIgnore.MaxMinSize, originalMin);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Domain.Enums;
using PhotoViewerV3.Lightbox;
using PhotoViewerV3.Persons;
using PhotoViewerV3.Persons.ViewModels;
using PhotoViewerV3.Services.Faces;
using PhotoViewerV3.Services.Faces.Assignment;

namespace PhotoViewerV3.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Personen“ (0.2.37, ADR-0037) nach dem Schritt „Gesichter“. Der Fake-Analysator liefert
/// Fingerabdrücke aus Pixeln (alle sehr ähnlich) — deshalb schreibt der Schritt reproduzierbare synthetische
/// Fingerabdrücke in die Gesichter der Testbibliothek (nach Face-Id): 8 × „Person A“, 6 × „Person B“, 1 Ausreißer von A
/// (Ähnlichkeit 0,68 zum Zentrum), der Rest zufällig. Danach über das ViewModel der Seite: Gruppe A öffnen (Ausreißer am
/// Ende, markiert, abgewählt), 6 Gesichter als neue Person benennen, Vorschläge der Person bestätigen (Ausreißer-
/// Rückfrage → ohne Ausreißer) und ablehnen, Gruppe B benennen und mit A zusammenführen, Gesichts-Ausschnitte im Cache.
/// </summary>
internal sealed partial class SmokeScenario
{
    private PersonsViewModel? _personsPage;
    private IFaceRecognitionService? _recognition;
    private SmokePersonsDialogs? _personsDialogs;

    private async Task PersonsAsync()
    {
        Check(_personsPage is not null && _recognition is not null && _personsDialogs is not null, "Personen-Seite, Zuordnungsdienst und Rauchtest-Dialoge vorhanden");
        var page = _personsPage!;
        var recognition = _recognition!;
        var dialogs = _personsDialogs!;

        var faces = await PrepareSyntheticFacesAsync().ConfigureAwait(true);
        Check(faces.A.Count == 8 && faces.B.Count == 6, $"synthetische Fingerabdrücke: A {faces.A.Count}, B {faces.B.Count}, Ausreißer {faces.Outlier}");
        recognition.InvalidateAll();
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);

        _navigation.NavigateTo(NavigationTags.Persons);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Persons, ShortWait, "Personen-Seite").ConfigureAwait(true);
        await page.LoadOverviewAsync(refreshGroups: true).ConfigureAwait(true);
        Check(page.Persons.Count == 0 && page.Groups.Count >= 2, $"Übersicht: {page.Persons.Count} Personen, {page.Groups.Count} Gruppen ({page.GroupsSummary})");
        Check(page.IsRecognitionOff && page.RecognitionOffText.Contains("Einstellungen", StringComparison.Ordinal), "Hinweis: Gesichtserkennung aus (Schritt „Gesichter“ hat sie wieder ausgeschaltet)");

        // (1) Gruppe A: Ausreißer am Ende, markiert und abgewählt
        var groupA = page.Groups.FirstOrDefault(g => g.Group.Members.Any(m => m.FaceId == faces.Outlier));
        Check(groupA is not null && groupA.Group.Count == 9, $"Gruppe A mit Ausreißer: {groupA?.Group.Count} Gesichter");
        page.OpenGroupCommand.Execute(groupA);
        Check(page.Mode == PersonsPageMode.Group && page.GroupMembers[^1].FaceId == faces.Outlier && page.GroupMembers[^1].IsOutlier && !page.GroupMembers[^1].IsSelected,
            "Ausreißer steht am Ende, ist markiert und abgewählt");
        Check(page.GroupSelectedCount == 8, $"vorausgewählt: {page.GroupSelectionText}");
        await WaitUntilAsync(() => page.GroupMembers.Take(4).All(t => t.Image is not null), ShortWait, "Gesichts-Ausschnitte der sichtbaren Kacheln").ConfigureAwait(true);
        await SnapshotAsync("22-personen-gruppe-ausreisser").ConfigureAwait(true);

        // (2) Nur 6 benennen — 2 bleiben als Vorschläge übrig
        foreach (var tile in page.GroupMembers.Where(t => t.FaceId == faces.A[6] || t.FaceId == faces.A[7]))
        {
            page.ToggleTileCommand.Execute(tile);
        }

        dialogs.NextName = "Smoke Anna";
        dialogs.OutlierDecision = OutlierDecision.SkipOutliers;
        await page.NameGroupCommand.ExecuteAsync(null).ConfigureAwait(true);
        var anna = await recognition.FindPersonByNameAsync("Smoke Anna").ConfigureAwait(true);
        Check(anna is not null && page.Mode == PersonsPageMode.Overview, $"Person „Smoke Anna“ angelegt ({page.Message})");
        var confirmedA = await DbScalarAsync<long>("SELECT COUNT(*) FROM Faces WHERE State = 2 AND PersonId = $p", ("$p", anna!.Value)).ConfigureAwait(true);
        var outlierState = await DbScalarAsync<long>("SELECT State FROM Faces WHERE Id = $f", ("$f", faces.Outlier)).ConfigureAwait(true);
        Check(confirmedA == 6 && outlierState != (long)FaceState.Confirmed, $"6 bestätigt, Ausreißer nicht (State {outlierState})");

        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var suggested = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM Faces WHERE State = 1 AND SuggestedPersonId = $p AND Id IN ($a6, $a7, $o)",
            ("$p", anna.Value), ("$a6", faces.A[6]), ("$a7", faces.A[7]), ("$o", faces.Outlier)).ConfigureAwait(true);
        Check(suggested >= 2, $"nach dem Lernen: {suggested} Vorschläge für „Smoke Anna“");

        // (3) Detail: Vorschlag bestätigen, Ausreißer ablehnen
        await page.LoadOverviewAsync().ConfigureAwait(true);
        var annaTile = page.Persons.FirstOrDefault(p => p.PersonId == anna.Value);
        Check(annaTile is not null && annaTile.Overview.Person.FaceCount == 6, $"Kachel „Smoke Anna“: {annaTile?.Details}");
        await page.OpenPersonCommand.ExecuteAsync(annaTile).ConfigureAwait(true);
        Check(page.Mode == PersonsPageMode.Person && page.DetailTab == 0 && page.Suggestions.Count >= 2, $"Detail: {page.PersonDetails}");
        await SnapshotAsync("23-personen-vorschlaege").ConfigureAwait(true);

        var toConfirm = page.Suggestions.First(t => t.FaceId == faces.A[6]);
        await page.ConfirmSuggestionCommand.ExecuteAsync(toConfirm).ConfigureAwait(true);
        var a6 = await DbScalarAsync<long>("SELECT State FROM Faces WHERE Id = $f", ("$f", faces.A[6])).ConfigureAwait(true);
        Check(a6 == (long)FaceState.Confirmed, "Vorschlag bestätigt");

        // Ablehnen: bevorzugt der Ausreißer (falls er vorgeschlagen wurde), sonst das letzte A-Gesicht
        var rejectTile = page.Suggestions.FirstOrDefault(t => t.FaceId == faces.Outlier) ?? page.Suggestions.First(t => t.FaceId == faces.A[7]);
        await page.RejectSuggestionCommand.ExecuteAsync(rejectTile).ConfigureAwait(true);
        var rejected = await DbScalarAsync<long>("SELECT COUNT(*) FROM FaceRejections WHERE FaceId = $f AND PersonId = $p", ("$f", rejectTile.FaceId), ("$p", anna.Value)).ConfigureAwait(true);
        var afterReject = await DbScalarAsync<long>("SELECT COALESCE(SuggestedPersonId, 0) FROM Faces WHERE Id = $f", ("$f", rejectTile.FaceId)).ConfigureAwait(true);
        Check(rejected == 1 && afterReject != anna.Value && !page.Suggestions.Any(t => t.FaceId == rejectTile.FaceId),
            $"Vorschlag abgelehnt ({(rejectTile.FaceId == faces.Outlier ? "Ausreißer" : "A-Gesicht")}): FaceRejections, nicht mehr „Smoke Anna“");

        // Ausreißer-Prüfung beim Bestätigen eines fremden Gesichts
        await recognition.WaitForIdleAsync().WaitAsync(LongWait).ConfigureAwait(true);
        var check = await recognition.CheckOutliersAsync([faces.B[0]], anna.Value).ConfigureAwait(true);
        Check(check.Count == 1 && check[0].IsOutlier, $"Ausreißer-Prüfung: Gesicht von B passt nicht zu A ({check.FirstOrDefault()?.Check.Kind})");

        // (4) Gruppe B benennen, dann mit A zusammenführen
        await page.BackCommand.ExecuteAsync(null).ConfigureAwait(true);
        await page.LoadOverviewAsync(refreshGroups: true).ConfigureAwait(true);
        var groupB = page.Groups.FirstOrDefault(g => g.Group.Members.Any(m => m.FaceId == faces.B[0]));
        Check(groupB is not null && groupB.Group.Count == 6 && groupB.Group.OutlierCount == 0, $"Gruppe B: {groupB?.Group.Count} Gesichter");
        page.OpenGroupCommand.Execute(groupB);
        dialogs.NextName = "Smoke Bert";
        await page.NameGroupCommand.ExecuteAsync(null).ConfigureAwait(true);
        var bert = await recognition.FindPersonByNameAsync("Smoke Bert").ConfigureAwait(true);
        Check(bert is not null, "Person „Smoke Bert“ angelegt");

        await page.LoadPersonAsync(bert!.Value, keepState: false).ConfigureAwait(true);
        dialogs.NextPick = (await recognition.GetPersonsAsync(default).ConfigureAwait(true)).First(p => p.Id == anna.Value);
        await page.MergePersonCommand.ExecuteAsync(null).ConfigureAwait(true);
        var persons = await DbScalarAsync<long>("SELECT COUNT(*) FROM Persons").ConfigureAwait(true);
        var merged = await DbScalarAsync<long>("SELECT COUNT(*) FROM Faces WHERE State = 2 AND PersonId = $p", ("$p", anna.Value)).ConfigureAwait(true);
        Check(persons == 1 && merged == 13 && page.CurrentPerson?.Id == anna.Value, $"zusammengeführt: {persons} Person, {merged} bestätigte Gesichter, Ansicht „{page.PersonTitle}“");

        // (5) Doppelklick → Lightbox, Ausschnitte im Cache
        page.DetailTab = 1;
        await page.OpenFaceCommand.ExecuteAsync(page.Confirmed[0]).ConfigureAwait(true);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox aus der Personen-Seite").ConfigureAwait(true);
        var lightbox = System.Windows.Application.Current.Windows.OfType<LightboxWindow>().First();
        lightbox.ViewModel.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        var cropFolder = Path.Combine(_paths.GetLibraryThumbnailsPath(_libraryId), "faces");
        var crops = Directory.Exists(cropFolder) ? Directory.EnumerateFiles(cropFolder, "*.jpg").Count() : 0;
        Check(crops >= 4, $"{crops} Gesichts-Ausschnitte im Cache (thumbs\\{_libraryId}\\faces)");
        _logger.LogInformation("Rauchtest   Personen: {Groups} Gruppen, {Crops} Ausschnitte", page.Groups.Count, crops);

        await page.BackCommand.ExecuteAsync(null).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);
        await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Gallery, ShortWait, "zurück in der Galerie").ConfigureAwait(true);
    }

    /// <summary>
    /// Schreibt reproduzierbare Fingerabdrücke in die Gesichter der Bibliothek (Face-Id aufsteigend) und setzt alle
    /// Zuordnungen zurück: 8 × A, 6 × B, 1 Ausreißer von A, Rest zufällig (keine Gruppe).
    /// </summary>
    private Task<(List<long> A, List<long> B, long Outlier)> PrepareSyntheticFacesAsync() => Task.Run(() =>
    {
        using var connection = _db.OpenSqliteConnection();
        var ids = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT x.Id FROM Faces x JOIN Files f ON f.Id = x.FileId WHERE f.LibraryId = $lib ORDER BY x.Id;";
            read.Parameters.AddWithValue("$lib", _libraryId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        if (ids.Count < 20)
        {
            return (new List<long>(), new List<long>(), 0L);
        }

        var random = new Random(4711);
        var centerA = RandomUnit(random);
        var centerB = RandomUnit(random);
        using var tx = connection.BeginTransaction();
        void Write(long id, float[] embedding)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Faces SET Embedding = $e, Quality = 0.9, State = 0, PersonId = NULL, SuggestedPersonId = NULL, SuggestionScore = NULL WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$e", FaceEmbeddingCodec.Encode(embedding));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        var a = ids.Take(8).ToList();
        var b = ids.Skip(8).Take(6).ToList();
        var outlier = ids[14];
        foreach (var id in a)
        {
            Write(id, Around(centerA, 0.80f + (float)random.NextDouble() * 0.10f, random));
        }

        foreach (var id in b)
        {
            Write(id, Around(centerB, 0.80f + (float)random.NextDouble() * 0.10f, random));
        }

        Write(outlier, Around(centerA, 0.68f, random));
        foreach (var id in ids.Skip(15))
        {
            Write(id, RandomUnit(random));
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM FaceRejections; DELETE FROM Persons;";
            clear.ExecuteNonQuery();
        }

        tx.Commit();
        return (a, b, outlier);
    });

    private static float[] Around(float[] center, float cos, Random random)
    {
        var noise = RandomUnit(random);
        var dot = 0f;
        for (var i = 0; i < noise.Length; i++)
        {
            dot += noise[i] * center[i];
        }

        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] -= dot * center[i];
        }

        FaceEmbeddingCodec.NormalizeL2(noise);
        var s = MathF.Sqrt(1 - cos * cos);
        var v = new float[center.Length];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = cos * center[i] + s * noise[i];
        }

        FaceEmbeddingCodec.NormalizeL2(v);
        return v;
    }

    private static float[] RandomUnit(Random random)
    {
        var v = new float[FaceEmbeddingCodec.Dimensions];
        for (var i = 0; i < v.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        FaceEmbeddingCodec.NormalizeL2(v);
        return v;
    }
}

/// <summary>Rauchtest-Ersatz für die Dialoge der Personen-Seite: vorgegebene Antworten statt Fenster.</summary>
internal sealed class SmokePersonsDialogs : IPersonsDialogService
{
    private readonly ILogger<SmokePersonsDialogs> _logger;

    public SmokePersonsDialogs(ILogger<SmokePersonsDialogs> logger)
    {
        _logger = logger;
    }

    public string? NextName { get; set; }

    public OutlierDecision OutlierDecision { get; set; } = OutlierDecision.SkipOutliers;

    public PersonSummary? NextPick { get; set; }

    public int OutlierQuestions { get; set; }

    public Task<string?> PromptNameAsync(string title, string message, string initialValue)
    {
        _logger.LogInformation("Rauchtest-Dialog „{Title}“ → {Name}", title, NextName ?? "(Abbruch)");
        return Task.FromResult(NextName);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        _logger.LogInformation("Rauchtest-Dialog „{Title}“: {Message} → „{Button}“", title, message, confirmText);
        return Task.FromResult(true);
    }

    public Task<OutlierDecision> AskOutliersAsync(string personName, IReadOnlyList<FaceOutlierResult> outliers, int total)
    {
        OutlierQuestions++;
        _logger.LogInformation("Rauchtest-Ausreißer-Rückfrage: {Count} von {Total} → {Decision}", outliers.Count, total, OutlierDecision);
        return Task.FromResult(OutlierDecision);
    }

    public Task<PersonSummary?> PickPersonAsync(string title, IReadOnlyList<PersonSummary> persons, int? suggestedPersonId, int? excludedPersonId)
    {
        _logger.LogInformation("Rauchtest-Personenauswahl „{Title}“ → {Name}", title, NextPick?.Name ?? "(Abbruch)");
        return Task.FromResult(NextPick);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        _logger.LogWarning("Rauchtest-Fehlerdialog „{Title}“: {Message}", title, message);
        return Task.CompletedTask;
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.ViewModels;
using Lumora.Lightbox;
using Lumora.Motifs;
using Lumora.Services.Motifs;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Motive“ (ADR-0052) mit <see cref="SmokeMotifEncoderFactory"/> statt Modell: Standard-Begriffe
/// angelegt, Einschalten im Reiter „Motive“ → Index läuft (Statusleiste), Pause/Fortsetzen, Vektoren und Treffer im
/// Katalog, Galerie-Filter „Motive“ (Zähler, „Es gilt“, Treffer = DB), Suche „Motiv: Strand“, Listenspalte, Lightbox-Info,
/// eigener Begriff mit Treffern ohne neue Analyse, danach wieder aus. Die Erkennung ist während der übrigen Schritte aus
/// (<see cref="DisableMotifsForSmokeAsync"/>), damit sie Scans und Zähler nicht stört.
/// </summary>
internal sealed partial class SmokeScenario
{
    private IMotifIndexService? _motifIndex;
    private MotifSettingsViewModel? _motifSettings;
    private IMotifTermService? _motifTerms;

    private void InitMotifs(IServiceProvider services)
    {
        _motifIndex = services.GetService<IMotifIndexService>();
        _motifSettings = services.GetService<MotifSettingsViewModel>();
        _motifTerms = services.GetService<IMotifTermService>();
    }

    /// <summary>Gleich zu Beginn: Motiv-Erkennung aus (Standard wäre an), erst der Schritt „Motive“ schaltet sie ein.</summary>
    private async Task DisableMotifsForSmokeAsync()
    {
        if (_motifIndex is not null)
        {
            await _motifIndex.SetEnabledAsync(false).ConfigureAwait(true);
        }
    }

    private async Task MotifsAsync()
    {
        Check(_motifIndex is not null && _motifSettings is not null, "Motiv-Index und Einstellungs-Reiter vorhanden");
        var index = _motifIndex!;
        var settings = _motifSettings!;
        try
        {
            await MotifsCoreAsync(index, settings).ConfigureAwait(true);
        }
        finally
        {
            if (_gallery.HasMotifFilter)
            {
                await _gallery.ClearMotifFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            if (_gallery.SearchTerm is not null)
            {
                await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            await index.SetEnabledAsync(false).ConfigureAwait(true);
        }
    }

    private async Task MotifsCoreAsync(IMotifIndexService index, MotifSettingsViewModel settings)
    {
        await OpenSettingsSectionAsync(NavigationTags.SettingsMotifs, "Einstellungen › Motive").ConfigureAwait(true);
        settings.Load();
        await WaitUntilAsync(() => settings.Terms.Count == MotifDefaults.Terms.Count, ShortWait, "Standard-Begriffe angelegt").ConfigureAwait(true);
        Check(settings.IsModelReady && !settings.IsEnabled && index.Status.State == MotifIndexState.Disabled,
            $"Modell (Fake) vorhanden, Erkennung im Rauchtest zunächst aus ({index.Status.State})");

        var analyzable = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM Files f WHERE f.LibraryId = $lib AND f.Missing = 0 AND (f.MediaType = 0 OR (f.MediaType = 1 AND EXISTS (SELECT 1 FROM Thumbnails t WHERE t.FileId = f.Id AND t.Status = 2)))",
            ("$lib", _libraryId)).ConfigureAwait(true);
        Check(analyzable >= 5, $"{analyzable} Fotos/Videos analysierbar");

        settings.IsEnabled = true; // wie der Schalter im Reiter
        await WaitUntilAsync(
            () => _statusBar.MotifIndexText is { } t && t.StartsWith("Motive:", StringComparison.Ordinal) && index.Status.Done >= 2,
            LongWait,
            "Statusleiste zeigt den laufenden Motiv-Index").ConfigureAwait(true);
        await SnapshotAsync("45-motive-einstellungen").ConfigureAwait(true);

        Check(_statusBar.PauseMotifIndexCommand.CanExecute(null), "Pause ist bedienbar");
        _statusBar.PauseMotifIndexCommand.Execute(null);
        await WaitUntilAsync(() => index.Status.State == MotifIndexState.Paused && _statusBar.MotifIndexIsPaused, ShortWait, "Motiv-Index pausiert").ConfigureAwait(true);
        await Task.Delay(SmokeMotifEncoderFactory.Delay * 4).ConfigureAwait(true);
        var paused = await CountEmbeddingsAsync().ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(1.2)).ConfigureAwait(true);
        var stillPaused = await CountEmbeddingsAsync().ConfigureAwait(true);
        Check(stillPaused == paused && paused < analyzable, $"pausiert: {paused} → {stillPaused} Vektoren (von {analyzable})");

        _statusBar.ResumeMotifIndexCommand.Execute(null);
        await WaitUntilAsync(() => index.Status.State == MotifIndexState.Idle && _statusBar.MotifIndexText is null && index.Status.Done == analyzable, LongWait, "Motiv-Index fertig").ConfigureAwait(true);
        var embeddings = await CountEmbeddingsAsync().ConfigureAwait(true);
        var vectors = await DbScalarAsync<long>(
            "SELECT COUNT(*) FROM FileEmbeddings e JOIN Files f ON f.Id = e.FileId WHERE f.LibraryId = $lib AND e.ModelVersion = $ver AND length(e.Embedding) = 1536",
            ("$lib", _libraryId), ("$ver", SmokeMotifEncoderFactory.Version)).ConfigureAwait(true);
        Check(embeddings == analyzable && vectors > 0, $"Vektoren: {embeddings} von {analyzable}, davon {vectors} mit 768 × float16");

        var strandId = settings.Terms.First(t => t.Name == SmokeMotifEncoderFactory.BrightTerm).Id;
        var nachtId = settings.Terms.First(t => t.Name == SmokeMotifEncoderFactory.DarkTerm).Id;
        var strandHits = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileMotifs WHERE TermId = $t", ("$t", strandId)).ConfigureAwait(true);
        var nachtHits = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileMotifs WHERE TermId = $t", ("$t", nachtId)).ConfigureAwait(true);
        var otherHits = await DbScalarAsync<int>("SELECT COUNT(*) FROM FileMotifs WHERE TermId NOT IN ($a, $b)", ("$a", strandId), ("$b", nachtId)).ConfigureAwait(true);
        Check(strandHits + nachtHits == vectors && otherHits == 0, $"Treffer: Strand {strandHits}, Nacht {nachtHits}, andere {otherHits}");

        // Galerie: Filter „Motive“
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
        var termWithHits = strandHits > 0 ? (Id: strandId, Name: SmokeMotifEncoderFactory.BrightTerm, Hits: strandHits) : (Id: nachtId, Name: SmokeMotifEncoderFactory.DarkTerm, Hits: nachtHits);
        await WaitUntilAsync(
            () => _gallery.ShowMotifFilter && _gallery.MotifFilterEntries.Any(e => e.TermId == termWithHits.Id && e.FileCount == termWithHits.Hits),
            ShortWait,
            $"Motiv-Filter mit „{termWithHits.Name} ({termWithHits.Hits})“").ConfigureAwait(true);
        _gallery.MotifFilterEntries.First(e => e.TermId == termWithHits.Id).IsSelected = true;
        await _gallery.ApplyMotifFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Motiv-Filter").ConfigureAwait(true);
        Check(_gallery.TotalCount == termWithHits.Hits && _gallery.HasMotifFilter, $"Filter „{termWithHits.Name}“: {_gallery.TotalCount} Treffer (erwartet {termWithHits.Hits}), Chip „{_gallery.MotifFilterValueText}“");
        Check(_gallery.ActiveConditions.Any(c => c.Kind == GalleryFilterConditionKind.Motifs && c.Label == $"Motiv „{termWithHits.Name}“"),
            $"„Es gilt“: {string.Join(" UND ", _gallery.ActiveConditions.Select(c => c.Label))}");
        await SnapshotAsync("46-motive-galerie-filter").ConfigureAwait(true);
        await _gallery.ClearMotifFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Motiv-Filter aufgehoben").ConfigureAwait(true);
        Check(!_gallery.HasMotifFilter && _gallery.TotalCount == baseline, "Motiv-Filter aufgehoben");

        // Bildinhalt-Suche (Freitext gegen die Bild-Vektoren): die Fake-Beschreibung von „Strand“ trifft genau die hellen Bilder
        Check(_gallery.CanSearchContent, "Bildinhalt-Suche verfügbar");
        _gallery.ContentSearchText = "a photo of a beach";
        await _gallery.SearchContentCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Bildinhalt-Suche").ConfigureAwait(true);
        var darkCosine = MotifVectors.Dot(SmokeMotifEncoderFactory.TermVector(SmokeMotifEncoderFactory.DarkTerm), SmokeMotifEncoderFactory.TextVector("a photo of a beach"));
        var expectedContent = strandHits + (darkCosine >= Lumora.Gallery.ViewModels.GalleryViewModel.ContentSearchMinCosine ? nachtHits : 0);
        Check(_gallery.TotalCount == expectedContent && _gallery.ActiveConditions.Any(c => c.Label == "Bildinhalt „a photo of a beach“"),
            $"Bildinhalt-Suche: {_gallery.TotalCount} (erwartet {expectedContent}), „Es gilt“: {string.Join(" UND ", _gallery.ActiveConditions.Select(c => c.Label))}");
        await _gallery.ClearMotifFilterCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Bildinhalt-Suche aufgehoben").ConfigureAwait(true);
        Check(_gallery.TotalCount == baseline && _gallery.AppliedContentSearch is null, "Bildinhalt-Suche aufgehoben");

        // Suche „Motiv: …“ und Begriff im Suchfeld
        _gallery.SearchTerm = $"Motiv: {termWithHits.Name}";
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche „Motiv: …“").ConfigureAwait(true);
        Check(_gallery.ErrorMessage is null && _gallery.TotalCount == termWithHits.Hits, $"Suche „Motiv: {termWithHits.Name}“: {_gallery.TotalCount} (erwartet {termWithHits.Hits})");
        _gallery.SearchTerm = termWithHits.Name.ToLowerInvariant();
        await _gallery.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche nach dem Begriff").ConfigureAwait(true);
        Check(_gallery.TotalCount >= termWithHits.Hits, $"Suche „{termWithHits.Name.ToLowerInvariant()}“ findet auch Motive: {_gallery.TotalCount} (≥ {termWithHits.Hits})");
        await _gallery.ClearSearchCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitForGalleryAsync("Suche geleert").ConfigureAwait(true);

        // Listenspalte „Motive“
        var item = _gallery.CurrentPage.FirstOrDefault(i => !i.IsArchive);
        Check(item is not null, "Kachel für Spalte und Lightbox");
        await WaitUntilAsync(() => item!.MotifsText.Length > 0, ShortWait, "Motive der Kachel geladen").ConfigureAwait(true);
        var columnsBefore = _gallery.ListColumnSettings;
        var modeBefore = _gallery.ViewMode;
        _gallery.ListColumnSettings = "Name,Motifs,Persons,Modified";
        _gallery.ViewMode = GalleryViewMode.List;
        await Task.Delay(400).ConfigureAwait(true);
        var listView = FindVisualChild<GalleryListView>(Application.Current.MainWindow);
        Check(listView is not null && listView.VisibleColumnKeysInOrder().Contains(GalleryListColumnKey.Motifs), "Listenansicht mit Spalte „Motive“");
        await SnapshotAsync("47-motive-galerie-liste").ConfigureAwait(true);
        _gallery.ViewMode = modeBefore;
        _gallery.ListColumnSettings = columnsBefore;
        await Task.Delay(300).ConfigureAwait(true);

        // Lightbox-Info
        _gallery.OpenInLightboxCommand.Execute(item);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var lightbox = window.ViewModel;
        await WaitUntilAsync(() => lightbox.CurrentItem?.FileId == item!.Id && lightbox.HasCurrentMotifs, ShortWait, "Motive in der Lightbox").ConfigureAwait(true);
        Check(lightbox.CurrentMotifsText == item!.MotifsText, $"Lightbox-Info „Motive: {lightbox.CurrentMotifsText}“");
        lightbox.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // Eigener Begriff: Treffer aus den gespeicherten Vektoren, ohne neue Analyse
        await OpenSettingsSectionAsync(NavigationTags.SettingsMotifs, "Einstellungen › Motive").ConfigureAwait(true);
        settings.NewTermName = "Smoke-Begriff";
        settings.NewTermPrompt = "a photo of a beach"; // gleiche Beschreibung wie „Strand“ → ähnlicher Fake-Vektor
        await settings.AddTermCommand.ExecuteAsync(null).ConfigureAwait(true);
        await WaitUntilAsync(() => settings.Terms.Any(t => t.Name == "Smoke-Begriff" && !t.IsPending), ShortWait, "eigener Begriff mit Text-Vektor").ConfigureAwait(true);
        var embeddingsAfter = await CountEmbeddingsAsync().ConfigureAwait(true);
        Check(embeddingsAfter == embeddings, $"Bild-Vektoren unverändert ({embeddingsAfter}) — Begriff ohne neue Analyse");
        var row = settings.Terms.First(t => t.Name == "Smoke-Begriff");
        await _motifTerms!.DeleteTermAsync(row.Id).ConfigureAwait(true); // „Löschen“ fragt in einem echten Dialog nach — hier direkt
        await WaitUntilAsync(() => settings.Terms.All(t => t.Name != "Smoke-Begriff"), ShortWait, "eigener Begriff gelöscht").ConfigureAwait(true);
    }

    private Task<long> CountEmbeddingsAsync()
        => DbScalarAsync<long>("SELECT COUNT(*) FROM FileEmbeddings e JOIN Files f ON f.Id = e.FileId WHERE f.LibraryId = $lib", ("$lib", _libraryId));
}

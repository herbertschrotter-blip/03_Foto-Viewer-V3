using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Services.Assistant;
using Lumora.Services.Infrastructure;
using Lumora.Services.Stories;
using Lumora.Settings;
using Lumora.Stories;
using Lumora.Stories.Assistant;
using Wpf.Ui.Appearance;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Story-Assistent“ (ADR-0063, nach „Story-Seite“, vor „Versteckte Bibliothek“) — mit der Attrappe
/// <see cref="RuleIntentModel"/> statt Ollama (kein Netzwerk, kein Modell): Satz aus einem vorhandenen Namen bzw. Motiv →
/// Leiste → Verständnis-Karte mit Treffern → eigene Story über den Assistenten anlegen (Fotos wie gezählt) → Collage
/// aus den Originalen in einen Temp-Ordner des Katalogs rendern (Datei da, Maße 1920 × 1080) → Verlauf. Danach aufgeräumt
/// (Story, Collage, Verlauf). Screenshots der Karte hell/dunkel.
/// </summary>
internal sealed partial class SmokeScenario
{
    private async Task StoryAssistantAsync()
    {
        var services = _storyPageServices;
        var page = services?.GetService<StoriesViewModel>();
        var assistant = services?.GetService<IStoryAssistant>();
        var collage = services?.GetService<ICollageService>();
        var catalog = services?.GetService<ICatalogStoragePaths>();
        var model = services?.GetService<IIntentModel>();
        Check(page?.Assistant is not null && assistant is not null && collage is not null && catalog is not null && _stories is not null,
            "Assistent, Collage-Dienst und Story-Seite registriert");
        Check(model is RuleIntentModel, $"Sprach-KI im Rauchtest = Attrappe ({model?.GetType().Name})");
        var vm = page!.Assistant!;
        var folder = Path.Combine(catalog!.TempRoot, "smoke-collage");
        int? storyId = null;
        try
        {
            // (1) Satz aus dem Wortschatz des Bereichs: eine Person mit Fotos, sonst ein Motiv-Begriff
            var scope = await services!.GetRequiredService<Lumora.Services.Library.ILibraryScope>().GetCurrentAsync().ConfigureAwait(true);
            var vocabulary = await services!.GetRequiredService<IAssistantStore>().ReadVocabularyAsync(scope).ConfigureAwait(true);
            var person = vocabulary.Persons.Where(p => p.FileCount > 0).OrderByDescending(p => p.FileCount).FirstOrDefault();
            var motif = vocabulary.Motifs.FirstOrDefault();
            var sentence = person is not null ? $"Story mit {person.Name}" : motif is not null ? $"Story von {motif.Name}" : null;
            Check(sentence is not null, $"Wortschatz: {vocabulary.Persons.Count} Personen, {vocabulary.Motifs.Count} Motive, {vocabulary.Tags.Count} Tags");
            if (sentence is null)
            {
                return;
            }

            // (2) Leiste: Satz → Verständnis-Karte
            _navigation.NavigateTo(NavigationTags.Stories);
            await WaitUntilAsync(() => _navigation.CurrentTag == NavigationTags.Stories, ShortWait, "Seite „Storys“ aktiv").ConfigureAwait(true);
            await vm.RefreshAsync().ConfigureAwait(true);
            Check(vm.IsEnabled && vm.SetupHint is null, "Leiste aktiv, Attrappe „bereit“ (kein Hinweis auf Ollama)");
            vm.Sentence = sentence;
            await vm.AskAsync().ConfigureAwait(true);
            for (var i = 0; i < 3 && vm.Question is { } question; i++)
            {
                await question.Choices[0].ChooseCommand.ExecuteAsync(null).ConfigureAwait(true); // Rückfrage: erste Wahl
            }

            Check(vm.Card is not null && vm.ErrorText is null, $"Karte „So habe ich es verstanden“ ({vm.Card?.Facts.Count} Punkte){(vm.ErrorText is null ? string.Empty : ": " + vm.ErrorText)}");
            var plan = vm.Plan;
            var result = vm.Result;
            Check(plan is not null && result is { Count: > 0 } && vm.Card?.CountText == result.Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")),
                $"Treffer: {result?.Count} ({result?.PhotoCount} Fotos, {result?.VideoCount} Videos), Vorschau {result?.PreviewIds.Count}");
            Check(vm.Card?.Facts.Any(f => f.Label == "Was entsteht") == true && vm.Card.CanCreate, "Was entsteht + „Erstellen“ bereit");
            _settings.IsThemeLight = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light, ShortWait, "Theme Hell").ConfigureAwait(true);
            await SnapshotAsync("67-assistent-karte-hell").ConfigureAwait(true);
            _settings.IsThemeDark = true;
            await WaitUntilAsync(() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark, ShortWait, "Theme Dunkel").ConfigureAwait(true);
            await SnapshotAsync("68-assistent-karte-dunkel").ConfigureAwait(true);
            if (plan is null || result is not { Count: > 0 })
            {
                return;
            }

            // (3) Eigene Story über den Assistenten: genau die gezählten Fotos, sachliche Überschrift
            storyId = await assistant!.CreateStoryAsync(plan, result).ConfigureAwait(true);
            var card = await _stories!.GetAsync(storyId.Value).ConfigureAwait(true);
            var expected = result.Selected.Count > 0 ? result.Selected.Count : result.Count;
            Check(card is { Kind: Domain.Enums.StoryKind.Custom } && card.FileCount == expected,
                $"Story „{card?.Title}“: {card?.FileCount} von {expected} Dateien");

            // (4) Collage aus den Originalen in einen Temp-Ordner (Full HD 16:9, Raster)
            var photos = await collage!.LoadPhotosAsync(result.FileIds).ConfigureAwait(true);
            Check(photos.Count > 0, $"Collage: {photos.Count} Fotos (ohne Videos)");
            if (photos.Count > 0)
            {
                var target = Path.Combine(folder, "rauchtest-collage.jpg");
                var saved = await collage.SaveAsync(
                    new CollageOptions(CollageFormat.Wide16x9, CollageSize.FullHd, 12, CollageArrangement.Grid, ShowTitle: true, ShowDates: true),
                    photos.Take(12).ToList(), card?.Title, target).ConfigureAwait(true);
                var (width, height) = ReadCollageSize(saved.Path);
                Check(File.Exists(saved.Path) && width == 1920 && height == 1080 && saved.Width == 1920,
                    $"Collage gespeichert: {width} × {height}, {new FileInfo(saved.Path).Length / 1024} kB");
                Check(Directory.GetFiles(folder).Length == 1, "keine Temp-Reste neben der Collage");
            }

            // (5) Verlauf „Zuletzt gefragt“
            vm.Dismiss();
            await vm.RefreshAsync().ConfigureAwait(true);
            Check(vm.History.Any(h => h.Sentence == sentence) && vm.ShowHistory, $"Verlauf: {vm.History.Count} Sätze");
        }
        finally
        {
            vm.Dismiss();
            if (storyId is { } id)
            {
                await _stories!.DeleteAsync(id).ConfigureAwait(true);
            }

            if (assistant is not null)
            {
                await assistant.ClearHistoryAsync().ConfigureAwait(true);
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            _settings.IsThemeSystem = true;
            await WaitUntilAsync(() => _theme.Mode == AppThemeMode.System, ShortWait, "Theme System").ConfigureAwait(true);
        }
    }

    private static (int Width, int Height) ReadCollageSize(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}

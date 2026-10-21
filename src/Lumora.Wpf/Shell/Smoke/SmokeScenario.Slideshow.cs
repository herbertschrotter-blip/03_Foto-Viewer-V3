using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Lumora.Lightbox;
using Lumora.Slideshow;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „Diashow“ (ADR-0059, vor „Versteckte Bibliothek“): aus der Galerie ohne Auswahl starten (ganze
/// Ansicht, seitenweise), rahmenloses Fenster über dem Hauptfenster, zwei Folien weiter, bis zu einem Video blättern (es
/// läuft), mit „Weiter“ überspringen, Esc → Fenster zu, zurück in der Galerie. Dazu „Diashow ab hier“ aus der Lightbox
/// (Startindex = Lightbox-Position). Einstellungen für den Schritt fest (Reihenfolge wie Ansicht, Videos an), danach zurück.
/// </summary>
internal sealed partial class SmokeScenario
{
    private SlideshowService? _slideshow;
    private ISlideshowSettingsStore? _slideshowSettings;

    private void InitSlideshowSmoke(IServiceProvider services)
    {
        _slideshow = services.GetService<SlideshowService>();
        _slideshowSettings = services.GetService<ISlideshowSettingsStore>();
    }

    private async Task SlideshowAsync()
    {
        Check(_slideshow is not null && _slideshowSettings is not null, "Diashow-Dienst und Einstellungen vorhanden");
        // Standardwerte für den Schritt (im frischen Rauchtest-AppData schon gesetzt → kein Schreiben vor dem nächsten Schritt)
        var saved = await _slideshowSettings!.LoadAsync().ConfigureAwait(true);
        var changed = saved != SlideshowSettings.Default;
        if (changed)
        {
            await _slideshowSettings.SaveAsync(SlideshowSettings.Default).ConfigureAwait(true);
        }

        try
        {
            _navigation.NavigateTo(NavigationTags.Gallery);
            await _gallery.ResetFiltersCommand.ExecuteAsync(null).ConfigureAwait(true);
            _folders.SelectWholeLibraryCommand.Execute(null);
            await WaitForGalleryAsync("Galerie für die Diashow").ConfigureAwait(true);
            _gallery.ClearSelectionCommand.Execute(null);
            Check(_gallery.HasSlideshow && _gallery.TotalCount >= 3 && _gallery.CurrentPage.Any(i => i.IsVideo),
                $"Galerie: {_gallery.TotalCount} Dateien, mit Video, Diashow verfügbar");

            // (1) Start aus der Galerie ohne Auswahl → ganze Ansicht ab dem Anfang
            _gallery.StartSlideshowCommand.Execute(null);
            await WaitUntilAsync(() => _slideshow!.Window is { IsLoaded: true }, ShortWait, "Diashow-Fenster offen").ConfigureAwait(true);
            var window = _slideshow!.Window!;
            var vm = window.ViewModel;
            await WaitUntilAsync(() => vm.CurrentSlide is not null && !vm.IsLoading, ShortWait, "erste Folie").ConfigureAwait(true);
            vm.TogglePause(); // der Schritt blättert selbst
            Check(vm.Count == _gallery.TotalCount && vm.CurrentSlide!.Info.FileId == _gallery.CurrentPage[vm.CurrentIndex].Id,
                $"Diashow über die ganze Ansicht in Galerie-Sortierung: {vm.PositionText}");
            Check(window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.NoResize,
                $"rahmenlos ({window.WindowStyle}, {window.ResizeMode}), {window.ActualWidth:N0}×{window.ActualHeight:N0}");
            Check(ReferenceEquals(window.Owner, Application.Current.MainWindow), "Besitzer = Hauptfenster");
            Check(vm.StatusText is null, "keine Meldung");
            await SnapshotAsync("60-diashow", window).ConfigureAwait(true);

            // (2) zwei Folien weiter
            var start = vm.CurrentIndex;
            await vm.NextAsync().ConfigureAwait(true);
            await vm.NextAsync().ConfigureAwait(true);
            Check(vm.CurrentIndex != start && vm.CurrentSlide is not null && vm.PositionText.StartsWith($"{vm.CurrentIndex + 1} /", StringComparison.Ordinal),
                $"zwei weiter: {vm.PositionText}");

            // (3) bis zu einem Video blättern — es läuft — und mit „Weiter“ überspringen
            vm.TogglePause();
            for (var i = 0; i < vm.Count && !vm.IsVideo; i++)
            {
                await vm.NextAsync().ConfigureAwait(true);
            }

            Check(vm.IsVideo && vm.CurrentSlide is { IsVideo: true }, $"Video erreicht: {vm.PositionText}");
            await WaitUntilAsync(() => window.VideoPlayer?.IsPlaying == true, TimeSpan.FromSeconds(30), "Video spielt").ConfigureAwait(true);
            await SnapshotAsync("61-diashow-video", window).ConfigureAwait(true);
            var videoIndex = vm.CurrentIndex;
            await vm.NextAsync().ConfigureAwait(true);
            Check(vm.CurrentIndex != videoIndex && vm.CurrentSlide is not null, $"Video übersprungen: {vm.PositionText}");
            if (!vm.IsVideo)
            {
                Check(window.VideoPlayer?.IsPlaying != true, "Video gestoppt");
            }

            // (4) Esc → zu, zurück in der Galerie
            Check(window.HandleKey(Key.Escape), "Esc behandelt");
            await WaitUntilAsync(() => !_slideshow.IsOpen, ShortWait, "Diashow geschlossen").ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
            Check(!Application.Current.Windows.OfType<SlideshowWindow>().Any(), "Diashow-Fenster zerstört");
            Check(_navigation.CurrentTag == NavigationTags.Gallery && vm.CurrentSlide is null, "zurück in der Galerie, ViewModel freigegeben");

            // (5) „Diashow ab hier“ aus der Lightbox
            var image = _gallery.CurrentPage.Skip(1).First(i => !i.IsVideo);
            var lightboxIndex = _gallery.CurrentPage.IndexOf(image);
            _gallery.OpenInLightboxCommand.Execute(image);
            await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
            var lightbox = Application.Current.Windows.OfType<LightboxWindow>().First();
            await WaitUntilAsync(() => lightbox.ViewModel.CurrentIndex == lightboxIndex && !lightbox.ViewModel.IsLoading, ShortWait, "Lightbox-Bild").ConfigureAwait(true);
            lightbox.ViewModel.StartSlideshowCommand.Execute(null);
            await WaitUntilAsync(() => _slideshow.Window is { IsLoaded: true } w && w.ViewModel.CurrentSlide is not null, ShortWait, "Diashow aus der Lightbox").ConfigureAwait(true);
            var fromLightbox = _slideshow.Window!.ViewModel;
            Check(fromLightbox.CurrentIndex == lightboxIndex && fromLightbox.CurrentSlide!.Info.FileId == image.Id,
                $"Diashow ab hier: {fromLightbox.PositionText} = Lightbox {lightbox.ViewModel.CounterText}");
            fromLightbox.Close();
            await WaitUntilAsync(() => !_slideshow.IsOpen, ShortWait, "Diashow aus der Lightbox geschlossen").ConfigureAwait(true);
            Check(_lightbox.IsOpen, "Lightbox bleibt offen");
            lightbox.ViewModel.CloseCommand.Execute(null);
            await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);
        }
        finally
        {
            _slideshow?.Dispose(); // falls ein Prüfpunkt vor dem Schließen scheiterte
            if (changed)
            {
                await _slideshowSettings.SaveAsync(saved).ConfigureAwait(true);
            }
        }
    }
}

using Lumora.Domain.Enums;
using Lumora.Services.Metadata;
using Lumora.Slideshow;

namespace Lumora.Wpf.Tests.Slideshow;

/// <summary>Ablauf der Diashow ohne Fenster (ADR-0059): Reihenfolge, Weiter/Zurück, Pause, Überspringen, Startindex, Videos.</summary>
public sealed class SlideshowViewModelTests
{
    private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

    private static SlideshowHarness Images(int count, int startIndex = 0, SlideshowSettings? settings = null)
    {
        var harness = new SlideshowHarness(Enumerable.Range(1, count).ToList(), startIndex, settings);
        for (var id = 1; id <= count; id++)
        {
            harness.Loader.AddImage(id);
        }

        return harness;
    }

    [Fact]
    public async Task Startet_beim_Startindex_und_laeuft_in_Reihenfolge_der_Ansicht_weiter()
    {
        using var h = Images(5, startIndex: 2);

        await h.ViewModel.StartAsync();
        h.CurrentFileId.Should().Be(3);
        h.ViewModel.PositionText.Should().Be("3 / 5");

        h.Time.Advance(FiveSeconds);
        h.CurrentFileId.Should().Be(4);
        h.Time.Advance(FiveSeconds);
        h.CurrentFileId.Should().Be(5);
        h.Time.Advance(FiveSeconds);
        h.CurrentFileId.Should().Be(1, "am Ende geht es vorn weiter");
        h.Slides.Select(s => s.Info.FileId).Should().Equal(3, 4, 5, 1);
    }

    [Fact]
    public async Task Weiter_und_Zurueck_blaettern_mit_Umlauf_und_Anfang_Ende()
    {
        using var h = Images(4);
        await h.ViewModel.StartAsync();

        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(2);
        await h.ViewModel.PreviousAsync();
        await h.ViewModel.PreviousAsync();
        h.CurrentFileId.Should().Be(4, "vor dem ersten kommt das letzte");
        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(1);
        await h.ViewModel.LastAsync();
        h.CurrentFileId.Should().Be(4);
        await h.ViewModel.FirstAsync();
        h.CurrentFileId.Should().Be(1);
    }

    [Fact]
    public async Task Weiter_setzt_die_Anzeigedauer_neu()
    {
        using var h = Images(3);
        await h.ViewModel.StartAsync();
        h.Time.Advance(TimeSpan.FromSeconds(4));
        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(2);

        h.Time.Advance(TimeSpan.FromSeconds(4));
        h.CurrentFileId.Should().Be(2, "nach dem Blättern beginnt die volle Anzeigedauer");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.CurrentFileId.Should().Be(3);
    }

    [Fact]
    public async Task Pause_haelt_an_und_Weiter_laeuft_mit_der_Restzeit()
    {
        using var h = Images(3);
        await h.ViewModel.StartAsync();
        h.Time.Advance(TimeSpan.FromSeconds(2));

        h.ViewModel.TogglePause();
        h.ViewModel.IsPaused.Should().BeTrue();
        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.CurrentFileId.Should().Be(1, "pausiert bleibt das Bild stehen");
        h.ViewModel.Remaining.Should().Be(TimeSpan.FromSeconds(3));

        h.ViewModel.TogglePause();
        h.Time.Advance(TimeSpan.FromSeconds(2.9));
        h.CurrentFileId.Should().Be(1);
        h.Time.Advance(TimeSpan.FromSeconds(0.2));
        h.CurrentFileId.Should().Be(2);
    }

    [Fact]
    public async Task Blaettern_waehrend_der_Pause_bleibt_pausiert()
    {
        using var h = Images(3);
        await h.ViewModel.StartAsync();
        h.ViewModel.TogglePause();

        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(2);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.CurrentFileId.Should().Be(2);
        h.ViewModel.Remaining.Should().Be(FiveSeconds);
    }

    [Fact]
    public async Task Fehlende_und_kaputte_Dateien_werden_in_Blaetterrichtung_uebersprungen()
    {
        using var h = new SlideshowHarness([1, 2, 3, 4, 5, 6]);
        h.Loader.AddImage(1);
        h.Loader.AddImage(2, missing: true); // im Katalog als fehlend markiert
        var broken = h.Loader.AddImage(3); // Datei weg/kaputt beim Dekodieren
        h.Decoder.Broken.Add(broken.Path);
        h.Loader.AddImage(4);
        // 5 fehlt im Katalog ganz (inzwischen gelöscht)
        h.Loader.Items[6] = new SlideshowItemInfo(6, @"C:\fake\6.zip", MediaType.Archive, false, null, null, []);

        await h.ViewModel.StartAsync();
        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(4, "2 fehlt, 3 ist kaputt");
        h.ViewModel.SkippedCount.Should().Be(2);

        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(1, "5 und das Archiv 6 werden übersprungen, dann Umlauf");
        await h.ViewModel.PreviousAsync();
        h.CurrentFileId.Should().Be(4, "rückwärts genauso");
        h.Slides.Select(s => s.Info.FileId).Should().Equal(1, 4, 1, 4);
    }

    [Fact]
    public async Task Fehlt_der_Startindex_geht_es_mit_der_naechsten_Datei_los()
    {
        using var h = new SlideshowHarness([1, 2, 3], startIndex: 1);
        h.Loader.AddImage(1);
        h.Loader.AddImage(2, missing: true);
        h.Loader.AddImage(3);

        await h.ViewModel.StartAsync();
        h.CurrentFileId.Should().Be(3);
        h.ViewModel.CurrentIndex.Should().Be(2);
    }

    [Fact]
    public async Task Nur_fehlende_Dateien_zeigen_eine_Meldung_statt_endlos_zu_suchen()
    {
        using var h = new SlideshowHarness([1, 2, 3]);
        h.Loader.AddImage(1, missing: true);
        h.Loader.AddImage(2, missing: true);

        await h.ViewModel.StartAsync();

        h.ViewModel.CurrentSlide.Should().BeNull();
        h.ViewModel.StatusText.Should().Contain("Keine Fotos");
        h.ViewModel.IsLoading.Should().BeFalse();
        h.ViewModel.SkippedCount.Should().Be(3);
    }

    [Fact]
    public async Task Ids_und_Infos_kommen_blockweise_nicht_alle_auf_einmal()
    {
        using var h = Images(500);
        await h.ViewModel.StartAsync();
        await h.ViewModel.NextAsync();

        h.Sequence.Requests.Should().OnlyContain(r => r.Count <= SlideshowViewModel.BlockSize);
        h.Sequence.Requests.Should().ContainSingle(r => r.Start == 0);
        h.Loader.MaxBatch.Should().BeLessThanOrEqualTo(SlideshowViewModel.BlockSize);
        h.Loader.Calls.Should().Be(1, "die ersten Folien liegen im selben Block");
    }

    [Fact]
    public async Task Das_naechste_Bild_wird_vorgeladen_und_hoechstens_drei_bleiben_im_Speicher()
    {
        using var h = Images(20);
        await h.ViewModel.StartAsync();

        h.Decoder.Decoded.Should().Equal(@"C:\fake\1.jpg", @"C:\fake\2.jpg");
        for (var i = 0; i < 15; i++)
        {
            await h.ViewModel.NextAsync();
            h.ViewModel.CachedImageCount.Should().BeLessThanOrEqualTo(3);
        }

        h.Decoder.Decoded.Should().HaveCount(17, "jedes Bild genau einmal dekodiert — das vorgeladene wird übernommen");
    }

    [Fact]
    public async Task Videos_laufen_hoechstens_die_eingestellte_Zeit_und_enden_frueher_mit_dem_Video()
    {
        using var h = new SlideshowHarness([1, 2, 3, 4], settings: SlideshowSettings.Default with { MaxVideoSeconds = 30 });
        h.Loader.AddImage(1);
        h.Loader.AddVideo(2); // Länge unbekannt → 30 s
        h.Loader.AddVideo(3, durationMs: 4000); // 4 s + Puffer
        h.Loader.AddImage(4);

        await h.ViewModel.StartAsync();
        h.Time.Advance(FiveSeconds);
        h.CurrentFileId.Should().Be(2);
        h.ViewModel.IsVideo.Should().BeTrue();
        h.Video!.Playing.Should().Be(@"C:\fake\2.mp4");
        h.ViewModel.CurrentSlide!.Duration.Should().Be(TimeSpan.FromSeconds(30));

        h.Time.Advance(TimeSpan.FromSeconds(29));
        h.CurrentFileId.Should().Be(2);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.CurrentFileId.Should().Be(3, "nach höchstens 30 s geht es weiter");
        h.ViewModel.CurrentSlide!.Duration.Should().Be(TimeSpan.FromSeconds(4.5));

        h.Video.RaiseEnded();
        h.CurrentFileId.Should().Be(4, "ein Video, das vorher endet, beendet auch die Folie");
        h.ViewModel.IsVideo.Should().BeFalse();
        h.Video.Playing.Should().BeNull("beim Wechsel zum Bild wird das Video gestoppt");
    }

    [Fact]
    public async Task Weiter_ueberspringt_ein_laufendes_Video()
    {
        using var h = new SlideshowHarness([1, 2, 3]);
        h.Loader.AddImage(1);
        h.Loader.AddVideo(2);
        h.Loader.AddImage(3);

        await h.ViewModel.StartAsync();
        await h.ViewModel.NextAsync();
        h.ViewModel.IsVideo.Should().BeTrue();

        await h.ViewModel.NextAsync();
        h.CurrentFileId.Should().Be(3);
        h.Video!.Playing.Should().BeNull();
    }

    [Fact]
    public async Task Videos_aus_oder_nicht_abspielbar_werden_uebersprungen()
    {
        using var off = new SlideshowHarness([1, 2, 3], settings: SlideshowSettings.Default with { PlayVideos = false });
        off.Loader.AddImage(1);
        off.Loader.AddVideo(2);
        off.Loader.AddImage(3);
        await off.ViewModel.StartAsync();
        await off.ViewModel.NextAsync();
        off.CurrentFileId.Should().Be(3);
        off.Video!.Playing.Should().BeNull();

        using var broken = new SlideshowHarness([1, 2, 3]);
        broken.Loader.AddImage(1);
        broken.Video!.Broken.Add(broken.Loader.AddVideo(2).Path);
        broken.Loader.AddImage(3);
        await broken.ViewModel.StartAsync();
        await broken.ViewModel.NextAsync();
        broken.CurrentFileId.Should().Be(3);
    }

    [Fact]
    public async Task Pause_pausiert_auch_das_Video()
    {
        using var h = new SlideshowHarness([1]);
        h.Loader.AddVideo(1);
        await h.ViewModel.StartAsync();

        h.ViewModel.TogglePause();
        h.Video!.IsPaused.Should().BeTrue();
        h.Video.RaiseEnded();
        h.CurrentFileId.Should().Be(1, "pausiert blättert auch ein Video-Ende nicht weiter");
        h.ViewModel.TogglePause();
        h.Video.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task Einblendung_zeigt_Datum_und_Ort_und_laesst_sich_abschalten()
    {
        using var h = new SlideshowHarness([1, 2]);
        var date = CaptureDateValue.FromWallClock(new DateTime(2025, 7, 12, 10, 30, 0), 120, CaptureDateSource.Exif, CaptureDatePrecision.Exact);
        h.Loader.Items[1] = new SlideshowItemInfo(1, @"C:\fake\1.jpg", MediaType.Image, false, date, "Grado, Italien", []);
        h.Loader.Items[2] = new SlideshowItemInfo(2, @"C:\fake\2.jpg", MediaType.Image, false, null, null, []);

        await h.ViewModel.StartAsync();
        h.ViewModel.DateText.Should().Be("Samstag, 12. Juli 2025");
        h.ViewModel.PlaceText.Should().Be("Grado, Italien");
        h.ViewModel.IsCaptionVisible.Should().BeTrue();

        h.ViewModel.ShowCaption = false;
        h.ViewModel.IsCaptionVisible.Should().BeFalse();
        h.ViewModel.ShowCaption = true;

        await h.ViewModel.NextAsync();
        h.ViewModel.IsCaptionVisible.Should().BeFalse("ohne Datum und Ort bleibt die Einblendung leer");
    }

    [Fact]
    public async Task Zoom_zielt_auf_Gesichter_wechselt_die_Richtung_und_ist_abschaltbar()
    {
        using var h = new SlideshowHarness([1, 2, 3]);
        h.Loader.AddImage(1, faces: [new SlideshowFaceBox(0.47, 0.42, 0.1, 0.1)]);
        h.Loader.AddImage(2);
        h.Loader.AddImage(3);

        await h.ViewModel.StartAsync();
        var first = h.ViewModel.CurrentSlide!.Plan;
        first.Start.Should().Be(SlideshowViewRect.Full, "die erste Folie zoomt hinein");
        (first.End.X + (first.End.Size / 2)).Should().BeApproximately(0.52, 1e-9, "Ziel = Mitte des Gesichts");
        (first.End.Y + (first.End.Size / 2)).Should().BeApproximately(0.47, 1e-9);

        await h.ViewModel.NextAsync();
        h.ViewModel.CurrentSlide!.Plan.End.Should().Be(SlideshowViewRect.Full, "die zweite zoomt heraus");

        h.ViewModel.KenBurnsEnabled = false;
        await h.ViewModel.NextAsync();
        h.ViewModel.CurrentSlide!.Plan.Should().Be(KenBurnsPlan.None);
    }

    [Fact]
    public async Task Leiste_wird_gespeichert_und_die_Anzeigedauer_gilt_ab_der_naechsten_Folie()
    {
        var store = new SlideshowSettingsStore(new MemoryViewSettingsStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SlideshowSettingsStore>.Instance);
        using var h = new SlideshowHarness([1, 2, 3], store: store);
        h.Loader.AddImage(1);
        h.Loader.AddImage(2);
        h.Loader.AddImage(3);
        await h.ViewModel.StartAsync();

        h.ViewModel.IsDuration8 = true;
        h.ViewModel.KenBurnsEnabled = false;
        h.ViewModel.SlideSeconds.Should().Be(8);
        h.ViewModel.IsDuration5.Should().BeFalse();
        h.Time.Advance(FiveSeconds);
        h.CurrentFileId.Should().Be(2);
        h.Time.Advance(TimeSpan.FromSeconds(7.9));
        h.CurrentFileId.Should().Be(2);
        h.Time.Advance(TimeSpan.FromSeconds(0.2));
        h.CurrentFileId.Should().Be(3);

        await h.ViewModel.FlushPersistAsync();
        var saved = await store.LoadAsync();
        saved.SlideSeconds.Should().Be(8);
        saved.KenBurns.Should().BeFalse();
    }

    [Fact]
    public async Task Leiste_ueberschreibt_keine_inzwischen_geaenderten_Einstellungen()
    {
        var store = new SlideshowSettingsStore(new MemoryViewSettingsStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SlideshowSettingsStore>.Instance);
        using var h = new SlideshowHarness([1], store: store);
        h.Loader.AddImage(1);
        await h.ViewModel.StartAsync();

        // während die Diashow läuft, in den Einstellungen geändert
        await store.SaveAsync(SlideshowSettings.Default with { Order = SlideshowOrder.Random, PlayVideos = false });
        h.ViewModel.IsDuration3 = true;
        await h.ViewModel.FlushPersistAsync();

        var saved = await store.LoadAsync();
        saved.SlideSeconds.Should().Be(3);
        saved.Order.Should().Be(SlideshowOrder.Random);
        saved.PlayVideos.Should().BeFalse();
    }

    [Fact]
    public async Task Endet_das_Video_waehrend_der_Pause_geht_es_beim_Fortsetzen_sofort_weiter()
    {
        using var h = new SlideshowHarness([1, 2]);
        h.Loader.AddVideo(1);
        h.Loader.AddImage(2);
        await h.ViewModel.StartAsync();

        h.ViewModel.TogglePause();
        h.Video!.RaiseEnded();
        h.CurrentFileId.Should().Be(1);

        h.ViewModel.TogglePause();
        h.CurrentFileId.Should().Be(2, "nicht bis zum Ablauf der Restzeit schwarz stehen bleiben");
    }

    [Fact]
    public async Task Leere_Reihenfolge_zeigt_eine_Meldung()
    {
        using var h = new SlideshowHarness([]);
        await h.ViewModel.StartAsync();
        h.ViewModel.StatusText.Should().NotBeNullOrEmpty();
        await h.ViewModel.NextAsync();
        h.ViewModel.CurrentSlide.Should().BeNull();
    }

    [Fact]
    public async Task Nach_dem_Schliessen_laeuft_kein_Zeitgeber_mehr()
    {
        using var h = Images(3);
        await h.ViewModel.StartAsync();
        h.ViewModel.Dispose();

        h.Time.ActiveTimers.Should().Be(0);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Slides.Should().HaveCount(1);
    }

    [Fact]
    public void Videodauer_ist_Laenge_plus_Puffer_hoechstens_die_Grenze()
    {
        SlideshowViewModel.VideoDuration(null, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
        SlideshowViewModel.VideoDuration(10_000, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(10.5));
        SlideshowViewModel.VideoDuration(120_000, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
    }
}

# ADR-0059: Diashow für beliebige Dateilisten — Überblendung, langsamer Zoom auf Gesichter, Datum/Ort, ohne Musik

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** Mockup [`09-storys.html`](../../mockups/09-storys.html) (Ansicht „Diashow“, Einstellungen › Storys,
  Karte „Diashow“), [ADR-0007](0007-thumbnail-scheduling.md) (Prioritäten, Lightbox „Critical“), [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md)
  und [ADR-0038](0038-personen-in-galerie-und-lightbox.md) (Gesichtsrahmen), [ADR-0046](0046-aufnahmedatum.md)
  (Aufnahmedatum mit Präzision/Abstand), [ADR-0050](0050-orte.md) (Ortsnamen offline), [ADR-0049](0049-versteckte-bibliotheken.md)
  (versteckte Bibliothek), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (Alben)

## Kontext

Welle 6 „Storys“ braucht eine Diashow; Herbert will sie auch unabhängig von Storys für Galerie, Alben und Lightbox —
**ohne Musik**. Abgenommen im Mockup 09: eigenes Vollbild, Überblendung, langsamer Zoom auf Gesichter, Datum und Ort
unten links, Bedienleiste unten mittig, die sich ausblendet, Fortschrittsbalken; in den Einstellungen Anzeigedauer, Zoom,
Videos (mit Ton, höchstens 30 s), Datum/Ort. Die ganze Galerie-Ansicht kann 500 000 Treffer haben, Fotos sind bis 4K und
größer, und die Diashow kann stundenlang laufen — Speicher darf nicht wachsen. Keine neuen Pakete, keine Migration.

## Entscheidung

1. **Schnittstelle** (Wpf-Schicht, `Slideshow/ISlideshowService.cs`): `Start(IReadOnlyList<int> fileIds, int startIndex = 0,
   string? title = null)` und `Start(ISlideshowSequence, …)`. `ISlideshowSequence` liefert Ids **blockweise**
   (`GetRangeAsync(start, count)`): `ListSlideshowSequence` (Auswahl, Album, Lightbox), `PagedSlideshowSequence` (ganze
   Galerie-Ansicht: dieselbe Galerie-SQL `GalleryPageSource.LoadPageAsync`, Abfrage und Trefferzahl beim Start
   eingefroren, Seiten zu 200, vier Seiten vorgehalten), `ShuffledSlideshowSequence` (Einstellung „zufällig“: Permutation,
   Startbild zuerst). Fehlende Einträge (Id ≤ 0, kürzere Seite) überspringt die Diashow. Ein zweiter Start ersetzt die
   laufende Diashow.
2. **Daten-Helfer** `ISlideshowItemLoader` (`SlideshowItemLoader`, EF, Blöcke ≤ 500 Ids): Pfad, Art, `Missing`,
   Aufnahmedatum als `CaptureDateValue` (Präzision + Abstand), Ortsname über `IPlaceLookup` („Grado, Italien“), Videolänge,
   Gesichtsrahmen aller **nicht ignorierten** Gesichter (bestätigt, vorgeschlagen, erkannt). Das ViewModel fragt je Block
   von 50 Positionen einmal Ids und einmal Infos ab und hält sechs Blöcke vor.
3. **Ablauf** im `SlideshowViewModel` (ohne Fenster testbar): Weiter/Zurück mit Umlauf (nach dem letzten das erste),
   Pos1/Ende, Pause mit Restzeit, Überspringen in Blätterrichtung (fehlend, im Katalog weg, Archiv, Dekodierfehler, Video
   ohne Wiedergabe) — höchstens einmal rundum, dann Meldung „Keine Fotos oder Videos …“. Zeitgeber über `TimeProvider`,
   Rückrufe per `post` auf den UI-Thread. Leiste und Mauszeiger blenden nach **2 s** ohne Bewegung aus, über der Leiste
   nicht; das Fenster zählt nur echte Mausbewegung (≥ 2 px), weil WPF auch bei Layoutwechseln unter dem ruhenden Zeiger
   `MouseMove` meldet.
4. **Bilder:** `SlideshowImageDecoder` nutzt den Lightbox-Decoder (eigener FileStream, `BitmapCacheOption.OnLoad`,
   `Freeze`, EXIF-Orientierung) mit einer Dekodier-Breite, bei der das **eingepasste** Bild die Fläche Bildschirm ×
   Skalierung × 1,12 (Zoom-Reserve) gerade füllt, nie größer als die Quelle. Vorgeladen wird die nächste Datei in
   Blätterrichtung; höchstens **drei** dekodierte Bilder (vorheriges, aktuelles, nächstes) liegen im Speicher. Nie
   `ImageSource` in Services.
5. **Überblendung und langsamer Zoom** (Fenster-Code): zwei `Image`-Ebenen, die neue blendet in 1 s ein, die alte aus und
   gibt danach ihre Bitmap frei. Ken Burns: `KenBurnsPlanner` legt einen Endausschnitt der Größe 1/1,12 um den
   **Mittelpunkt des Rechtecks aller Gesichter** (ohne Gesichter: Bildmitte) und schiebt ihn ins Bild; abwechselnd hinein
   und heraus. Das Fenster animiert Skalierung und Verschiebung **linear und gleichlaufend** — damit liegt der sichtbare
   Ausschnitt zu jedem Zeitpunkt im Bild (keine schwarzen Ränder, im Test für t = 0 … 1 geprüft). Pause hält Zoom und
   Fortschrittsbalken an und setzt sie mit der Restzeit fort. Abschaltbar in Leiste und Einstellungen.
6. **Videos** über den `LibVlcHost` der Lightbox (`SlideshowVideoPlayer`, ein MediaPlayer je Diashow-Fenster, mit Ton in
   der Lautstärke der Lightbox): Folie = Videolänge + 0,5 s, höchstens Einstellung (Standard 30 s); Ende oder Fehler →
   weiter. Keine Überblendung zum/vom Video (HwndHost, Airspace); das Overlay liegt deshalb doppelt vor — über den Bildern
   und als `VideoView.Content`. „Videos abspielen“ aus → Videos werden übersprungen. Pause wirkt auch auf ein gerade
   startendes Video (nachgeholt beim LibVLC-Ereignis „Playing“, auf dem Thread-Pool); endet ein Video während der Pause,
   geht es beim Fortsetzen sofort weiter.
7. **Fenster** `SlideshowWindow : AppFluentWindow` (Hwnd-Null-Lehre 0.2.13): `WindowStyle=None`, schwarz, Ecken nicht
   gerundet, Größe = ganzer Monitor des **Hauptfensters** (inkl. Taskleiste, DIP über dessen Transformation), Besitzer =
   Hauptfenster. Tasten: ←/→, Leertaste, Pos1/Ende, Esc beendet, F/F11 ohne Wirkung (Vollbild bleibt). Solange es offen
   ist, verhindert `SetThreadExecutionState` Bildschirmschoner und Abschalten (wie ein Videoplayer).
8. **Einblendung:** Datum sachlich als Wanduhrzeit der Aufnahme — genau/Tag „Samstag, 12. Juli 2025“, Monat „Juli 2025“,
   Jahr „2025“ (nur so genau wie bekannt, ohne „ca.“); Ort darunter, leer → nur Datum; unbekanntes Datum → nur Ort.
9. **Einstiege:** Galerie-Leiste „Diashow“ und Kontextmenü „Diashow ab hier“ — ab zwei ausgewählten Dateien die Auswahl
   (Seitenreihenfolge), sonst die ganze Ansicht ab der angeklickten bzw. einzigen ausgewählten Datei (sonst ab dem
   Anfang); Seite „Alben“ (Rechtsklick „Diashow“, `IAlbumService.GetFileIdsAsync`, Titel = Albumname); Lightbox
   „Diashow ab hier“ (Knopf, Taste **S**; stoppt das Lightbox-Video, die Lightbox bleibt offen).
10. **Einstellungen** `slideshow.*` in der Settings-Tabelle des Katalogs (`SlideshowSettingsStore`): `slideSeconds` (3/5/8),
    `kenBurns`, `playVideos`, `maxVideoSeconds` (5–600), `showCaption`, `order` (`AsView`/`Random`). Abschnitt
    „Diashow“ vorerst unter Einstellungen › Darstellung (`SettingsSlideshowSection`, eigenes ViewModel); die Story-Seite
    verschiebt ihn nach „Storys“. Änderungen über die Leiste (Dauer, Zoom, Datum & Ort) werden ebenfalls gespeichert.
11. **Versteckte Bibliothek:** die Diashow liest nur den Katalog der Sitzung, schreibt keine Dateien, ruft keine Shell auf
    und protokolliert Pfade nur auf Debug-Ebene (Ids auf Info) — keine Spuren außerhalb.

## Begründung

Eine Diashow über Ids statt über Galerie-Items macht sie für alle Quellen (Storys eingeschlossen) gleich. Blockweise Ids
halten die ganze Ansicht billig: 500 000 Treffer kosten nur die Seiten, die tatsächlich gezeigt werden. Auf Bildschirmgröße
dekodieren und drei Bilder vorhalten ist die Lehre aus der Lightbox (ADR-0007) — groß genug für den Zoom, klein genug für
lange Läufe. Lineare, gleichlaufende Skalierung/Verschiebung ist die einfachste Animation, für die „bleibt im Bild“ für
jeden Zeitpunkt beweisbar ist. Der Gesichter-Mittelpunkt nutzt vorhandene Daten ohne neue Analyse.

## Konsequenzen

### Positiv
- Eine Diashow für Galerie, Alben, Lightbox und später Storys; Storys rufen nur `ISlideshowService.Start` auf.
- Speicher stabil: Messung (Wpf-Test `Speicher_bleibt_ueber_200_dekodierte_Bilder_stabil`, Fenster 1280 × 720,
  3000 × 2000-JPEGs, dekodiert 1211 × 807): 200 Bilder in ≈ 20 ms je Bild dekodiert, verwalteter Speicher 15,6 → 15,6 MB,
  privater Speicher 116,6 → 124,6 MB (+8 MB über 200 Bilder ohne GC-Zwang zwischendurch), höchstens 3 Bilder im Cache.
- Kein Ruckeln beim Wechsel: das nächste Bild ist dekodiert, bevor die Folie wechselt (Prefetch beim Anzeigen).

### Negativ
- Zum und vom Video gibt es keine Überblendung (Airspace des HwndHost).
- Ein Bibliothekswechsel während einer Diashow über die ganze Ansicht lässt die nächste Seite fehlschlagen → Meldung
  statt weiter (Abfrage beim Start eingefroren).
- Zufällige Reihenfolge über eine Galerie-Ansicht holt je Folie eine einzelne Zeile an ihrer Stelle (`LIMIT 1 OFFSET n`
  mit dem Rückwärts-Trick der Galerie) statt einer Seite — 50 kleine Abfragen je Block; die Permutation belegt 4 Byte je
  Treffer (2 MB bei 500 000).

### Neutral
- Umlauf am Ende (wie im Mockup) statt Stopp; Esc beendet.
- Optionale Konstruktor-Parameter (`ISlideshowService? slideshow = null`) in `GalleryViewModel`, `AlbumsViewModel`,
  `LightboxViewModel` und `SettingsPage`; ohne Dienst sind Knöpfe ausgeblendet.
- Rauchtest-Schritt „Diashow“ vor „Versteckte Bibliothek“.

## Alternativen

- **Diashow in der Lightbox** (Autoplay-Modus) → verworfen: Lightbox ist Bearbeiten/Ansehen mit Toolbar und Info-Panel;
  Überblendung, Zoom und Einblendung bräuchten einen zweiten Darstellungsweg im selben Fenster.
- **Alle Ids der Ansicht auf einmal laden** → verworfen: 500 000 Ids und DTOs vor dem ersten Bild.
- **Zoom per Storyboard über einen Anteil t mit Rechnung je Frame** (CompositionTarget.Rendering) → verworfen: mehr Code,
  dieselbe Wirkung; die linearen DoubleAnimations laufen auf dem Render-Thread.
- **Musik** → ausdrücklich nicht gewünscht (Herbert, Mockup 09).

## Referenzen

- Code: `src/Lumora.Wpf/Slideshow/` (Dienst, ViewModel, Fenster, Overlay, Planer, Decoder, Video, Einstellungen),
  `Gallery/ViewModels/GalleryViewModel.Slideshow.cs`, `Gallery/Views/GalleryView.Slideshow.cs`,
  `Lightbox/LightboxViewModel.Slideshow.cs`, `Shell/Smoke/SmokeScenario.Slideshow.cs`
- Tests: `tests/Lumora.Wpf.Tests/Slideshow/`

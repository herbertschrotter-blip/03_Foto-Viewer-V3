# ADR-0043: Vorschaubilder nach kurzer Kante (400 px), passend zur Kachel geladen; Bildmaße im Katalog

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0007](0007-thumbnail-scheduling.md), [ADR-0009](0009-cache-strategie.md) (Größen „160 / 320 / 640“
  teilweise ersetzt), [ADR-0025](0025-scan-sperre-fuer-datei-operationen.md), [ADR-0026](0026-speicherort-katalog-waehlbar.md),
  [Spike #11](../spikes/11-vorschaubild-groesse.md)

## Kontext

„In der Kachelansicht ist schon alles verschwommen.“ Vorschaubilder hatten die LANGE Kante 320 px (Querformat 320 ×
180…240, JPEG-Qualität 80), und die Galerie dekodierte sie fest auf 180 px Breite. Die Kachel (Regler 120–320 DIP,
`UniformToFill`) bei 125–150 % Bildschirm-Skalierung zog ein 180 × 120-Bild auf bis zu 480 px hoch. Außerdem waren
`Files.Width/Height` nie gefüllt — die geplante Collage-Ansicht braucht die Seitenverhältnisse. Kataloge mit
500 000 Dateien haben bereits 320-px-Vorschaubilder; ein Übergang darf den Start nicht blockieren.

## Entscheidung

1. **Größe:** kurze Kante **400 px**, lange höchstens **1200 px** (3×), nie hochskalieren, EXIF-Drehung wie bisher,
   JPEG-Qualität **82** (`ThumbnailSizing`). Gilt für WIC und FFmpeg (Video-Standbild, Bild-Fallback) gleich. Dateiname
   `{fileId}_400_still.jpg`, `Thumbnails.Size = 400`.
2. **Laden in der Galerie:** Dekodierfläche = Bildbereich der Kachel (Regler minus Rand und Namenszeile, DIP) ×
   Skalierung des Hauptfensters (`VisualTreeHelper.GetDpi`, `DpiChanged`); dekodiert wird die kleinste Breite, bei der
   das Bild die Fläche bedeckt.
   Nach dem Vergrößern des Reglers bzw. einem Wechsel auf höhere Skalierung werden nur Kacheln neu dekodiert, deren
   Bild um mehr als 15 % zu klein ist (entprellt, sichtbare zuerst, gebündelt mit Background-Priorität).
3. **Übergang:** Vorschaubilder älterer Größe (`ThumbnailSizing.LegacySizes` = 320) werden sofort gezeigt, das neue
   entsteht in der normalen Warteschlange; nach Erfolg löscht dieselbe Transaktion die alte Zeile, die Datei folgt nach
   dem Commit. Scheitert das neue, bleibt das alte sichtbar. Eine **Pflege im Hintergrund** (einmal je Sitzung,
   20 s nach dem ersten Request) reiht die übrigen in Stapeln zu 32 nach FileId ein — jeden Stapel erst bei leerer
   Warteschlange (angeforderte, sichtbare Kacheln gehen vor), nicht während Pause/Umzug oder Scan-Sperre der
   Bibliothek, nicht für fehlende Originale und Cloud-Platzhalter. Die noch nicht eingereihten zählen in
   `PendingCount` (Statusleiste „Vorschaubilder: N in Warteschlange“).
4. **Bildmaße:** beim Erzeugen schreibt der ThumbnailService die Maße des Originals **nach EXIF-Drehung** (wie
   angezeigt) nach `Files.Width/Height` — in derselben Transaktion wie den Status „Generated“ (keine zusätzliche
   Transaktion je Datei). Videos: aus der Eingabe-Beschreibung von FFmpeg (`-loglevel info`, Display-Matrix/`rotate`).
   Die Pflege trägt für Bilder mit neuem Vorschaubild, aber ohne Maße, die Maße aus dem **Dateikopf** nach
   (`BitmapDecoder` mit `DelayCreation`, kein Pixel-Decode), je Stapel von 200 eine Transaktion; nicht lesbare bekommen
   **0 × 0** („nicht lesbar“, kein erneuter Versuch je Sitzung). Geschrieben wird nur für die gelesene Dateiversion
   (`Files.ModifiedAtUtc` = Zeitstempel des Auftrags). „Neu aufbauen“ legt die
   Files-Zeilen neu an und löscht die Vorschaubilder; die Maße werden beim Neu-Erzeugen wieder gefüllt.
   Keine Migration (Spalten bestanden seit V0.1).

## Begründung

- Spike #11 (200 Handyfotos): die Vorgabe 480/Q85 kostet 8,3 GB je 100 000 Fotos, 400/Q82 **5,5 GB** (Grenze ≈ 6 GB,
  alt 1,3 GB); Erzeugung +20–30 % Zeit. 400 px decken jede Kachelgröße bis 125 % Skalierung; nur die zwei größten
  Stufen bei 150 % vergrößern Hochformate noch um höchstens 1,14 (vorher 1,4–2 schon bei der Standardgröße, bis 2,5).
- Kurze statt lange Kante: `UniformToFill` füllt die Kachel über die kurze Seite des Bildes; mit „lange Kante = Ziel“
  waren Querformate in der Höhe immer zu klein. Die Begrenzung der langen Kante hält Panoramen klein.
- Dekodieren nach Kachel × Skalierung statt fest: schärfer bei großer Kachel/hoher Skalierung, sparsam bei kleiner
  Kachel. Nur bei deutlich größerer Fläche neu dekodieren vermeidet Ruckeln beim Ziehen des Reglers.
- Alte Größe zeigen statt verwerfen: kein leerer Platzhalter nach dem Update. Pflege nur bei leerer Warteschlange,
  in kleinen Stapeln und über den Primärschlüssel (Index-Bereich ab Cursor) — beim Start wird nur gezählt, nichts
  auf einmal eingereiht (ADR-0007: Background in Stapeln). Cloud-Platzhalter auszulassen verhindert, dass die Pflege
  einen OneDrive-Ordner herunterlädt.
- Maße in derselben Transaktion wie der Status: kein zusätzlicher Schreibvorgang, kein Zustand „Vorschaubild da, Maße
  fehlen“ außer bei Renderern ohne Maße (FFmpeg ohne Eingabe-Beschreibung) und Bestandsdateien — die holt die Pflege.

## Konsequenzen

### Positiv
- Scharfe Kacheln in allen Größen bis 125 % Skalierung, bei 150 % bis Reglerstufe 280.
- `Files.Width/Height` für alle Bilder und Videos mit Vorschaubild — Grundlage für Collage und Seitenverhältnis-Filter.
- Bestehende Kataloge wechseln ohne Leerlauf und ohne Hänger beim Start.

### Negativ
- Cache ≈ 4× größer: ≈ 5,5 GB je 100 000 Fotos, ≈ 27 GB bei 500 000 (vorher ≈ 1,3 / 6,7 GB).
- Erzeugung ≈ 20–30 % langsamer je Bild; das Ersetzen von 500 000 alten Vorschaubildern dauert im Hintergrund
  ≈ 2,5 h (niedrige Thread-Priorität, unterbrochen von jeder Galerie-Anforderung).
- Galerie-Speicher: Kachelbilder bei 150 % ≈ 2× größer (Seite mit 500 Kacheln ≈ 90–160 MB statt 50–85 MB); bei 100 %
  kleiner als bisher, weil nur der Bildbereich zählt.
- FFmpeg läuft mit `-loglevel info`; die Fehlermeldung (letzte Zeilen von stderr) blendet eingerückte Beschreibungszeilen aus.

### Neutral
- Reglerstufen 300/320 bei 150 %: Hochformate bis 1,14-fach vergrößert (bewusst, siehe Spike #11).
- Der Bildbereich ist aus der Kachelvorlage abgeleitet (`ThumbnailDecodeBox.TileChromeWidth/Height`: Rand 16 DIP,
  Namenszeile knapp 32 DIP); ändert sich die Vorlage, die Konstanten anpassen.
- Ein verspätet dekodiertes Bild älterer Größe ersetzt in der Kachel nie ein schon gezeigtes neues (Dateiname).
- Die Pflege läuft einmal je Sitzung; übersprungene (fehlend, nur online) bleiben bei der alten Größe, bis sie angesehen werden.
- Gesichts-Ausschnitte der Personen-Seite (128 px, ADR-0037) sind nicht Teil dieser Entscheidung.

## Alternativen

- **480 px / Q85 (Vorgabe):** schärfer bei 150 % und größter Kachel → verworfen: 8,3 GB je 100 000 (> 6 GB).
- **480 px mit niedrigerer Qualität:** Q82 7,5 GB, Q75 sogar größer (WPF-Encoder) → verworfen.
- **Zwei Größen (klein für Kacheln, groß für große Kacheln/Collage):** doppelte Erzeugung und Verwaltung → verworfen,
  solange eine Größe reicht.
- **Alle alten Vorschaubilder beim Start löschen und neu erzeugen:** leere Kacheln, Startlast → verworfen.
- **Maße beim Scan lesen:** jeder Scan läse 500 000 Dateiköpfe (OneDrive-Platzhalter würden geladen) → verworfen;
  Maße entstehen dort, wo die Datei ohnehin gelesen wird.
- **Maße über „Neu aufbauen“ mitnehmen (Carry-over):** verlängert die eine Schreibtransaktion (ADR-0028) für alle
  Dateien → verworfen; die Vorschaubilder entstehen danach ohnehin neu.

## Referenzen

- `src/PhotoViewerV3.Services/Thumbnails/ThumbnailSizing.cs`, `ThumbnailService.cs`, `ThumbnailService.Maintenance.cs`,
  `FfmpegStreamInfo.cs`, `FfmpegVideoThumbnailer.cs`
- `src/PhotoViewerV3.Wpf/Thumbnails/WicImageThumbnailRenderer.cs`,
  `src/PhotoViewerV3.Wpf/Gallery/ViewModels/GalleryViewModel.ThumbnailDecode.cs`
- Tests: `tests/PhotoViewerV3.Tests/Thumbnails/ThumbnailUpgradeTests.cs`, `ThumbnailSizingTests.cs`,
  `tests/PhotoViewerV3.Wpf.Tests/Gallery/GalleryThumbnailDecodeTests.cs`; Rauchtest-Schritt „Vorschaubild-Größe und Bildmaße“

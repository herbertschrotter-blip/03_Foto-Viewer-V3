# Spike #11 — Vorschaubild-Größe: Schärfe gegen Speicherbedarf

- **Status:** 🟢 Entschieden — kurze Kante 400 px, lange höchstens 1200 px, JPEG-Qualität 82
- **Datum:** 2026-10-21 (v0.2.45)
- **Bezogen auf:** [ADR-0043](../adr/0043-vorschaubilder-kurze-kante-und-bildmasse.md), PLAN Kap. 13,
  Harness `tests/PhotoViewerV3.PerfTests/Spikes/Wic/Spike11ThumbnailSize.cs`

## Frage

Herbert: „In der Kachelansicht ist schon alles verschwommen.“ Ursache: Vorschaubilder hatten die **lange** Kante 320 px
(Querformat 320 × 180…240), die Galerie dekodierte sie zusätzlich fest auf **180 px Breite** — die quadratnahe Kachel
(Regler 120–320 DIP) bei 150 % Bildschirm-Skalierung zog ein 180 × 120-Bild auf bis zu 480 px hoch (3–4-fach).
Wie groß müssen die Vorschaubilder werden, und was kostet das an Platz und Zeit bei 100 000 bzw. 500 000 Fotos?
Vorgabe: kurze Kante 480 px, lange höchstens 1440 px, Qualität 85 — über ≈ 6 GB je 100 000 Fotos ein begründeter
Kompromiss.

## Messaufbau

- 200 Fotos aus Herberts Handy-Katalog (gleichmäßige Stichprobe, jede n-te von 19 556 lokalen JPEGs, Ø 5,1 MB je Datei;
  54 mit EXIF-Orientierung 5–8). Nur gelesen; Vorschaubilder nummeriert in einen eigenen Temp-Ordner; ausgegeben werden
  nur zusammengefasste Zahlen. Cloud-Platzhalter werden übersprungen.
- Gemessen je Datei und Variante: Dateikopf lesen, auf Zielgröße dekodieren (WIC-Skalierung), EXIF-Drehung, JPEG
  schreiben — derselbe Weg wie `WicImageThumbnailRenderer`. Vorher ein Aufwärm-Lesen (Dateicache), Reihenfolge der
  Varianten je Datei abwechselnd. Ein Thread; Hochrechnung auf 4 Worker.
- Aufruf: `dotnet run -c Release --project tests/PhotoViewerV3.PerfTests -- spike11-thumb-size --dir <Ordner> --out <Temp>`.
- Rechner: Herberts PC (i7-8700K, 6 Kerne / 12 Threads, 32 GB). Während der Messung liefen andere Builds — Zeiten
  schwanken je Lauf um ± 20 %; die Größen sind exakt reproduzierbar.

## Ergebnis (Lauf 2 von 3; Zeiten der anderen Läufe in der letzten Spalte)

| Variante | Ø KB | Median KB | p95 KB | Ø Pixel | GB je 100k | GB je 500k | Ø ms je Bild | Ø ms (Läufe 1/3) |
|---|---|---|---|---|---|---|---|---|
| **alt**: lange Kante 320, Q80 | 14,0 | 13,6 | 21,3 | 61 136 | 1,34 | 6,69 | 53,7 | 57,4 / 61,4 |
| kurz 480 (max 1440), Q85 — Vorgabe | 87,1 | 83,4 | 155,4 | 393 499 | **8,31** | 41,55 | 66,8 | 72,4 / 80,7 |
| kurz 480 (max 1440), Q82 | 78,7 | 75,4 | 140,6 | 393 499 | 7,51 | 37,55 | 67,8 | 71,8 / 80,3 |
| kurz 480 (max 1440), Q75 | 109,6 | 105,3 | 193,3 | 393 499 | 10,45 | 52,26 | 66,8 | – / 80,8 |
| kurz 480 (max 960), Q80 | 72,2 | 69,2 | 132,0 | 382 687 | 6,89 | 34,44 | 67,1 | – / 80,0 |
| kurz 440 (max 1320), Q80 | 63,6 | 60,8 | 112,9 | 330 676 | 6,06 | 30,31 | 73,0 | – / 80,1 |
| kurz 400 (max 1200), Q85 | 63,3 | 61,3 | 111,6 | 273 319 | 6,04 | 30,19 | 65,4 | – / 78,5 |
| **kurz 400 (max 1200), Q82 — gewählt** | **57,2** | 55,4 | 101,2 | 273 319 | **5,46** | **27,29** | 66,6 | 68,6 / 81,6 |

Erzeugungszeit hochgerechnet (4 Bild-Worker, Durchsatz ≈ 4 × 1/67 ms): **alt ≈ 22–26 min je 100 000, neu ≈ 28–34 min
je 100 000** (500 000: ≈ 2,3–2,8 h statt 1,9–2,1 h). Der Mehraufwand (+ 20–30 %) steckt im größeren JPEG-Schreiben;
Lesen und Dekodieren (WIC skaliert JPEG beim Dekodieren) dominieren weiter.

Auffällig: Der WPF-JPEG-Encoder liefert bei Qualität 75 **größere** Dateien als bei 80–85 (in allen Läufen) —
Qualität unter 80 ist also kein Sparhebel.

## Bewertung

- Die Vorgabe (480/Q85) kostet **8,3 GB je 100 000** — über der Grenze von ≈ 6 GB. Qualität senken hilft kaum
  (Q82: 7,5 GB), die lange Kante auf 2× zu begrenzen ebenso wenig (6,9 GB), weil Handyfotos meist 4:3/16:9 sind.
- **400 px / Q82: 5,5 GB je 100 000** (27 GB je 500 000), Faktor 4,1 gegenüber alt; sichtbar so scharf wie 480 bei
  allen Kachelgrößen bis 125 % Skalierung.
- Was 400 px abdeckt — Bildbereich der Kachel (Regler minus 16 DIP Rand; Höhe ≈ 0,65 × Breite) × Skalierung;
  „UniformToFill“ braucht beim Hochformat die Breite, beim Querformat die Höhe des Bildbereichs:

  | Kachel (DIP) | Bildbereich | 100 % | 125 % | 150 % |
  |---|---|---|---|---|
  | 180 (Standard) | 164 × 112 | 164 px ✓ | 205 px ✓ | 246 px ✓ |
  | 240 | 224 × 150 | 224 px ✓ | 280 px ✓ | 336 px ✓ |
  | 320 (größte) | 304 × 202 | 304 px ✓ | 380 px ✓ | 456 px — Hochformat 1,14-fach vergrößert |

  Nur die beiden größten Reglerstufen (300, 320) bei 150 % vergrößern ein Hochformat (höchstens Faktor 1,14;
  Querformat reicht: Bildhöhe 303 px < 400). Vorher waren es bei der Standardkachel und 150 % schon die Faktoren 1,4
  (Hochformat) bzw. 1,4–2 (Querformat 180 × 120 auf 168 Höhe), bei der größten Kachel bis 2,5.

## Galerie: Dekodiergröße und Speicher

- Neu dekodiert die Kachel auf **Bildbereich × Bildschirm-Skalierung** (`ThumbnailDecodeBox`), so groß, dass die Fläche
  bedeckt ist, nie größer als das Vorschaubild. Bisher fest 180 px Breite.
- Speicher je Kachel (32 bit/Pixel), Vorschaubild 4:3: Standardkachel bei 100 % ≈ 80 KB (Querformat) / 144 KB (Hochformat)
  — weniger als bisher (97 / 173 KB); bei 150 % ≈ 182 / 323 KB. Eine Seite mit 500 Kacheln bei 150 % ≈ 90–160 MB
  (bisher ≈ 50–85 MB, aber unscharf). Größte Kachel bei 150 %: ≈ 0,6–0,85 MB je Kachel.
- Regler größer ziehen oder Wechsel auf einen Bildschirm mit höherer Skalierung: nur Kacheln, deren Bild für die neue
  Fläche um mehr als 15 % zu klein ist, werden im Hintergrund neu dekodiert (entprellt 250 ms, sichtbare zuerst,
  gebündelt mit Background-Priorität an den UI-Thread). Kleiner ziehen dekodiert nichts neu.

## Übergang bestehender Kataloge

Herberts Katalog (256 Dateien) hat Vorschaubilder in 320 px. Sie werden gezeigt, bis das 400-px-Bild fertig ist, dann
samt Zeile gelöscht. Die Pflege im Hintergrund ersetzt die übrigen in Stapeln zu 32, jeweils erst bei leerer Warteschlange;
bei 500 000 alten Vorschaubildern ≈ 2,5 h Hintergrundarbeit mit niedriger Thread-Priorität, Fortschritt in der Statusleiste.
Der zusätzliche Speicher entsteht schrittweise (alt wird je Datei sofort gelöscht).

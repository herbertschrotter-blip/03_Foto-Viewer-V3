# Spike #15 — Doppelte: Größen-Vorfilter, SHA-256, pHash-Abstände, Gruppieren, SQL bei 500 000 Dateien

- **Status:** 🟢 Grün
- **Datum:** 2026-10-21 (v0.3.1)
- **Bezogen auf:** [ADR-0053](../adr/0053-doppelte-erkennen.md), `src/PhotoViewerV3.Services/Duplicates/**`, Harness
  `tests/PhotoViewerV3.PerfTests/Spikes/Duplicates/Spike15Duplicates.cs`

## Messaufbau

- Rechner: i7-8700K (6 Kerne/12 Threads), 32 GB, SSD/OneDrive-Ordner lokal; Release-Build des Harness. Während der
  Messung lief Herberts Lumora — Werte schwanken.
- Echte Fotos: privater Handy-Katalog (24 217 Dateien inkl. Videos, nur gelesen). Ausgegeben wurden nur zusammengefasste
  Zahlen — keine Dateinamen, Pfade oder Bilder.
- Aufruf: `PhotoViewerV3.PerfTests spike15-duplicates --root <Ordner> --limit 400 --hash-limit 1500` (echte Fotos +
  Gruppieren + SQL) bzw. ohne `--root` (nur synthetisch). `--explain <DB>` zeigt die Abfragepläne.

## Ergebnisse — echte Fotos

| Messung | Ergebnis |
|---|---|
| Vorfilter Größe | 24 217 Dateien, 22 492 verschiedene Größen, **3 180 (13,1 %) teilen ihre Größe** — nur sie werden gehasht |
| SHA-256, 1 500 Dateien / 30 941 MB | erster Lauf 87,7 s = **353 MB/s** (58 ms/Datei), zweiter 86,6 s = 357 MB/s → plattengebunden |
| Hochrechnung 500 000 Dateien | 13,1 % × Ø 25,9 MB ≈ **80 min Lesen** beim ersten Durchgang (danach nur neue Dateien) |
| pHash laden — Original (längste Kante 128) | Median 42,8 ms, p95 65,1 ms |
| pHash laden — Vorschaubild (400 px) | Median **0,96 ms**, p95 1,31 ms; Rechnen Median 0,18 ms |

pHash-Abstand (400 JPEGs, keiner fast einfarbig):

| Abwandlung | Median | p95 | max |
|---|---:|---:|---:|
| verkleinert auf 640 px + neu gespeichert (Q70) | 0 | 4 | 8 |
| 3 % je Seite beschnitten | 6 | 10 | 16 |
| heller (+15) | 0 | 2 | 4 |
| fremde Fotos (20 000 Zufallspaare) | 32 | 38 | 48 |

- Zufallspaare mit Abstand ≤ 6 / 8 / 10 / 12: 0,11 / 0,13 / 0,15 / 0,16 % (darunter echte Doppelte des Ordners).
- Aufeinanderfolgende Fotos (Aufnahme-Reihenfolge) ≤ 6 / 8 / 10 / 12: 10,8 / 13,8 / 17,0 / 19,1 % — Serien und Kopien.
- Gruppen über die 400 Fotos: r = 6 → 29 Gruppen (17 %), r = 8 → 38 (23 %), r = 10 → 48 (28 %), r = 12 → 53 (33 %).

**Folgerung Schwelle:** verkleinerte/neu gespeicherte Kopien liegen sicher unter 8, leichte Ausschnitte bis ≈ 10,
fremde Fotos fast nie darunter → **Standard 10**; der Regler reicht bis 12 (darüber wachsen Gruppen aus Serien und
ähnlichen Motiven, und die Suche wird langsam, siehe unten).

## Ergebnisse — Gruppieren (synthetisch)

Zufällige 64-Bit-Fingerabdrücke, 10 % davon Mittelpunkt einer Gruppe aus 1–5 Kopien mit 0–6 gekippten Bits;
Mehrfach-Index-Hashing (4 × 16 Bit) + Stern-Gruppen, Nachbarsuche blockweise parallel.

| Anzahl | r = 6 | r = 8 | r = 10 | r = 12 |
|---|---:|---:|---:|---:|
| 100 000 | 0,13 s | 0,3–0,4 s | 0,2 s | 0,8–1,0 s |
| 500 000 | 0,7–1,2 s | 4,5–5,3 s | 4,3–5,0 s | 20–23 s |

Die Bucket-Suche liefert genau die Paare des Vollvergleichs (Test `SimilarGroupingTests`, r = 0–12).

## Ergebnisse — SQL auf 500 000 Dateien (Wegwerf-DB)

Wegwerf-DB über die echte Migration: 500 000 Dateien in 1 000 Ordnern (10 % unter einer Eingangs-Wurzel), 2 % byte-gleiche
Kopien, Prüfsummen wie in der App nur für ≈ 13 % (gleich große Dateien), Bild-Fingerabdrücke für alle (wie oben
gehäuft). Median aus 3 Läufen, warmer Cache, Release.

| Abfrage | Median | Ergebnis |
|---|---:|---|
| Größen-Vorfilter `GROUP BY Size HAVING COUNT(*) > 1` (je Durchgang) | 370 ms | 9 413 Größen |
| Seite „Doppelte“, exakt gleich (Gruppen + Mitglieder + offene Zähler) | 1,3 s | 9 368 Gruppen |
| Seite „Doppelte“, ähnlich r = 10 | 6,7 s | 39 815 Gruppen |
| Eingang schon in der Bibliothek (Zähler, nur im Hintergrund) | 1,6 s | 1 658 |
| Galerie-Filter „nur Doppelte“: Zählen | 1,1 s | 19 111 |
| Galerie-Filter „nur Doppelte“: Seite 1 (200 nach Änderungsdatum) | 1,1 s | 200 |

Erster Entwurf: korreliertes `EXISTS` je Galerie-Zeile bzw. je Eingangs-Datei — der Planer lief für den Eingang-Zähler
über `IX_Files_Library_Captured` durch alle Dateien der Bibliothek je Eingangs-Datei (nach 15 min abgebrochen); mit allen
Dateien gehasht kostete der Galerie-Filter ≈ 4,7 s. Jetzt nicht korreliert: die Prüfsummen mit mehr als einer Datei im
Bereich (bzw. die der Bibliotheks-Dateien) werden einmal über `IX_FileFingerprints_ContentHash` gesammelt, die Reihenfolge
der Verknüpfungen ist per `CROSS JOIN` festgelegt.

## Bewertung

🟢 Tragfähig bis 500 000 Dateien: der Index liest beim ersten Mal nur die gleich großen Dateien (≈ 80 min im Hintergrund,
pausiert bei Scans), die Bild-Fingerabdrücke kosten aus Vorschaubildern ≈ 1 ms je Foto (500 000 ≈ 10 min; ohne
Vorschaubild aus dem Original ≈ 43 ms). Die Seite „Doppelte“ zeigt exakte Gruppen in ≈ 1,3 s, der Galerie-Filter braucht ≈ 1,1 s; „Ähnlich“ braucht bei
500 000 Fotos und Schwelle 10 einige Sekunden (Ladering). Eingang-Zähler und Galerie-Filter laufen nicht korreliert (siehe oben).

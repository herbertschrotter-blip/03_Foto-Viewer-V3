# Spike #16 — Foto-Güte: Schärfe, Belichtung, Bildwirkung, Highlight-Auswahl

- **Status:** 🟢 Grün
- **Datum:** 2026-10-22 (v0.4.7)
- **Bezogen auf:** [ADR-0057](../adr/0057-foto-guete-und-highlights.md), `src/Lumora.Services/Quality/**`, Messung
  `tests/Lumora.Wpf.Tests/Quality/QualityMeasurementTests.cs`, Laufzeit der Auswahl
  `tests/Lumora.Tests/Quality/QualityHighlightSelectorTests.cs` (`Auswahl_aus_5000_Dateien_ist_schnell`)

## Messaufbau

- Rechner: Herberts PC (i7-8700K, 32 GB, SSD); **Debug-Build** (so läuft Lumora im Alltag); während der Messung bauten
  und testeten parallel andere Agenten — Werte schwanken.
- Laufzeit: 1 000 synthetische Vorschaubilder (533 × 400 JPEG, Qualität 85) im Temp-Ordner, WIC-Lader wie im
  Hintergrund-Index.
- Stichprobe: 300 zufällige JPEGs (fester Seed) aus Herberts Handy-Katalog (19 438 JPEG), nur gelesen; je Foto ein
  Vorschaubild wie im Katalog (kurze Kante 400, JPEG) im Temp-Ordner, danach gelöscht. Gegenproben: dieselben Bilder
  weichgezeichnet (Kastenfilter r = 2, zweimal) und abgedunkelt (× 0,3). Ausgegeben wurden nur zusammengefasste Zahlen.
- Bildwirkung: SigLIP 2 base-patch16-224 (`LUMORA_MOTIF_MODEL`), Bild-Vektoren der Stichprobe auf der Grafikkarte.
- Aufruf: `LUMORA_QUALITY_MEASURE=1`, optional `LUMORA_QUALITY_SAMPLE_DIR=<Ordner>` und `LUMORA_MOTIF_MODEL=<Ordner>`,
  dann `dotnet test tests/Lumora.Wpf.Tests --filter FullyQualifiedName~QualityMeasurementTests`.

## Ergebnisse — Laufzeit

| Messung | Ergebnis |
|---|---|
| Laden ≤ 512 + Güte, 1 000 Vorschaubilder | **6,97 s** (7,0 ms/Bild, davon Rechnen 3,9 ms) |
| zum Vergleich Laden ≤ 128 (pHash, ADR-0053) | 1,26 s |
| Hochrechnung 500 000 (ein Kern, Hintergrund) | **≈ 58 min**, einmalig; danach nur neue/geänderte Dateien |
| `SelectAsync` 5 000 Dateien (Güte, pHash-Serien zu 4, Gesichter, Favoriten) | **Median 87 ms** (min 73, max 247) allein; 128 ms (102–153) während der ganzen Test-Suite parallel — Ziel < 200 ms |
| `PickCoverAsync` 5 000 Dateien | 92 ms allein; 167 ms unter Last |

Die Rechenzeit ist Debug-Code (keine JIT-Optimierung); Festkomma-Helligkeit und vorberechnete Kachel-Spalten brachten
≈ 15 %. Ein gemeinsamer Durchlauf mit dem pHash lohnt nicht: der pHash müsste dann ebenfalls auf 512 px laden
(≈ 3 ms statt 1,3 ms) und seine Fingerabdrücke verschöben sich.

## Ergebnisse — Stichprobe (300 Fotos)

| Messung | Ergebnis |
|---|---|
| Laplace-Varianz roh (Mittel der 4 schärfsten von 16 Kacheln) | p10 448 · p50 1 828 · p90 4 151 |
| Schärfe 0..1 (log. zwischen 5 und 8 000) | p10 0,60 · p50 0,79 · p90 0,91 — weichgezeichnet p10 0,02 · p50 0,11 · p90 0,16 |
| Belichtung 0..1 | p10 0,69 · p50 0,95 · p90 1,00 — abgedunkelt p10 0,18 · p50 0,28 · p90 0,35 |
| Mittlere Helligkeit | p10 0,35 · p50 0,47 · p90 0,57; ausgefressen > 5 %: 17 Fotos, abgesoffen > 10 %: 9 |
| Gegenprobe Schärfe / Belichtung | weichgezeichnet unschärfer **300 / 300**, abgedunkelt schlechter belichtet **297 / 300** |

Erste Normierung (Obergrenze 2 500) setzte p50 auf 0,95 — zu wenig Spreizung; daher 8 000.

## Ergebnisse — Bildwirkung (Prompt-Sätze, gleiche Bild-Vektoren)

| Prompt-Satz | weich niedriger | dunkel niedriger | Spearman ↔ Schärfe | Abstand cos⁺ − cos⁻ (p10 · p50 · p90) |
|---|---|---|---|---|
| **gewählt**: en/de-Paar „schön, scharf, gut belichtet“ + „gelungenes Urlaubsfoto“ / en/de „verschwommen, dunkel, missglückt“ | **100 %** | **98 %** | 0,24 | −0,0197 · −0,0044 · 0,0088 |
| vier je Seite (+ „great composition“, + „Zufallsfoto vom Boden/Finger“) | 95 % | 83 % | 0,17 | −0,0221 · −0,0077 · 0,0074 |
| nur englisch (zwei je Seite) | 99 % | 79 % | 0,21 | −0,0245 · −0,0140 · −0,0021 |
| nur deutsch | 99 % | 96 % | 0,18 | −0,0171 · −0,0007 · 0,0117 |
| „a good photo“ / „a bad photo“ | 65 % | 66 % | −0,15 | −0,0084 · 0,0005 · 0,0071 |
| en/de-Paare ohne Urlaubsfoto | 100 % | 96 % | 0,24 | −0,0202 · −0,0069 · 0,0049 |
| + „unscharfes, verwackeltes, zu dunkles Foto“ | 98 % | 97 % | 0,14 | −0,0199 · −0,0044 · 0,0073 |

Abbildung des gewählten Satzes σ((Abstand + 0,0045) · 105): p10 0,17 · p50 0,50 · p90 0,80; Rangkorrelation mit der
Belichtung 0,09 — die Bildwirkung bringt eigene Information. Bildmodell nur für die Messung: 24 ms/Bild (DirectML); im
Betrieb liegen die Vektoren aus dem Motiv-Index schon vor.

## Bewertung

🟢 Alle Ziele erreicht: Gegenproben trennen sicher, die Auswahl für 5 000 Dateien liegt deutlich unter 200 ms, der
Hintergrund-Index ist mit ≈ 1 h für 500 000 Dateien vertretbar. Offen: Gewichte und Schwellen an echten Storys mit
Herbert nachjustieren (Welle 6).

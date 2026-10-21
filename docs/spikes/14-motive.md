# Spike #14 — Motive (SigLIP 2 base): Laufzeit CPU vs. DirectML, Vorlagen, Schwelle, Plausibilität

- **Status:** 🟢 Grün
- **Datum:** 2026-10-21 (v0.2.55)
- **Bezogen auf:** [ADR-0052](../adr/0052-motive-siglip2-lokal.md), `src/Lumora.Services/Motifs/**`, Messtests
  `tests/Lumora.Wpf.Tests/Motifs/MotifMeasurementTests.cs`, `MotifModelTests.cs`, `MotifModelExplorationTests.cs`

## Messaufbau

- Rechner: i7-8700K (6 Kerne/12 Threads), 32 GB, NVIDIA GTX 1080 Ti; ONNX Runtime DirectML 1.24.4; Debug-Build.
  Während der Messung lief Herberts Lumora mit eingeschalteter Gesichtserkennung — die Werte schwanken deshalb
  (zwei Läufe, beide genannt).
- Modell: onnx-community/siglip2-base-patch16-224-ONNX, `vision_model.onnx` (fp32) und `text_model_int8.onnx`,
  SHA-256 gegen die Hugging-Face-API geprüft.
- Stichprobe: 200 zufällige JPEGs (Seed 42) aus einem privaten Handy-Katalog (19 438 Fotos, nur gelesen). Ausgegeben
  wurden nur zusammengefasste Zahlen — keine Dateinamen, Pfade, Bilder. Bildschirmfotos gibt es dort keine.
- Referenzbilder ohne persönliche Fotos: gezeichnete Hund- und Katzen-Glyphe (Segoe UI Emoji, einfarbig), eine
  Textseite, ein Programmfenster, die Windows-Hintergründe „See bei Sonnenuntergang“, „See bei Tag“, „Blüte (abstrakt)“.
- Aufruf: `LUMORA_MOTIF_MODEL=<Ordner>`, `LUMORA_MOTIF_SAMPLE_DIR=<Fotos>`, optional
  `LUMORA_FACE_MODELS=<buffalo_l>` (Gegenprobe Gesichter), `dotnet test tests/Lumora.Wpf.Tests --filter
  MotifMeasurementTests --logger "console;verbosity=detailed"`.

## Ergebnisse — Zeit (ms)

| Schritt | Lauf 1 Ø (p50 / p90) | Lauf 2 Ø (p50 / p90) |
|---|---:|---:|
| Laden Original (WIC, längste Kante 448) | 44,6 (35 / 63) | 57,6 (45 / 81) |
| Laden Vorschaubild (400er-JPEG) | 0,9 | 1,3 |
| Bildmodell DirectML je Foto | 25,8 (23 / 34) | 26,1 (26 / 31) |
| Bildmodell CPU je Foto | 125 (123 / 131) | 202 (167 / 363) |
| Durchsatz DirectML, 3 Originale vorab | — | **39,7 ms/Foto** |
| Sprachmodell laden / je Text | 1 397 / 30 | 2 109 / 55 |
| Bildmodell laden (DirectML / CPU) | 1 399 / 687 | 1 722 / 1 007 |

CPU- und DirectML-Vektor desselben Bildes: Kosinus > 0,999 (Test). float16-Speicherung: Kosinus-Fehler < 0,001 (Test).

### Hochrechnung

| Bibliothek | DirectML, Originale (≈ 40 ms) | DirectML, aus Vorschaubildern (≈ 27 ms) | CPU (≈ 125–200 ms) |
|---:|---:|---:|---:|
| 50 000 | ≈ 33 min | ≈ 23 min | ≈ 1,7–2,8 h |
| 500 000 | ≈ 5,5 h | ≈ 3,8 h | ≈ 17–28 h |

Läuft die Gesichtserkennung gleichzeitig, wechseln sich beide über die GPU-Sperre Foto um Foto ab (Summe der Zeiten).
Katalog: 1 536 Bytes Vektor je Datei → 50 000 ≈ 80 MB, 500 000 ≈ 0,8 GB (float16; float32 wäre doppelt so groß).

## Vorlagen — was trennt am besten?

Referenzbilder, je Begriff Kosinus zum richtigen Bild minus bester falscher Kandidat (größer = besser):

| Text-Vektor | Hund | Katze | Dokument | Bildschirm | Sonnenuntergang | See | Ø |
|---|---:|---:|---:|---:|---:|---:|---:|
| Wort DE („Hund“) | +0,039 | +0,042 | +0,041 | +0,010 | +0,037 | +0,033 | +0,034 |
| Vorlage DE („ein Foto von einem Hund“) | +0,014 | +0,026 | +0,036 | +0,017 | +0,022 | −0,010 | +0,017 |
| Vorlage EN („a photo of a dog“) | +0,040 | +0,053 | +0,044 | −0,001 | +0,066 | +0,066 | +0,045 |
| **Wort DE + Vorlage EN (gemittelt)** | +0,039 | +0,048 | +0,043 | +0,014 | +0,052 | +0,052 | **+0,041** |
| Wort DE + Vorlage DE + EN | +0,034 | +0,047 | +0,041 | +0,016 | +0,042 | +0,032 | +0,036 |

Gewählt: **Wort + englische Vorlage** (fast so gut wie Englisch allein, ohne dessen Aussetzer beim Bildschirm). Die
deutsche Satz-Vorlage hebt bei Text- und Bildschirm-Bildern alle Begriffe an und trennt am schlechtesten. Eigene Begriffe
ohne Beschreibung nutzen nur das Wort (zweitbeste Zeile).

Referenzprüfung mit den 82 Standard-Begriffen (Test `MotifModelTests`): Hund-Glyphe → Platz 1 „Hund“ (2,0 %, Katze
0,01 %), Katzen-Glyphe → Platz 1 „Katze“ (2,9 %), Textseite → „Dokument“ (4,7 %), Programmfenster → „Bildschirmfoto“
(2,6 %), See bei Sonnenuntergang → „Sonnenaufgang · See · Sonnenuntergang“ auf Platz 1–3.

## Schwelle — SigLIP-Wahrscheinlichkeit p = σ(112,7 · cos − 16,77)

Höchste Wahrscheinlichkeit je Foto (200 Fotos): Ø 1,7 %, Median 0,7 %, p90 4,1 %, max 20,7 % — kurze Begriffe erreichen
nie die 50 %, die SigLIP für ausführliche Bildbeschreibungen liefert.

| Schwelle | Fotos mit ≥ 1 Motiv | Ø Motive je Foto |
|---:|---:|---:|
| 0,5 % | 118 / 200 | 0,91 |
| **1 % (Standard)** | **85 / 200** | **0,56** |
| 2 % | 55 / 200 | 0,31 |
| 3 % | 33 / 200 | 0,18 |
| 5 % | 16 / 200 | 0,09 |
| 10 % | 5 / 200 | 0,03 |

Häufigste Motive bei 1 %: Berge 23, Selfie 14, Gruppe 10, Wandern 10, Schnee 9, Sonnenaufgang 4, Straße 4, … (29
Begriffe mit mindestens einem Treffer).

## Plausibilität ohne Hinsehen (Gegenprobe mit buffalo_l)

Die Fotos werden nicht angesehen; als schwaches Kennzeichen dient die Gesichtserkennung (Gesichter ≥ 40 px):

| Bei 1 % | Ergebnis |
|---|---|
| Fotos ohne Gesicht (126) mit Personen-Motiv (Gruppe, Selfie, Porträt, Kinder, Baby, Party, Hochzeit) | 3 (2,4 %) |
| Fotos mit ≥ 3 Gesichtern (25) mit irgendeinem Personen-Motiv | 15 (60 %), davon „Gruppe“ 6 |
| „Gruppe“ auf Fotos mit < 2 Gesichtern | 4 |

Bei 3 % fallen Fehltreffer kaum (2 bzw. 3), die Trefferquote aber stark (4 von 25) — 1 % ist der bessere Standard.
Wie viele der 85 Fotos mit Motiv **inhaltlich** richtig liegen, lässt sich ohne Ansehen nicht zählen; die Gegenprobe
spricht für hohe Genauigkeit bei mäßiger Vollständigkeit. Feinabstimmung je Begriff (eigene Schwelle) nach Herberts
Rückmeldung.

## Folgerungen

- Vorschaubild statt Original laden (≈ 1 ms statt ≈ 45 ms) — der Index nutzt es, sobald es existiert.
- DirectML ist ≈ 5–8 × schneller als die CPU; die GPU-Sperre hält Gesichter und Motive auseinander.
- Standard-Schwelle 1 %, je Begriff 0,3 %–10 % wählbar; Bildinhalt-Suche mit Kosinus-Untergrenze 0,05 (unpassende Paare
  lagen bei 0,00–0,05, passende bei 0,09–0,14).

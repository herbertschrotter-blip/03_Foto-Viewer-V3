# Spike #09 — Gesichtserkennung (buffalo_l): Laufzeit CPU vs. DirectML, Qualität, Ähnlichkeiten

- **Status:** 🟢 Grün
- **Datum:** 2026-10-21 (v0.2.34)
- **Bezogen auf:** [ADR-0035](../adr/0035-gesichtserkennung-buffalo-l-directml.md), `src/Lumora.Services/Faces/**`,
  `src/Lumora.Wpf/Faces/WicFaceImageLoader.cs`, Messtest
  `tests/Lumora.Wpf.Tests/Faces/FaceRecognitionMeasurementTests.cs`

## Messaufbau

- Rechner: i7-8700K (6 Kerne/12 Threads), 32 GB, NVIDIA GTX 1080 Ti; ONNX Runtime DirectML 1.24.4; Debug-Build.
- Stichprobe aus einem privaten Handy-Katalog (19 556 JPEGs, nur gelesen): 500 zufällige Fotos (Seed 4711) plus jeweils
  das nach Dateinamen folgende Foto desselben Ordners (Serienaufnahmen) = **1 000 Fotos**. Ausgegeben wurden nur
  zusammengefasste Zahlen — keine Dateinamen, Pfade, Bilder oder Ausschnitte.
- Pipeline wie im Index: WIC-Laden mit längster Kante 1280 und EXIF-Orientierung → SCRFD det_10g (Letterbox 640,
  Score ≥ 0.5, NMS 0.4) → Ausrichtung 112 × 112 → ArcFace w600k_r50 → L2-Norm. CPU: ArcFace in Stapeln bis 16,
  DirectML: einzeln (Stapel scheitert dort, siehe unten).
- Laden einmal vorab gemessen (danach warmer Dateicache), Erkennen/Fingerabdruck je Ausführung über alle 1 000 Fotos
  (nach einem Aufwärmlauf). „Durchsatz“ = Wanduhr je Foto wie im Index (Laden auf Pool-Threads vorab, Analyse
  nacheinander), je 150 Fotos.
- Aufruf: `LUMORA_FACE_MODELS=<buffalo_l>`, `LUMORA_FACE_SAMPLE_DIR=<Ordner>`, `LUMORA_FACE_SAMPLE_PAIRS=500`,
  `dotnet test tests/Lumora.Wpf.Tests --filter FaceRecognitionMeasurementTests --logger "console;verbosity=detailed"`.

## Ergebnisse — Zeit je Foto (ms)

| Schritt | Ø | p50 | p90 | max |
|---|---:|---:|---:|---:|
| Laden (WIC, ≤ 1280 px) | 115 | 88 | 187 | 1 732 |
| Erkennen — CPU | 130 | 114 | 183 | 598 |
| Erkennen — DirectML | 23 | 20 | 32 | 145 |
| Fingerabdruck je Foto — CPU | 112 | 0 | 279 | 4 034 |
| Fingerabdruck je Foto — DirectML | 14 | 0 | 30 | 864 |
| Fingerabdruck je Gesicht — CPU | 112 | 101 | 158 | 353 |
| Fingerabdruck je Gesicht — DirectML | 12 | 10 | 17 | 67 |

Durchsatz (Laden parallel vorab, Analyse nacheinander):

| Vorab geladen | CPU ms/Foto | DirectML ms/Foto |
|---:|---:|---:|
| 1 | 301 | 82 |
| 2 | 231 | 65 |
| 3 | 255 | **46** |
| 4 | 236 | 42 |

→ Standard **3 Bilder vorab** (`FaceIndexDefaults.Prefetch`): mit Grafikkarte fast der Bestwert, die CPU bleibt
für UI und Vorschaubilder frei genug; ohne Grafikkarte begrenzt die Inferenz, mehr Vorab-Laden bringt dort nichts.
Mit DirectML ist das **Dekodieren der JPEGs der Engpass** (12-MP-Handyfotos, Huffman-Dekodierung lässt sich durch
verkleinertes Dekodieren kaum sparen).

## Ergebnisse — Gesichter und Qualität

| Wert | Ergebnis |
|---|---|
| Fotos mit Gesichtern | 444 von 1 000 (**44,4 %**) |
| Gesichter gesamt / je Foto | 1 121 / Ø **1,12** (max 44 in einem Gruppenfoto) |
| Gesichter je Foto | 0: 556 · 1: 206 · 2: 131 · 3: 51 · 4: 11 · 5+: 45 |
| Erkennungs-Score | Ø 0,81 · p10 0,64 · p50 0,84 · p90 0,90 |
| Größe (px im 1280er-Bild) | Ø 94 · p10 25 · p50 62 · p90 203 · max 759 |
| Schärfe (Laplace-Varianz 112er) | Ø 397 · p10 11 · p50 92 · p90 1 151 |
| Qualität 0..1 | Ø 0,57 · p10 0,20 · p50 0,56 · p90 0,96 |
| Qualität verteilt (Zehntel) | 52 · 66 · 108 · 156 · 133 · 86 · 75 · 99 · 115 · 231 |
| Länge der Fingerabdrücke | 1,0000 (alle) |
| CPU vs. DirectML | gleiche Gesichter, Werte bis auf die 3. Nachkommastelle der Schärfe identisch |

Etwa jedes fünfte Gesicht hat eine Qualität unter 0,3 (klein, unscharf, Hintergrund) — Welle 2 sollte solche Gesichter
nicht zum Lernen verwenden und nur vorsichtig vorschlagen.

## Ergebnisse — Kosinus-Ähnlichkeit

| Paare | n | Ø | p10 | p50 | p90 | max | Anteil > 0,4 | Anteil < 0,2 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Gesichter im selben Foto (verschiedene Personen, Qualität ≥ 0,3) | 1 918 | 0,09 | −0,04 | 0,05 | 0,23 | 0,85 | 5,9 % | 87,5 % |
| Serienpaar, je genau ein Gesicht an ähnlicher Stelle (wahrscheinlich dieselbe Person) | 35 | 0,79 | 0,46 | 0,87 | 0,98 | 0,99 | 94,3 % | — |

Dieselbe Person liegt also deutlich über 0,4, verschiedene Personen meist unter 0,2. Die Ausreißer „im selben Foto“
über 0,4 stammen aus Fotos, in denen dieselbe Person mehrfach vorkommt (Spiegel, Bildschirm, Collage, Gruppenfotos mit
44 Gesichtern) — „gleiches Foto = verschiedene Personen“ ist nur eine grobe Näherung.

## Hochrechnung

| Bibliothek | DirectML (46 ms/Foto) | CPU (231 ms/Foto) | Gesichter (1,12/Foto) | DB-Zuwachs (≈ 2,3 KB/Gesicht) |
|---|---:|---:|---:|---:|
| 19 556 Fotos | ≈ 15 min | ≈ 75 min | ≈ 22 000 | ≈ 50 MB |
| 100 000 Fotos | ≈ 1,3 h | ≈ 6,4 h | ≈ 112 000 | ≈ 0,26 GB |
| 500 000 Fotos | ≈ **6,4 h** | ≈ **32 h** | ≈ 560 000 | ≈ **1,3 GB** |

Der Index läuft im Hintergrund, pausier- und fortsetzbar und überlebt Neustarts (FaceScans); bereits analysierte Fotos
werden nie erneut angefasst, außer die Modell-Kennung ändert sich oder „Index neu aufbauen“ wird gewählt.

## Beobachtungen

- **DirectML + ArcFace-Stapel**: w600k_r50 deklariert die Ausgabe als [1, 512]; auf der CPU funktionieren Stapel
  (mit Warnung, daher Protokollstufe „Fehler“), mit DirectML scheitert ein Stapel > 1 an einer BatchNormalization
  („Falscher Parameter“). Der Analysator rechnet mit DirectML deshalb jedes Gesicht einzeln (≈ 10 ms).
- Einige Handy-JPEGs melden beim Dekodieren „Corrupt JPEG data“ oder „Invalid SOS parameters“; WIC liefert trotzdem
  ein Bild (Ladefehler: 0 von 1 000).
- Erster DirectML-Lauf je Sitzung (Kompilieren) ≈ 0,5–0,9 s — deshalb der Probelauf beim Laden.

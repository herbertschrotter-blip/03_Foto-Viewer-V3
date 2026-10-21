# Spike #12 — Aufnahmedatum: Lesen im Scan oder im Hintergrund, verwalteter Leser oder WIC, Sortierung bei 500 000

- **Status:** 🟢 Entschieden — verwalteter Leser, Nachtragen im Hintergrund nach dem Scan, Ausdrucks-Index
- **Datum:** 2026-10-21 (v0.2.48)
- **Bezogen auf:** [ADR-0046](../adr/0046-aufnahmedatum.md), PLAN Kap. 4, Harness
  `tests/PhotoViewerV3.PerfTests/Spikes/Metadata/Spike12CaptureDate.cs`, Messtest
  `tests/PhotoViewerV3.Wpf.Tests/Gallery/CaptureDateSortMeasurementTests.cs`

## Fragen

1. Darf das Aufnahmedatum **im Scan** gelesen werden (Vorgabe: Scan höchstens ≈ 10 % langsamer)?
2. **WIC-Metadatenabfrage oder eigener Parser?** (Schichtregel: kein WPF in Services; HEIC/Video?)
3. Hält die **Sortierung** nach `COALESCE(CapturedAt, ModifiedAtUtc)` bei 500 000 Dateien mit Filtern mit?

## Messaufbau

- Herberts Handy-Katalog (lokal, 24 217 Mediendateien in 505 Ordnern; nur gelesen, Ausgabe nur Zahlen). Wegwerf-DB im Temp-Ordner. Rechner: i7-8700K, 6 Kerne / 12 Threads, 32 GB, SSD.
- Aufruf: `dotnet run -c Debug --project tests/PhotoViewerV3.PerfTests -- spike12-capture-date --root <Ordner>
  --limit 2000 --runs 3`.
- Sortierung: synthetischer Katalog wie Spike #08 (500 000 Dateien, 200 je Ordner, jede 4. ein Video, 70 % mit
  Aufnahmedatum quer zu Id und Änderungsdatum, Tag „Familie“ auf 20 %), kein ANALYZE.
  `PV3_CAPTURESORT_FILES=500000 dotnet test tests/PhotoViewerV3.Wpf.Tests --filter CaptureDateSortMeasurement`.

## Ergebnis

### 1. Scan alt/neu (Fresh-Scan, 24 217 Dateien, je 3 Läufe)

| Variante | Läufe (s) | Median |
|---|---|---:|
| ohne Sortier-Index (alt) | 5,18 / 3,95 / 4,36 | **4,36 s** |
| mit Sortier-Index (neu) | 4,74 / 4,04 / 4,93 | **4,74 s** |

Mehraufwand **+8,8 %** (Streuung je Lauf ± 15 %) — allein die Pflege des neuen Ausdrucks-Index beim Bulk-Insert; der Scan
liest keine Datei. **Lesen im Scan** hätte den Scan dagegen vervielfacht: das Nachtragen derselben 24 217 Dateien
(kalter Dateicache) dauerte **179,5 s** ≈ **+3 800 %** gegenüber 4,74 s.

### 2. Leser je Datei (Stichprobe 2 000 Dateien, 95 Videos)

| Leser | Median | p95 |
|---|---:|---:|
| verwaltet, kalter Cache (erstes Lesen von der Platte) | 6,27 ms | 13,2 ms |
| verwaltet, warm | **0,115 ms** | 0,21 ms |
| WIC `BitmapDecoder` (DelayCreation) + `GetQuery`, warm, nur Bilder | 1,31 ms | 6,0 ms |

Herkunft: 1 712 EXIF, 91 Video, 190 Dateiname, 7 unbekannt. **Übereinstimmung mit WIC: 1 712 von 1 712** Bildern mit
EXIF-Datum gleich, 0 nur WIC, 0 nur verwaltet. Der kalte Wert ist das Öffnen und erste Lesen der Datei (Platte,
Virenscanner) — bei WIC fällt er genauso an.

### 3. Nachtragen end-to-end (`CaptureDateService.RunBackfillPassAsync`, alle 24 217 Dateien)

24 217 gelesen (24 164 mit Datum), 0 übersprungen, **179,5 s = 135 Dateien/s** (kalter Cache, ein Thread, Stapel zu
200 mit je einer Transaktion). Hochgerechnet: 100 000 Dateien ≈ **12 min**, 500 000 ≈ **1 h** — einmalig für einen
bestehenden Katalog, im Hintergrund, mit Pause bei Scan/Datei-Batch/Umzug. Neue Dateien eines Scans (Dutzende bis
Hunderte) sind in Sekunden gelesen.

### 4. Sortierung bei 500 000 Dateien (Median aus 3 Läufen, Seitengröße 500)

| Fall | Treffer | Zählen (ms) | Seite (ms) |
|---|---:|---:|---:|
| Aufnahme neu → alt, Seite 1 | 500 000 | 27 | **8** |
| Aufnahme neu → alt, Seite 501 (Mitte) | 500 000 | 34 | 211 |
| Aufnahme neu → alt, Seite 1000 (letzte) | 500 000 | 29 | **3** |
| Aufnahme alt → neu, Seite 501 (Mitte) | 500 000 | 31 | 1 311 |
| Aufnahme + Bilder (75 %), Seite 1 | 375 000 | 19 | 9 |
| Aufnahme + Videos (25 %), Seite 1 | 125 000 | 6 | 14 |
| Aufnahme + Tag „Familie“ (20 %), Seite 1 | 100 000 | 319 | 209 |
| Aufnahme + Ordner (mit Unterordnern), Seite 1 | 200 | 1 | 1 326 |
| Aufnahme + Suche „img“ (alle Treffer), Seite 1 | 500 000 | 220 | 323 |
| Vergleich: Änderungsdatum neu → alt, Seite 501 | 500 000 | 26 | 2 316 |
| Vergleich: Änderungsdatum alt → neu, Seite 501 | 500 000 | 27 | 2 272 |
| Vergleich: Änderungsdatum + Ordner, Seite 1 | 200 | 1 | 4 366 |
| Vergleich: Änderungsdatum + Tag „Familie“, Seite 1 | 100 000 | 336 | 174 |

Pläne (Seite): ohne Filter `SEARCH f USING INDEX IX_Files_Library_Captured (LibraryId=?)` **ohne** temporären B-Baum
(Id ist letzte Index-Spalte; ohne sie stand „USE TEMP B-TREE FOR LAST TERM OF ORDER BY“ wie heute beim
Änderungsdatum). Tag- und Ordnerfilter laufen über den Index mit Bloom-Filter, die Suche über die FTS-Treffer — **kein
Plan durchläuft Files** (Testfall `Abfrageplan_ohne_Vollscan`, 32 Kombinationen). Die Sortierung nach Aufnahmedatum
ist in allen vergleichbaren Fällen gleich schnell oder schneller als die nach Änderungsdatum. Langsam bleiben die Mitte
einer 500 000er-Liste (großer OFFSET) und der Ordnerfilter mit Datums-Sortierung (der Planer läuft den Datums-Index
entlang, bis 200 passende Dateien gefunden sind) — beides war beim Änderungsdatum schon so (dort langsamer).

## Entscheidung

- **Nicht im Scan lesen**, sondern direkt danach im Hintergrund (Scan +8,8 % nur durch den Index, statt +3 800 %).
- **Verwalteter Leser** statt WIC: 11× schneller warm, gleiche Ergebnisse, liest HEIC ohne HEIF-Erweiterung und Videos,
  keine WPF-Abhängigkeit in Services, keine neue NuGet-Abhängigkeit (MetadataExtractor nicht nötig).
- Ausdrucks-Index `(LibraryId, COALESCE(CapturedAt, ModifiedAtUtc) DESC, Id DESC)`.

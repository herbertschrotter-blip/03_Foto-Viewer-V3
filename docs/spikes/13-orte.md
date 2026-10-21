# Spike #13 — Orte: GPS lesen, Ortsnamen offline, EXIF schreiben

- **Status:** 🟢 Entschieden — eigene Pflege-Schleife nach dem Muster des Aufnahmedatums, Raster-Index, verlustfreies
  APP1-Schreiben
- **Datum:** 2026-10-21 (v0.2.53)
- **Bezogen auf:** [ADR-0050](../adr/0050-orte.md), PLAN Kap. 4, Harness
  `tests/PhotoViewerV3.PerfTests/Spikes/Metadata/Spike13Places.cs`, Tests `tests/PhotoViewerV3.Tests/Places/*`

## Fragen

1. Was kostet das Lesen der Koordinaten **je Datei** zusätzlich zum Aufnahmedatum (eigener Durchgang statt im Scan)?
2. Liest der verwaltete GPS-Leser dasselbe wie WIC?
3. Schafft der Ortsname offline **Nearest < 0,1 ms** bei ≤ 3 MB eingebetteten Daten?
4. Wie teuer ist das verlustfreie Schreiben (Temp-Datei + Ersetzen) und ist Rückgängig Byte für Byte gleich?

## Messaufbau

- Herberts Handy-Katalog (lokal, 24 217 Mediendateien; **nur gelesen**, Ausgabe nur Zahlen — keine Datei-, Ordner- oder
  Ortsnamen). Wegwerf-DB im Temp-Ordner. Rechner: i7-8700K, 6 Kerne / 12 Threads, 32 GB, SSD.
- Aufruf: `dotnet run -c Debug --project tests/PhotoViewerV3.PerfTests -- spike13-places --root <Ordner> --limit 2000`.
- Schreiben nur an **synthetischen** JPEGs (50 × 4 MB, im Temp-Ordner) — nie an echten Fotos.

## Ergebnis

### 1. Leser je Datei (Stichprobe 2 000 Dateien, 80 Videos; 1 665 mit Koordinaten, davon 46 aus Videos)

| Leser | Median | p95 |
|---|---:|---:|
| GPS, kalter Cache (zuerst gelesen) | 1,19 ms | 2,45 ms |
| Aufnahmedatum, warm (Vergleich) | 0,142 ms | 0,354 ms |
| GPS warm **inkl.** Prüfung auf XMP-Begleitdatei (2 × `File.Exists`) | **0,160 ms** | 0,250 ms |
| GPS warm ohne XMP-Prüfung | 0,092 ms | — |

**Übereinstimmung mit WIC** (`/app1/ifd/gps`, nur Bilder): 1 619 von 1 619 Bildern mit GPS gleich (± 1e-6°), 0 nur WIC,
0 nur verwaltet. Die XMP-Prüfung kostet warm ≈ 0,07 ms je Datei — vertretbar, dafür gewinnen Begleitdateien von
Lightroom/digiKam und die von Lumora geschriebenen.

### 2. Ortsnamen (eingebettete Ressource 1,5 MB gzip, 64 584 Orte)

| Messung | Wert |
|---|---:|
| Laden der Ressource (einmal, im Hintergrund beim Start) | 155 ms |
| `Nearest`, 100 000 Zufallspunkte weltweit (auch Meer) | **1,75 µs** je Aufruf |
| `Nearest`, die echten Koordinaten der Stichprobe (dichte Gegend) | **8,0 µs** je Aufruf (79 verschiedene Orte) |
| `Search`, erster Aufruf (Suchschlüssel aufbauen) | 85 ms |
| `Search` danach | 12 ms je Suche |

Vorgabe < 0,1 ms deutlich eingehalten (Raster 1° × 1°, ringweise Suche mit Abbruch, Haversine nur für die Grenze).

### 3. Nachtragen end-to-end (`GeoLocationService.RunBackfillPassAsync`, alle 24 217 Dateien)

24 217 gelesen (20 156 mit Koordinaten), 0 übersprungen in **30,7 s = 790 Dateien/s** (Dateicache durch die Messung
in [1] und die anderen Läufe teilweise warm; ein Thread, Stapel zu 200 mit je einer Transaktion, `Nearest` je Datei mit
Koordinate). Hochgerechnet 100 000 Dateien ≈ **2 min** warm; bei kaltem Cache gilt wie beim Aufnahmedatum ≈ 135
Dateien/s (Öffnen der Datei dominiert, Spike #12) ≈ 12 min für 100 000 — einmalig, im Hintergrund, mit Pause bei
Scan/Datei-Batch/Umzug. Der Scan selbst (3,18 s für 24 217 Dateien) bleibt unberührt.

### 4. Schreiben (50 synthetische JPEGs à 4 MB, Temp-Datei neben dem Original + `File.Replace`)

| Schritt | Median | p95 |
|---|---:|---:|
| GPS setzen (Kopf + neues APP1 + Rest kopieren, prüfen, ersetzen, Änderungsdatum zurück) | **38 ms** | 169 ms |
| Rückgängig (altes APP1 zurück) | 41 ms | — |

**Byte-gleich nach Rückgängig: 50 / 50.** Die Kosten sind das Kopieren der Datei (4 MB); 1 000 Fotos ≈ 40 s.

## Entscheidung

- **Eigene Pflege-Schleife** (`GeoSource IS NULL`) nach dem Muster des Aufnahmedatums statt Erweiterung der bestehenden:
  bestehende Kataloge haben das Aufnahmedatum schon gelesen (Quelle ≠ NULL) — die alte Schleife käme an ihnen nie wieder
  vorbei. Zusatzkosten je Datei warm ≈ 0,16 ms (ein weiteres Öffnen + Kopf lesen).
- **Container-Durchgang geteilt** (`MediaDateReader.ReadContainer<T>`): JPEG/TIFF/RAF/PNG/WebP/HEIC/MP4 werden für Datum
  und GPS mit demselben Code gefunden.
- **Raster-Index** im Speicher statt SQLite-Tabelle: 1,75–8 µs, 9 MB Arbeitsspeicher, keine Migration für Ortsdaten.
- **Verlustfreies APP1-Schreiben** über eine Temp-Datei: nie halbe Dateien, Rückgängig byte-genau.

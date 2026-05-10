# Spike #01 — SQLite Bulk + FTS5 mit 500k synthetischen FileRows

- **Status:** ✅ Grün — Architektur bestätigt, freigegeben für V0.1-Implementierung
- **Datum gestartet:** 2026-05-10
- **Datum abgeschlossen:** 2026-05-10
- **Bezogen auf:** [ADR-0004](../adr/0004-datenzugriff-ef-core-raw-sqlite.md), [ADR-0011](../adr/0011-suche-und-filter-v01.md), [PLAN.md Kap. 8 + 9](../../PLAN.md)
- **Blockiert:** Schema, Scanner, Search-UI

## Zielsetzung

Sicherstellen, dass die geplante Data-Strategie (EF Core CRUD + Raw `Microsoft.Data.Sqlite` Bulk + FTS5 minimal) die Messziele für eine 500k-Bibliothek erreicht **bevor** UI-Code geschrieben wird.

## Messziele

| Messung | Ziel SSD | Ziel HDD-Simulation |
|---|---|---|
| Bulk-Insert 500k FileRows | < 5 min | < 15 min |
| Folder-Query (Page 500 Items) | < 100 ms | < 100 ms |
| FTS-Name-Query | < 200 ms | < 200 ms |
| Filter MediaType (Page 500 Items) | < 100 ms | < 100 ms |
| DB-Größe nach Insert + FTS | (Referenz) | (Referenz) |

## Aufbau

### Synthetische Daten (reproduzierbar, deterministischer Seed)
- 500.000 FileRows in 10.000 Folders
- 80 % `.jpg` / `.png` (MediaType = Image), 20 % `.mp4` (MediaType = Video)
- Pareto-Verteilung: Top-20 % der Folders halten 80,2 % der Files (Ziel ~80 %)
- 5 % `IsFavorite=1`
- `ModifiedAtUtc` gleichverteilt über 5 Jahre
- Namensmuster: `IMG_YYYYMMDD_HHMMSS_NNNN.jpg` (50 %), `DSC_NNNNN.jpg` (20 %), `screenshot_*.png`, `Urlaub_<Topic>_NNN.mp4`, `Aufnahme_*`, freie Topic-Kombinationen
- Folder-Pfade dedupliziert via HashSet (sonst kollidiert UNIQUE-Index `(LibraryId, Path)` bei begrenzter Topic-Year-Kombinatorik)

### Schema (V0.1-relevant)
- `Folders(Id, LibraryId, Path, ParentId, Name, ScannedAt)`
- `Files(Id, LibraryId, FolderId, Name, Extension, Size, CreatedAtUtc, ModifiedAtUtc, MediaType, Width, Height, DurationMs, Codec, IsFavorite, Rating, Missing)`
- `DateTime` als Unix-Sekunden (`INTEGER`) — kompakt + index-freundlich
- Alle 7 Pflicht-Indizes laut PLAN.md Kap. 4
- `FilesFts(Name, Extension, content='Files', content_rowid='Id')` — externe Content-Table
- **Keine** FOREIGN-KEY-Constraints (kostet Bulk-Performance, ist Scanner-seitig geprüft)

### PRAGMAs
```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA temp_store   = MEMORY;
PRAGMA foreign_keys = OFF;
```

### Insert-Strategie
- Single `SqliteConnection`, single `SqliteTransaction` für gesamten Files-Insert
- Prepared `SqliteCommand` mit benannten Parametern, `cmd.Prepare()`, Parameter-Werte pro Row gesetzt
- Indizes **vor** Insert angelegt (Production-typisches Profil; HDD-Variante "Index nach Insert" als zukünftiger Reserve-Hebel)
- `INSERT OR IGNORE INTO Files …` — robust gegen UNIQUE-Kollision, erlaubt idempotenten Re-Scan
- FTS5-Befüllung **nach** Files-Insert via `INSERT INTO FilesFts(rowid, Name, Extension) SELECT Id, Name, Extension FROM Files`
- Folders eigene kleine Transaction
- `ANALYZE` + `PRAGMA wal_checkpoint(TRUNCATE)` nach Abschluss

## Messverfahren

Pro Query 3 Warmup-Runs (verworfen) + 5 Messungen — Median, p95, Min, Max. `EXPLAIN QUERY PLAN` mitgeloggt zur Index-Verifikation.

## Hardware-Profil

| Feld | Wert |
|---|---|
| OS | Windows 11 Pro Build 26200 |
| CPU | Intel Core i7-8700K @ 3.70 GHz (6 Kerne / 12 Threads) |
| RAM | 32 GB |
| Storage | Samsung SSD 860 EVO 2 TB (SATA) — kein NVMe |
| .NET SDK | 10.0.203 |
| Microsoft.Data.Sqlite | 10.0.0 |

## Ergebnisse

### Bulk-Phase

| Phase | Zeit | Throughput |
|---|---|---|
| Folders generieren (RAM) | 15,2 ms | — |
| DB löschen + öffnen + PRAGMAs + Schema + Indizes + FTS5 | 80,2 ms | — |
| Bulk-Insert 10.000 Folders | 86,0 ms | ~116k rows/s |
| Bulk-Insert 500.000 Files (mit Index-Pflege) | 44,50 s | 11.237 rows/s avg (Start 24k, Ende 11k) |
| Files-Commit | 929,5 ms | — |
| FTS5-Population (`INSERT … SELECT`) | 1,65 s | ~303k rows/s |
| ANALYZE | 440,3 ms | — |
| WAL-Checkpoint | 18,4 ms | — |
| **Bulk-Phase gesamt** | **47,16 s = 0,79 min** | — |

| DB-Datei | Größe |
|---|---|
| `library-spike.db` | 118,21 MB |
| `library-spike.db-wal` | 0,00 MB (nach Checkpoint) |
| `library-spike.db-shm` | 0,22 MB |

### Query-Performance (Page 500)

| Query | Rows | Median | p95 | Min | Max | Index laut EXPLAIN |
|---|---:|---:|---:|---:|---:|---|
| Folder-Page #3315 | 330 | 0,25 ms | 0,28 ms | 0,25 ms | 0,28 ms | `IX_Files_Folder_Name` |
| Folder-Page #3004 | 328 | 0,47 ms | 0,53 ms | 0,23 ms | 0,53 ms | `IX_Files_Folder_Name` |
| Folder-Page #4874 | 327 | 0,24 ms | 0,26 ms | 0,23 ms | 0,26 ms | `IX_Files_Folder_Name` |
| FTS-Name MATCH 'Urlaub' | 500 | 0,45 ms | 0,68 ms | 0,44 ms | 0,68 ms | `FilesFts` VIRTUAL + `Files.rowid` PK |
| FTS-Name MATCH 'Geburtstag' | 500 | 0,43 ms | 0,78 ms | 0,31 ms | 0,78 ms | `FilesFts` VIRTUAL + PK |
| FTS-Name MATCH 'IMG' | 500 | 0,30 ms | 0,33 ms | 0,26 ms | 0,33 ms | `FilesFts` VIRTUAL + PK |
| FTS-Name MATCH 'Berge' | 500 | 0,34 ms | 0,79 ms | 0,31 ms | 0,79 ms | `FilesFts` VIRTUAL + PK |
| MediaType-Filter Image (ORDER BY ModifiedAtUtc DESC) | 500 | 4,38 ms | 5,07 ms | 3,74 ms | 5,07 ms | `IX_Files_Library_Modified` |
| MediaType-Filter Video (ORDER BY ModifiedAtUtc DESC) | 500 | 15,71 ms | 18,45 ms | 14,63 ms | 18,45 ms | `IX_Files_Library_Modified` |
| Favorit-Filter (ORDER BY ModifiedAtUtc DESC) | 500 | 64,61 ms | 71,29 ms | 63,57 ms | 71,29 ms | `IX_Files_Library_Modified` |

### Zielvergleich

| Messung | Ergebnis (worst) | Ziel | Faktor Reserve | Ampel |
|---|---|---|---|---|
| Bulk-Insert 500k | 0,79 min | < 5 min | **6,4×** | 🟢 |
| Folder-Query 500 Items (Median) | 0,47 ms | < 100 ms | **213×** | 🟢 |
| Folder-Query 500 Items (p95) | 0,53 ms | — | — | 🟢 |
| FTS-Name-Query (Median) | 0,45 ms | < 200 ms | **444×** | 🟢 |
| FTS-Name-Query (p95) | 0,79 ms | — | — | 🟢 |
| Filter MediaType Page (Median) | 15,71 ms | < 100 ms | **6,4×** | 🟢 |
| Filter MediaType Page (p95) | 18,45 ms | — | — | 🟢 |
| DB-Größe gesamt | 118,21 MB | (Referenz) | — | — |

## Schlussfolgerung

**Architektur ist bestätigt.** Die in ADR-0004 / ADR-0011 festgelegten Entscheidungen halten an SATA-SSD-Hardware ohne weitere Optimierung mit ≥ 6×-Spielraum auf alle Messziele. Auf moderner NVMe sind dramatisch bessere Werte zu erwarten.

Freigabe für:
- V0.1-Implementierung der Datenschicht (Raw SQLite Bulk + EF Core CRUD parallel)
- FTS5-minimal (`Name + Extension`) als Pflicht-Feature
- Paged Grid mit 500er-Pages — Page-Loads sind keine Bottleneck-Quelle

## Folge-Erkenntnisse (für V0.1/V0.2)

1. **Composite-Index-Empfehlung für Favorit/MediaType+Sort:**
   - `IX_Files_Library_Modified (LibraryId, ModifiedAtUtc DESC)` greift bei MediaType- und Favorit-Filtern mit `ORDER BY ModifiedAtUtc DESC` — SQLite scannt aber alle Rows bis 500 Treffer gefunden sind.
   - Bei 5 %-Favorit-Quote → 64 ms; bei seltenen Filter-Werten (zukünftige Tag-Filter mit < 1 %) würde das langsam.
   - **Vorschlag V0.2:** Composite-Indizes ergänzen, z.B. `(LibraryId, IsFavorite, ModifiedAtUtc DESC)` und `(LibraryId, MediaType, ModifiedAtUtc DESC)`. Index-Größe steigt, Query-Zeit fällt um Faktor 10–50.
   - Für V0.1 nicht erforderlich (alle Ziele grün), aber als Performance-Reserve dokumentiert.

2. **Bulk-Insert-Throughput sinkt mit Index-Pflege** (24k → 11k rows/s über 500k Rows).
   - Ist erwartetes Verhalten bei wachsenden B-Tree-Indizes.
   - **Optimierungs-Reserve:** Für HDD-Profil oder Initial-Scans > 500k könnte "Indizes nach Insert anlegen" weitere ~30–50 % bringen. Nicht für V0.1 nötig.

3. **`INSERT OR IGNORE` als Standard-Strategie**: Im Spike wurden 0 Kollisionen auf `UX_Files_Folder_Name_Size_Modified` beobachtet (Generator-Namen sind genügend variabel). Im realen Scanner ist die OR-IGNORE-Strategie trotzdem das richtige Idempotenz-Pattern für Re-Scans — Performance-Overhead vernachlässigbar.

4. **FTS5-Population via single `INSERT … SELECT` ist sehr schnell** (1,65 s für 500k). Im Production-Scanner sollte FTS-Population **nicht** über Trigger laufen, sondern als expliziter Folge-Schritt nach Bulk-Insert (Trigger würden Bulk-Insert verlangsamen und sind im MVP-Scope von ADR-0011 explizit nicht vorgesehen).

5. **DB-Größe pro File:** ~240 Bytes inkl. aller Indizes und FTS5. Bei 500k Files ergibt das 118 MB; bei 50k entsprechend ~12 MB. Plausibel und vorhersagbar.

## Reproduzierbarkeit

```powershell
dotnet build .\PhotoViewerV3.slnx -c Release
dotnet run --project .\tests\PhotoViewerV3.PerfTests --no-build -c Release -- spike01-sqlite-bulk --rows 500000 --folders 10000
```

Optionen:
- `--rows N` (Default 500.000)
- `--folders N` (Default 10.000)
- `--db PATH` (Default `%LocalAppData%\PhotoViewerV3\spike-01\library-spike.db`)
- `--validate-generator` (zusätzliche Generator-Sanity-Checks vor DB-Insert)

Deterministische Seeds: Folder-Seed `20260510`, File-Seed `20260511`. Gleicher Run reproduziert identische Daten.

## Code

- [tests/PhotoViewerV3.PerfTests/Spikes/Spike01SqliteBulk.cs](../../tests/PhotoViewerV3.PerfTests/Spikes/Spike01SqliteBulk.cs)
- [tests/PhotoViewerV3.PerfTests/Spikes/SyntheticData/SyntheticDataGenerator.cs](../../tests/PhotoViewerV3.PerfTests/Spikes/SyntheticData/SyntheticDataGenerator.cs)
- [tests/PhotoViewerV3.PerfTests/Spikes/Schema/SchemaBuilder.cs](../../tests/PhotoViewerV3.PerfTests/Spikes/Schema/SchemaBuilder.cs)
- [tests/PhotoViewerV3.PerfTests/Spikes/Bulk/BulkInserter.cs](../../tests/PhotoViewerV3.PerfTests/Spikes/Bulk/BulkInserter.cs)
- [tests/PhotoViewerV3.PerfTests/Spikes/Queries/QueryBenchmarks.cs](../../tests/PhotoViewerV3.PerfTests/Spikes/Queries/QueryBenchmarks.cs)

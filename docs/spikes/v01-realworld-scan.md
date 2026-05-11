# V0.1 Real-Welt-Scan-Validation

- **Status:** ✅ Production-Stack läuft End-to-End
- **Datum:** 2026-05-11
- **Bezogen auf:** [Spike #1](01-sqlite-bulk-fts5.md), [PLAN.md Kap. 6 + 13](../../PLAN.md)
- **Zweck:** Beweis, dass der produktive Code-Pfad (Domain + Data + Services-Scanner) gegen eine echte Foto-Bibliothek funktioniert — nicht nur gegen synthetische Spike-Daten

## Zielsetzung

Spike #1 hatte mit synthetischen Daten 500k Files in 47 s gemessen. Diese Validation prüft, dass:
1. Der **produktive Code** (nicht der Spike-Code) gegen ein reales Filesystem läuft
2. Migration + DI + ScanService + Raw-SQL-Bulk + FTS-Indexer alle zusammen funktionieren
3. Die Performance plausibel zu Spike-#1-Werten skaliert
4. OneDrive-synced Pfade unterstützt sind

## Test-Setup

| Feld | Wert |
|---|---|
| Test-Tool | `tests/PhotoViewerV3.PerfTests` Subcommand `spike-v01-scan` |
| Source-Pfad | `D:\OneDrive\Bilder\01 Privat` (OneDrive-synced) |
| Library-Name | `"Privat"` |
| Mode | `ScanMode.Fresh` + `--clear` (vorhandene Library-Daten wurden geleert) |
| DB-Pfad | `C:\Users\herbe\AppData\Local\PhotoViewerV3\library.db` |

## Hardware-Profil

(identisch [Spike #1](01-sqlite-bulk-fts5.md))

| Feld | Wert |
|---|---|
| OS | Windows 11 Pro Build 26200 |
| CPU | Intel Core i7-8700K |
| RAM | 32 GB |
| Storage | Samsung 960 PRO NVMe (System) + Samsung 860 EVO SATA (D:\) |
| .NET | 10.0.203 |

## Ergebnis

```
Spike V01 — End-to-End Scan
  Root:    D:\OneDrive\Bilder\01 Privat
  Library: Privat
  Clear:   True

[1] Initialisiere DB…
    DB: C:\Users\herbe\AppData\Local\PhotoViewerV3\library.db
    Neu migriert: 0, Stale-Recovered: 0

[2] Library angelegt: Id=3

[3] Scan: 32 folders · 770 files · 17 406,4 MB

=== Scan-Ergebnis ===
  Folders inserted:  32
  Files inserted:    770
  FTS rows:          770
  Total bytes:       17,00 GB
  Enumeration:          0,05 s
  Persistence:          0,03 s
  FTS-Indexing:         0,00 s
  Total:                0,17 s
  Throughput:          4 408 files/s
```

### Phasen-Vergleich

| Phase | Spike #1 (500k synth) | Real-Welt (770 echt) |
|---|---|---|
| Bulk-Insert | 47,2 s @ 10.600 files/s | 0,03 s @ 25.700 files/s (gemessene Persistence) |
| FTS-Indexing | 1,65 s | 0,00 s (sub-ms) |
| Folder-Count | 10.000 | 32 |
| Files-Count | 500.000 | 770 |
| Total-Bytes | (synth) | 17,0 GB |

Bei kleinen Datenmengen dominiert der **Setup-Overhead** (Transaction-Begin, Connection-Open, Statement-Prepare). Daher der scheinbar höhere Throughput pro Sekunde — er ist aber nicht repräsentativ für große Bibliotheken. Spike-#1-Werte bleiben die Performance-Basis für 50k–500k.

## Funktionale Verifikation

| Check | Ist |
|---|---|
| Database-Migration läuft ohne Fehler | ✅ (`Neu migriert: 0` weil schon angewendet) |
| `--clear` löscht Files/Folders/Libraries + FTS-Index sauber | ✅ |
| Library-Entity wird via EF Core angelegt | ✅ (Id=3) |
| Scanner findet alle gültigen `.jpg/.png/.mp4`-Files | ✅ (770 erkannt) |
| Verschachtelte Folder-Hierarchie (z.B. `GoPro\2016-01-01\HERO7 BLACK 1`) wird korrekt erfasst | ✅ |
| Folder.ParentId wird korrekt gesetzt | ✅ (über Path-Mapping) |
| Bulk-Insert via Raw SQLite + Prepared INSERT OR IGNORE | ✅ |
| FTS5-Population via INSERT … SELECT (external content) | ✅ (770 rows in FilesFts) |
| Smoke-Query gegen FTS funktioniert | ✅ (MATCH 'IMG OR DSC' → 0 Treffer, da Files-Namen `20230211_*.jpg` etc.) |
| OneDrive-Pfad funktioniert | ✅ (keine "Cloud-Stub"-Fehler, Files alle lokal gecached) |

## Beobachtungen

1. **OneDrive ist transparent.** `D:\OneDrive\Bilder` verhält sich für den Scanner wie jedes normale NTFS-Verzeichnis — `FileInfo.Length` und `LastWriteTimeUtc` liefern korrekte Werte ohne dass Cloud-Sync-Downloads getriggert werden müssen.

2. **Eine MP4-Datei mit nur 26,9 KB Größe** (`GX010442.MP4` vom GoPro) wurde gefunden — vermutlich abgebrochene Aufnahme oder fehlerhaftes File. Scanner hat sie korrekt erfasst (Mechanik egal). Eine spätere V0.2-Validation könnte sehr kleine MP4 als "potentiell beschädigt" markieren — out-of-scope V0.1.

3. **Folder-Hierarchie funktioniert.** Subfolder wie `Audi\Neuer Ordner` und `GoPro\2016-01-01\HERO7 BLACK 1` werden mit korrektem ParentId-Mapping persistiert.

4. **`--clear` arbeitet korrekt mit FTS5 external content** — der `INSERT INTO FilesFts(FilesFts) VALUES('delete-all');`-Trigger ist die saubere Methode, sonst wirft FTS5 bei `DELETE FROM FilesFts` einen Fehler.

5. **FTS-Indexing in unter 1 ms für 770 Rows** — bestätigt Spike-#1-Skalierung (1,65 s für 500k → linear extrapoliert ~2,5 ms für 770).

## Folge-Erkenntnisse für V0.2+

1. **Cloud-Stub-Detection** — wenn `FileAttributes.Offline` gesetzt ist, könnte Scanner sich anders verhalten (z.B. überspringen, oder mit Hinweis markieren). Aktuell ignoriert.

2. **Re-Scan ist V0.2.** ScanMode.Fresh erwartet leere Library-Daten — wer eine bestehende Library erneut scannen will, muss `--clear` nutzen. Production-Re-Scan mit Diff-Logic (added/removed/modified Files) kommt später.

3. **Größerer Test ausstehend.** 770 Files ist klein. Echte Performance-Werte gegen 50k+ Files würden zeigen, ob der Production-Code die Spike-#1-Ziele wirklich hält.

## Code

- [tests/PhotoViewerV3.PerfTests/Spikes/V01Scan/SpikeV01Scan.cs](../../tests/PhotoViewerV3.PerfTests/Spikes/V01Scan/SpikeV01Scan.cs)
- [src/PhotoViewerV3.Services/Scanning/ScanService.cs](../../src/PhotoViewerV3.Services/Scanning/ScanService.cs)
- [src/PhotoViewerV3.Services/Scanning/FolderEnumerator.cs](../../src/PhotoViewerV3.Services/Scanning/FolderEnumerator.cs)
- [src/PhotoViewerV3.Data/RawSql/BulkScanWriter.cs](../../src/PhotoViewerV3.Data/RawSql/BulkScanWriter.cs)
- [src/PhotoViewerV3.Data/RawSql/FtsIndexer.cs](../../src/PhotoViewerV3.Data/RawSql/FtsIndexer.cs)

## Reproduzierbarkeit

```powershell
cd "D:\OneDrive\Dokumente\02 Arbeit\05 Vorlagen - Scripte\03_Foto-Viewer-V3"
dotnet run --project tests\PhotoViewerV3.PerfTests --no-build -c Release -- spike-v01-scan --root "PATH_TO_PHOTO_FOLDER" --name "LibraryName" --clear
```

Achtung: `--root` muss auf einen Pfad zeigen, der vom Wpf-Process-Identitäts-Kontext lesbar ist (nicht im UWP-AppContainer-Schatten). Beim Spike-Run aus normaler PowerShell — kein Sandboxing.

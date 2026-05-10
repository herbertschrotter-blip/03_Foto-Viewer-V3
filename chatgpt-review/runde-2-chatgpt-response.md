# Runde 2 — ChatGPT (GPT-5) Antwort

> Datum: 2026-05-10
> Kanvas-Titel: Review Runde 2

## Kurzposition GPT-5

- **WIC bleibt richtig** für JPG/PNG-Thumbnails im MVP
- **Thumbnail-Queue produktionsnah** bauen, nicht als kleine `PriorityQueue`-Spielerei
- **OperationBatches Pflicht**, auch vor Plan-Ordnern
- **Animated Hover Default AUS**, on-demand + stark limitiert
- **V0.1 muss Such-/Filter-Grundlagen** enthalten (sonst 500k unbenutzbar)
- **Persons/Faces nicht in V0.1-Schema** — widerspricht Claude

## Stellungnahme zu Defaults

Alle 7 Defaults aus Claude akzeptiert. **Einer Widerspruch:** Persons/Faces nicht im V0.1-Schema. Begründung:
1. Modell unstabil (Embedding-Dim, Crops, Library-Scope offen)
2. Leere Tabellen sind trotzdem Vertrag
3. V0.1 hat wichtigere Risiken
4. Tags/Plans brauchen ohnehin Migrations-Disziplin später

## Vertiefungen geliefert

### 1a. WIC vs ImageSharp
- Performance-Erfahrungswerte: WIC 3-8s vs ImageSharp 5-12s für 100 JPEGs
- Memory-Lifecycle-Code: `FileShare.ReadWrite | FileShare.Delete`, `BitmapCacheOption.OnLoad`, `.tmp` + atomic Move
- ImageSharp v3 Lizenz: handhabbar, aber unnötige Dependency
- Für Animated Hover: FFmpeg, nicht ImageSharp
- Faustregel: 500k × 30ms = 4,2h seriell, ~42min mit 6 Workern

### 1b. Multi-Channel Queue — vollständiger Service
- 4 Channels + ConcurrentDictionary für Dedupe
- ThumbJobState mit Versioning
- `PromoteIfNeeded`-Logik
- Worker-Loop mit Stale-Detection (`IsCurrent`)
- Adaptive Delay basierend auf UI-Aktivität
- FFmpeg-Timeout via `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter(3s/8s)`
- Memory-Footprint-Korrektur: 50-80MB für 500k Keys (nicht 25MB wie Claude angenommen)
- Background batched 1000-5000, nicht alle auf einmal
- Crash-Recovery via `Thumbnails.Status` + `AttemptCount` + `.tmp`-Write

### 1c. OperationBatches — Crash-Recovery-UX
- Recovery-InfoBar/Dialog beim Start mit konkreten Optionen
- "Fortsetzen" = nur pending+failed Operationen, nicht nochmal alles
- "Reparieren" = Konsistenz-Check pro Operation mit 7 Source/Dest-Fällen
- Heuristik für extern verschobene Dateien: name+size+modifiedAt match
- Vollständiger DB→Dateisystem-Check **nicht bei jedem Start** (Startzeit-Killer bei 500k), nur offene Batches

### 1d. Animated Hover-Thumbs
- Default `Enabled = false`, `GenerateOnDemand = true`
- DelayMs 350, MaxCacheSizeGB 2, MaxConcurrentJobs 1, MinCacheAgeHours 24
- ViewModel-Pattern mit CancellationTokenSource
- Wichtig: Job NICHT hart canceln bei MouseLeave (Thrashing!), nur UI-CTS canceln
- LRU-Eviction non-blocking, bei Limit static poster behalten
- Mit Erstaktivierung: explizites Opt-in mit Größenhinweis

### 1e. MVP-Schnitt für 50k-500k
- V0.1 MUSS Suche+Filter enthalten (sonst 500k unbenutzbar)
- FTS5 oder indexed search — Empfehlung: FTS5 minimal in V0.1 wenn produktiv
- 6 Pflicht-Spikes vor V0.1 (FTS5 als 6.)
- V0.1 produktiv = Viewer + Indexer (nicht Organizer)
- V0.2 = Video + Operations
- V0.3 = Plan-Organizer

## 2. Skalierungs-Gefahren bei 500k

### Initial-Scan-Performance
- Naiv EF SaveChanges: 16-83 Min für 500k = inakzeptabel
- EF AddRange + Clear in 2000er-Batches: Minuten-Bereich
- **Raw `Microsoft.Data.Sqlite` prepared INSERT in Transaction: deutlich schneller (<5min)**
- `Directory.EnumerateFiles` streaming, keine FileInfo-Materialisierung

### DB-Größe Faustregel (500k MVP)
- Files+Indizes 100-300MB
- Thumbnails-Metadaten 100-300MB
- FTS5 Name-Index 50-200MB
- Operations-History 10-500MB
- **Gesamt MVP: 300MB-1GB**

### Pflicht-Indizes
```sql
CREATE INDEX IX_Files_Library_Folder ON Files(LibraryId, FolderId);
CREATE INDEX IX_Files_Folder_Name ON Files(FolderId, Name);
CREATE INDEX IX_Files_Library_MediaType ON Files(LibraryId, MediaType);
CREATE INDEX IX_Files_Library_Modified ON Files(LibraryId, ModifiedAtUtc DESC);
CREATE INDEX IX_Files_Library_Favorite ON Files(LibraryId, IsFavorite);
CREATE INDEX IX_Thumbnails_File_Size_Kind ON Thumbnails(FileId, Size, Kind);
CREATE INDEX IX_FileOperations_Batch_Status ON FileOperations(BatchId, Status);
```

### PRAGMA
```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
PRAGMA temp_store=MEMORY;
```

### WPF VirtualPanel-Limits
- 500k Items in einer ObservableCollection ist falsch, auch wenn virtualisiert
- **Paging Pflicht**: pro Folder/Search/Datumgruppe 200-1000 Items
- Query-Service mit Offset/Limit als Architektur-Vorgabe

### Memory-Leak-Killer
1. BitmapImage ohne OnLoad → File-Lock
2. Nicht-gefreezte BitmapSource über Threads
3. Event-Handler in recycelten Tile-Controls
4. ObservableCollection mit Massen
5. EF ChangeTracker mit hunderttausenden Entities
6. Task/CTS pro Tile nicht disposed
7. LibVLC-Player nicht disposed beim Lightbox-Wechsel
8. FFmpeg-Prozesse bei Timeout nicht gekillt

**Designregel: UI hält IDs und kleine DTOs, keine EF Entities, keine großen Images, keine offenen Streams.**

### Scanner braucht Prioritätslogik
Wenn User unindizierten Ordner öffnet: Critical-Priority im Scanner, nicht nur Thumbnailer.

## 3. Neue MVP-Phasen

### Vor V0.1: 6 Pflicht-Spikes
1. Virtualisiertes/paged Grid mit 500k Dummy-Metadaten (1-2 Tage)
2. SQLite Bulk Insert + WAL + Query Benchmarks 500k (1-2 Tage)
3. WIC Thumbnail Benchmark 1k echte JPG/PNG (0,5-1 Tag)
4. FFmpeg timeout/cancel für MP4 (1 Tag)
5. LibVLCSharp Lightbox Spike (1-3 Tage je nach Edge-Cases)
6. **NEU: Minimal FTS5 / indexed search 500k Spike (0,5-1 Tag)**

### V0.1 — Produktiver Viewer + Indexer
- Libraries+Folders+Files+Thumbnails+OperationBatches+FileOperations+Settings
- Initialscan mit Progress, Pause/Cancel/Resume-light
- FolderTree + Container-State mit Subfolder-Kacheln
- Paged virtualisiertes Thumbnail-Grid
- WIC-Thumbnails JPG/PNG, optional minimal MP4 statisch
- Suche+Filter+Favorit
- Lightbox Bilder
- Delete-to-Recycle + Cache-Status

### V0.2 — Video + robuste Operations
- LibVLCSharp Lightbox MP4
- Move/Rename mit OperationBatches voll
- Recovery-UI
- Thumbnail priorities komplett

### V0.3 — Plan-Ordner
- Plans+PlanFolders+PlanAssignments
- Drag&Drop + Kontextmenü
- Preflight + Apply Move-only + Block-Undo

### Später (separate Epics)
FTS5 erweitert, Tags, Albums, Animated Hover, File Sorter, Archive, pHash, Faces

### Wenn 4 Wochen MVP hart sind, streichen aus V0.1
- LibVLCSharp produktiv (nur Spike + extern öffnen)
- Animated Hover komplett
- Tags/Albums komplett
- Plan-Ordner komplett
- Multi-Library UI
- EXIF-Panel
- Settings-UI umfangreich (nur Basis)

## 4. Adaptive Throttling

`PauseBackgroundDuringVideoPlayback` reicht NICHT bei 500k. Vollständiger `WorkBudgetService` mit:
- FPS-Messung via `CompositionTarget.Rendering`
- Inputs: User aktiv, Lightbox offen, Video Playback, FPS<45, CPU>80%, Akku
- Regeln: idle/active/lightbox/video/battery
- Worker fragen Budget vor Background-Job ab
- Critical/Viewport dürfen Budget umgehen

Vollständiger Code für `UiResponsivenessMonitor` und `WorkBudgetService` geliefert.

## ADR-Liste (10 Stück)

```
ADR-001: WPF + .NET 10 + CommunityToolkit.Mvvm
ADR-002: SQLite zentral unter LocalAppData, eine aktive Library im MVP
ADR-003: WIC für JPG/PNG, FFmpeg für Video-Thumbnails
ADR-004: LibVLCSharp als primärer Video-Player ab V0.2
ADR-005: Thumbnail-Pipeline Multi-Channel-Queue + Dedupe + Status-Tabelle
ADR-006: OperationBatches/FileOperations für alle destruktiven Operationen
ADR-007: PlanAssignments 1:1 pro Plan, Move-only im MVP
ADR-008: Animated Hover default off, on-demand, LRU-limitiert
ADR-009: KEINE Face-Recognition-Tabellen im V0.1-Schema
ADR-010: V0.1 enthält Suche/Filter minimal wegen 50k-500k
```

## Minimal V0.1-Schema

**Drin:** Libraries, Folders, Files, Thumbnails, OperationBatches, FileOperations, Settings, optional FilesFts

**Nicht drin:** Tags, Albums, Plans, PlanFolders, PlanAssignments, Persons, Faces

## ✅ Einigkeit
- User-Constraints sinnvoll, MVP klarer
- `Libraries`+`LibraryId` im Schema, eine aktive Library
- Zentrale DB in `%LocalAppData%`
- JPG/PNG/MP4: WIC + FFmpeg
- Move-only für Plan Apply
- WPF/.NET 10 bleibt
- LibVLCSharp produktiv eher V0.2
- OperationBatches Pflicht
- Multi-Channel Queue
- Suche/Filter müssen früher kommen (V0.1)
- Animated Hover nur on-demand limitiert

## ⚠️ Widerspruch
- Persons/Faces NICHT in V0.1 (verfrühte Schema-Bindung)
- `PriorityQueue<T,P>` allein zu schwach
- `Files.Modified > Thumbnails.GeneratedAt` allein zu dünn → `Status`+`AttemptCount`+`.tmp`
- Kein vollständiger DB→Dateisystem-Check bei jedem Start
- Animated Hover NICHT default AN
- 500k Items NICHT als ObservableCollection → Paging Pflicht
- EF Core naiv für Initialscan zu langsam → Raw SQLite

## ❓ Rückfragen (6 Stück)
1. V0.1 produktiver Viewer in 4 Wochen ODER nur technische Validierung? (steuert FTS5)
2. Storage: lokale SSD / externe HDD / NAS? (steuert Throttling)
3. MP4-Video in V0.1 in Lightbox ODER erst V0.2 (statisches Thumb + extern öffnen)?
4. Favorit/Rating in V0.1? (Favorit billig+nützlich, Rating kann warten)
5. DB-Reset während Entwicklung bis V0.3 weiter OK ODER ab V0.1 Migration-Disziplin?
6. Sichtbare technische Status-Anzeigen ("Scan 128k/500k", "Thumbs pending", "Index wird aufgebaut")?

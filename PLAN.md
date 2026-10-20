# PhotoViewer V3 — Implementation-Plan (C# Port)

> **Stand:** 2026-10-19 (Release 0.2.0)
> **Quelle:** Konsolidiert aus 3 ChatGPT-Review-Runden mit GPT-5 (siehe `chatgpt-review/`)
> **Status:** ✅ V0.1 und V0.2 umgesetzt, Teile von V0.3/V0.4 vorgezogen (File Sorter, Flatten & Move, Rating,
> Formatregister) — Stand je Punkt in Kap. 6 und 7 (✅ umgesetzt · 🟡 teilweise · ⬜ offen); Änderungen je Version in
> [CHANGELOG.md](CHANGELOG.md), Entscheidungen seit dem Plan in [ADR-0016 … 0019](docs/adr/README.md)

---

## Inhaltsverzeichnis

1. [Vision & Ausgangslage](#1-vision--ausgangslage)
2. [Stack-Entscheidungen](#2-stack-entscheidungen)
3. [Solution-Layout](#3-solution-layout)
4. [Datenbank-Schema](#4-datenbank-schema)
5. [15 finale ADRs](#5-15-finale-adrs)
6. [V0.1-Scope](#6-v01-scope)
7. [Phasen-Roadmap](#7-phasen-roadmap)
8. [6 Pflicht-Spikes](#8-6-pflicht-spikes)
9. [Tag-1–3-Implementation-Plan](#9-tag-13-implementation-plan)
10. [NuGet-Pakete](#10-nuget-pakete)
11. [Storage-Profile](#11-storage-profile)
12. [Offline-vs-Missing-Guard](#12-offline-vs-missing-guard)
13. [Thumbnail-Pipeline](#13-thumbnail-pipeline)
14. [Migration-Disziplin](#14-migration-disziplin)
15. [Risiken & Mitigation](#15-risiken--mitigation)
16. [UI/UX](#16-uiux)

---

## 1. Vision & Ausgangslage

### Was ist das Projekt
Native Windows-Desktop-Anwendung zum Ansehen, Sortieren und Verschieben von Foto-/Video-Bibliotheken (50.000–500.000 Dateien). Ablösung der bestehenden PowerShell-Web-App (HTTP-Server-Notlösung) durch eine richtige Desktop-App.

### Constraints
| | |
|---|---|
| **Plattform** | Windows-only |
| **User** | Single-User, lokal, keine Auth |
| **Bibliotheks-Größe** | 50.000–500.000 Dateien |
| **Storage** | gemischt — wandert zwischen lokaler SSD, externer HDD, NAS |
| **Formate** | Formatregister `IMediaFormatRegistry` ([ADR-0016](docs/adr/0016-formatregister.md)): gängige Foto-Formate (JPG, PNG, GIF, BMP, TIFF, WebP, HEIC/HEIF, AVIF, Kamera-RAW) und Video-Container (MP4, MOV, MKV, AVI, WMV, MTS, …); WIC + Windows-Codec-Pakete, FFmpeg als Fallback — ursprünglich nur JPG / PNG / MP4 |
| **Plan-Apply-Modus** | nur Move im MVP, Copy später |
| **Distribution** | Portable Ordner via `dotnet publish` — seit 0.2.26 `build/publish.ps1`: self-contained `win-x64`, Ordner + ZIP unter `artifacts/`, kein Single-File, kein Trimming; FFmpeg nicht im Paket (PATH/winget), Daten unter `%LocalAppData%\PhotoViewerV3` |
| **V0.1-Aufwand** | 8–10 Wochen, robust release-fähig |

### Hauptfeatures
- Thumbnail-Grid mit Lazy Loading + Virtualisierung + Paging
- Lightbox-Viewer (Bilder + Videos via LibVLCSharp)
- Foto-Zoom + Pfeiltasten- und Button-Navigation
- Datei-Operationen: Verschieben / Kopieren / Umbenennen / Löschen → Papierkorb
- **Plan-Ordner** (virtuelle Sortier-Schablone, später auf Dateisystem anwendbar)
- Tags + Favoriten + Bewertungen
- Suche via SQLite FTS5
- Filter (Bilder / Videos / Favoriten)
- **Token-basierter File-Sorter** (Phase 8) — Generator für Plan-Ordner
- Auto-Extract `.zip`/`.rar`/`.7z` (Phase 8)
- Animierte Multi-Frame Video-Hover-Thumbs (Phase 8, on-demand)
- Gesichtserkennung offline (Phase 9)

---

## 2. Stack-Entscheidungen

| Komponente | Wahl | Begründung |
|---|---|---|
| **Runtime** | .NET 10 (LTS) | Microsoft, LTS bis 2028, C# 14 |
| **UI-Framework** | WPF | Mature, Windows-nativ, ideal für Media + komplexes Grid |
| **Design-System** | WPF-UI (lepoco) v4.* | Fluent / WinUI 3 Look, Mica, Segoe Fluent Icons |
| **MVVM** | CommunityToolkit.Mvvm 8.4.2 | Source-Generators, Microsoft-offiziell |
| **DI** | Microsoft.Extensions.DependencyInjection | Microsoft-Standard |
| **Hosting** | Microsoft.Extensions.Hosting (HostBuilder) | IHostedService für Background-Worker |
| **Datenbank** | SQLite | Most-deployed DB der Welt |
| **ORM** | Entity Framework Core 10 | für CRUD + Migrations |
| **Raw SQL** | Microsoft.Data.Sqlite 10 | für Bulk-Insert + FTS5 + Performance-Pfade |
| **Video-Player** | LibVLCSharp 3.9.7.1 + VideoLAN.LibVLC.Windows 3.* | spielt alles, eigene Codecs |
| **Bild-Thumbs** | WIC (`BitmapDecoder`) | Windows-Built-in, kleinerer Footprint, Lizenz-frei |
| **FFmpeg-Aufruf** | **eigener `ProcessStartInfo`-Wrapper** | volle Kontrolle über CancelAfter/Kill (statt FFMpegCore) |
| **Logging** | Serilog 9.* + Sinks.File 6.* | Rolling File-Logs |
| **Tests** | xUnit 2.* + FluentAssertions 8.* | Standard |
| **Papierkorb** | Microsoft.VisualBasic.FileIO | `FileSystem.DeleteFile` mit `RecycleOption` |
| **Archive (Phase 8)** | SharpCompress | Pure C#, ZIP/RAR/7z |
| **Animierte GIFs (Phase 8)** | WpfAnimatedGif | etabliert |
| **Face Recognition (Phase 9)** | Microsoft.ML.OnnxRuntime + InsightFace | offline, kein FaceONNX-Wrapper |

---

## 3. Solution-Layout

**Multi-Project ab Tag 1** (nicht Single-Project). Reales Layout seit 0.2.0 — Module statt technischer Ordner
(`Views/`, `ViewModels/`), jedes Modul mit eigener DI-Registrierung `AddXyz()` ([ADR-0017](docs/adr/0017-modul-entkopplung-messenger.md)):

```
PhotoViewerV3.slnx                       # Solution (.slnx), Directory.Packages.props = Central Package Management
build/                                   # publish.ps1 (portable Version → artifacts/), run-smoke.ps1 (Rauchtest gegen eine EXE)
src/
  PhotoViewerV3.Domain/                  # net10.0 — keine externen Deps
    Entities/                         # Library, MediaFile, Folder, Thumbnail, OperationBatch, FileOperation, Setting
    Enums/                            # MediaType, ThumbnailStatus/Kind, BatchStatus, FileOperationStatus, OperationType
  PhotoViewerV3.Data/                    # → Domain
    LibraryContext.cs                 # DbContext (+ DesignTimeLibraryContextFactory)
    EntitiesMapping/                  # EF Core Configurations, ValueConverters
    RawSql/                           # BulkScanWriter, FtsIndexer, FtsQueryBuilder
    Migrations/                       # EF Core (InitialV01)
  PhotoViewerV3.Services/                # → Domain + Data, WPF-frei; AddPhotoViewerServices()
    Catalog/                          # CatalogUpdateService (Favorit/Bewertung)
    Database/                         # LibraryContextFactory (+ IDatabaseAccessGate), DatabaseInitializer, SqlitePragmaInterceptor
      Relocation/                     # DatabaseRelocationService, StorageProbe, ThumbnailInventory — Katalog + Thumbnails umziehen (ADR-0026)
    Infrastructure/                   # AppDataPaths, DatabaseLocationStore (database-location.json, ADR-0026)
    Library/                          # LibraryService, LibraryCatalogCleanup
    Media/                            # MediaFormatRegistry (ADR-0016)
    Messaging/                        # Nachrichten-Records für WeakReferenceMessenger (ADR-0017)
    Operations/                       # FileOperationService, WindowsRecycleBin (ADR-0012/0018)
    Scanning/                         # ScanService, FolderEnumerator, IncrementalScanRunner, RootAvailabilityProbe, MassMissingGuard (ADR-0019)
    Sorter/                           # FileSorterService, FlattenService, SorterPatternEngine, SorterProfileStore
    Thumbnails/                       # ThumbnailService, FfmpegVideoThumbnailer, Renderer-Interfaces
  PhotoViewerV3.Wpf/                     # net10.0-windows → alle; App.xaml.cs = Host + DI, MainWindow = NavigationView
    Folders/                          # Ordnerbaum-Seite
    Gallery/                          # Galerie: Data (GalleryPageSource), ViewModels, Views, Controls, Converters, Services
    Library/                          # Bibliotheks-Seite (Namespace PhotoViewerV3.Libraries)
    Lightbox/                         # LightboxWindow/-ViewModel/-Service, ZoomPanControl, LibVlcHost, Imaging
    Settings/                         # Einstellungen, ThemeService
    Shell/                            # Navigation, DialogService, StatusBar, Converters, Smoke/ (Rauchtest)
    Sorter/                           # Sortieren-Seite (File Sorter, Flatten & Move)
    Thumbnails/                       # WicImageThumbnailRenderer (WIC lebt im Wpf-Projekt, Interface in Services)
tests/
  PhotoViewerV3.Tests/                   # xUnit: Services + Data
  PhotoViewerV3.Wpf.Tests/               # xUnit: WPF-gebundener Code (WIC, GalleryQuery, Converter)
  PhotoViewerV3.PerfTests/               # Benchmark/Console-Harness der Spikes
```

Noch nicht angelegt (Plan bleibt): `Services/Storage/` (StorageProfileService, ADR-0008), `Services/Settings/`,
`Domain/ValueObjects/`, `Domain/Abstractions/`, `Data/Repositories/`, `PhotoViewerV3.Tools/` — Settings laufen heute
über die `Settings`-Tabelle direkt im jeweiligen Service, Interfaces liegen in `Services`.

**Referenz-Regeln:**
- Domain: keine Projekt-Abhängigkeiten
- Data → Domain
- Services → Domain + Data
- Wpf → Domain + Data + Services
- Keine WPF-Referenz in Domain/Data/Services

---

## 4. Datenbank-Schema

### V0.1-Tabellen (7 Stück)

```sql
Libraries          (Id, Name, RootPath, CreatedAt, LastOpenedAt)
Folders            (Id, LibraryId, Path, ParentId, Name, ScannedAt)
Files              (Id, LibraryId, FolderId, Name, Extension, Size,
                    CreatedAtUtc, ModifiedAtUtc, MediaType,
                    Width, Height, DurationMs, Codec,
                    IsFavorite, Rating, Missing)
Thumbnails         (FileId, Size, Kind, Format, CachePath,
                    SourceModifiedAtUtc, GeneratedAtUtc, ByteSize,
                    Status, AttemptCount, LastAttemptAtUtc,
                    ErrorCode, ErrorMessage)
                   -- Status: missing | pending | generated | failed | stale
                   -- Kind: still | hover | poster
OperationBatches   (Id, OperationType, Status, CreatedAtUtc, CompletedAtUtc,
                    PlanId, Description)
                   -- Status: pending | applying | applied | undoing | undone | failed
FileOperations     (Id, BatchId, FileId, Action, SourcePath, DestPath,
                    Status, Error, ExecutedAtUtc, UndoneAtUtc)
                   -- Action: move | copy | recycle | rename | mkdir | renamefolder (0.2.5)
Settings           (Key, Value)
FilesFts           -- FTS5 virtual table (Files.Name, Extension)
```

### V0.2+ Tabellen
- LibraryRoots (Id, LibraryId, Path, Role, Name, SortOrder) + `Folders.RootId` — 0.2.5, Migration `AddLibraryRoots`,
  ADR-0020: Wurzelordner je Bibliothek, Role 0 = Eingang (beliebig viele), 1 = Archiv (genau eine; `Libraries.RootPath`
  zeigt auf sie). Indizes `UX_LibraryRoots_Library_Path`, `IX_LibraryRoots_Library_Role`, `IX_Folders_Root`.
- Tags / FileTags — umgesetzt in 0.2.6 (Migration `AddTags`, ADR-0021):
  `Tags (Id, Name UNIQUE COLLATE NOCASE, Color "#RRGGBB", SortOrder, CreatedAtUtc)`,
  `FileTags (FileId → Files ON DELETE CASCADE, TagId → Tags ON DELETE CASCADE, PK (FileId, TagId), IX_FileTags_Tag)`.
  Tags sind global (nicht je Library); Suche nach Tag-Namen per Join, nicht über `FilesFts`.
- Categories / CategoryValues / FileCategoryValues — umgesetzt in 0.2.20 (Migration `AddCategories`, ADR-0027):
  `Categories (Id, Name UNIQUE COLLATE NOCASE, SortOrder, CreatedAtUtc)`,
  `CategoryValues (Id, CategoryId → Categories ON DELETE CASCADE, Name COLLATE NOCASE, SortOrder, UNIQUE (CategoryId, Name))`,
  `FileCategoryValues (FileId → Files, CategoryId → Categories, ValueId → CategoryValues — alle ON DELETE CASCADE,
  PK (FileId, CategoryId) = ein Wert je Kategorie und Datei, IX_FileCategoryValues_Value, IX_FileCategoryValues_Category)`.
  Seit 0.2.30 (Migration `AddCategorySortIndex`, ADR-0031) ersetzt der abdeckende Index
  `IX_FileCategoryValues_Category_Value_File (CategoryId, ValueId, FileId)` den Index `IX_FileCategoryValues_Category`
  (segmentweise Kategorie-Sortierung, Kategorie-Filter, Suche nach Kategorie-Namen lesen nur den Index).
  Global wie Tags; „Neu aufbauen“ trägt die Werte über `UserDataCarryOver` (ADR-0023) mit — seit 0.2.24 in derselben
  Transaktion, die die alten Zeilen ersetzt (ADR-0028, keine Schema-Änderung).
- Albums / AlbumFiles (V0.3+)
- Plans / PlanFolders / PlanAssignments (V0.3)
- FileHashes (V0.3+ separat von Files)
- SorterProfiles / SorterTokens (Phase 8)
- ~~ExtractedArchives (Phase 8)~~ — entfällt: Archive sind Files-Zeilen mit `MediaType = 2`, der Papierkorb protokolliert über OperationBatches (ADR-0022)
- Persons / Faces (Phase 9 — eigene Migration)

### Pflicht-Indizes V0.1
```sql
CREATE INDEX IX_Files_Library_Folder ON Files(LibraryId, FolderId);
CREATE INDEX IX_Files_Folder_Name ON Files(FolderId, Name);
CREATE INDEX IX_Files_Library_MediaType ON Files(LibraryId, MediaType);
CREATE INDEX IX_Files_Library_Modified ON Files(LibraryId, ModifiedAtUtc DESC);
CREATE INDEX IX_Files_Library_Favorite ON Files(LibraryId, IsFavorite);
CREATE INDEX IX_Thumbnails_File_Size_Kind ON Thumbnails(FileId, Size, Kind);
CREATE INDEX IX_FileOperations_Batch_Status ON FileOperations(BatchId, Status);
CREATE UNIQUE INDEX UX_Folders_Library_Path ON Folders(LibraryId, Path);
CREATE UNIQUE INDEX UX_Files_Folder_Name_Size_Modified ON Files(FolderId, Name, Size, ModifiedAtUtc);
```

### PRAGMAs
```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
PRAGMA temp_store=MEMORY;
```

---

## 5. 15 finale ADRs

| ADR | Titel | Inhalt |
|---|---|---|
| **ADR-001** | Runtime + UI-Stack | .NET 10 LTS, WPF, WPF-UI als isolierter UI-Layer, CommunityToolkit.Mvvm |
| **ADR-002** | Solution-Architektur | Multi-Project ab Tag 1 (Domain/Data/Services/Wpf) |
| **ADR-003** | Catalog-Modell | Eine aktive Library, zentrale DB `%LocalAppData%\PhotoViewerV3\library.db`, LibraryId-FKs vorbereitet |
| **ADR-004** | Datenzugriff | EF Core für CRUD, Raw `Microsoft.Data.Sqlite` für Bulk/FTS5/Performance-Pfade, WAL + Single-Writer |
| **ADR-005** | Migration-Disziplin | DB-Reset bis erste produktive Nutzung, ab V0.2 strict Migrations, WAL-Checkpoint + versioniertes Backup |
| **ADR-006** | Bild- & Video-Pipeline | WIC für JPG/PNG, FFmpeg für MP4-Standbilder, LibVLCSharp für MP4-Lightbox (V0.1 Cutline: bei rotem Spike "Extern öffnen") |
| **ADR-007** | Thumbnail-Scheduling | Multi-Channel Queue (Critical/Viewport/CurrentFolder/Background) + Dedupe + Versioning + Status + `.tmp` Writes |
| **ADR-008** | Storage-Profile | StorageProfileService (LocalSsd/LocalHddOrUsb/NetworkShare/Unknown), Offline-State separat von Profil |
| **ADR-009** | Cache-Strategie | Thumbs lokal `%LocalAppData%\PhotoViewerV3\thumbs\{libraryId}\` auch bei NAS |
| **ADR-010** | UI-Datenmodell | Paged/virtualisiertes Grid via Query-DTOs, keine 500k ObservableCollection, SelectionStore nach FileId |
| **ADR-011** | Suche/Filter V0.1 | FTS5 minimal Pflicht (Files.Name+Extension), Filter Alle/Bilder/Videos/Favoriten Pflicht, Favorit ja, Rating später |
| **ADR-012** | Operation-Logging | OperationBatches/FileOperations ab V0.1 für Delete-to-Recycle, Move/Rename V0.2+ |
| **ADR-013** | Status-UI | 4 Indikatoren Pflicht: Scan-Fortschritt, Thumb-Queue, Background-Aktivität, Recovery-Hinweise |
| **ADR-014** | Animated Hover | NICHT V0.1, später default-off, on-demand, LRU-limitiert |
| **ADR-015** | Face Recognition | NICHT im V0.1-Schema, eigenes Phase-9-Epic mit eigener Migration |

> **Vollständige ADRs** (Kontext / Entscheidung / Begründung / Konsequenzen / Alternativen) liegen unter [`docs/adr/`](docs/adr/) — jeder ADR ist eine eigene Markdown-Datei nach klassischem Michael-Nygard-Template. Siehe [docs/adr/README.md](docs/adr/README.md) für Index + Lifecycle-Regeln.

---

## 6. V0.1-Scope

Status (Stand 0.2.0): ✅ umgesetzt · 🟡 teilweise · ⬜ offen

### Pflicht (nicht streichbar)
| Status | Punkt |
|---|---|
| ✅ | Multi-Project Solution |
| ✅ | SQLite-Bulk-Scan (Raw SQLite Prepared INSERT in Transaktion) |
| ✅ | FTS5 minimal (Files.Name + Extension) — mit `FtsQueryBuilder` für sichere Benutzereingaben |
| ✅ | Paged virtualisiertes Grid (200–1000 Items pro Page, keine 500k ObservableCollection) |
| ✅ | WIC-Thumbnails JPG/PNG mit `.tmp`-Write + atomic Move — plus EXIF-Orientierung, weitere Formate (ADR-0016) |
| 🟡 | MP4-statisches Thumbnail via FFmpeg + Codec-Badge — Thumbnail ✅, Codec-Badge nur im Lightbox-Info-Panel |
| ✅ | Suche + Filter (Alle/Bilder/Videos/Favoriten) + Favorit |
| ✅ | Offline-vs-Missing-Guard mit 20%-Threshold (ADR-0019) |
| ✅ | Statusbar mit 4 Indikatoren (technisch-look OK, Hauptsache zuverlässig) |
| ✅ | Image-Lightbox mit Zoom + Pfeiltasten + Buttons |
| ✅ | Delete-to-Recycle mit Confirm + OperationBatches-Eintrag |
| ✅ | Container-State (Subfolder-Kacheln wenn keine direkten Medien) |

### V0.1 nur wenn Spike grün (Cutline ~5 PT)
| Status | Punkt |
|---|---|
| ✅ | **LibVLCSharp Video-Lightbox** — Spike grün, produktiv in `Wpf/Lightbox` |
| ⬜ | **FPS-basiertes Adaptive Throttling** — fällt zurück auf StorageProfile-feste Budgets (beides offen, feste Worker-Zahl) |

### Aus V0.1 raus
| Status | Punkt |
|---|---|
| ⬜ | Animated Hover-Thumbs |
| ✅ | Move / Rename (0.2.0 über `IFileOperationService`, ADR-0018) |
| ⬜ | Plan-Ordner |
| ⬜ | Tags / Albums |
| ✅ | Rating (0.2.0: 1–5 Sterne in Galerie und Lightbox) |
| ⬜ | Face Recognition |
| 🟡 | Full-Recovery-Repair-UI — Hinweis in der Statusleiste + Undo, kein Reparatur-Dialog |
| ✅ | File-Sorter (0.2.0 aus V2 portiert, Pattern-Engine + Multi-Level; ab 0.2.16 über alle Wurzeln: Eingang → Archiv, Ziele nur unter den Wurzeln, ADR-0024) |
| ✅ | Archive: im Katalog, Inhaltsansicht, manuelles Entpacken ✅ (0.2.8, ADR-0022); Auto-Extract nach dem Scan für neue Archive in Eingängen ✅ (0.2.25, ADR-0032, Einstellung, Standard aus) |
| ⬜ | pHash Duplikat-Erkennung |

---

## 7. Phasen-Roadmap

| Status | Phase | Inhalt | Aufwand |
|---|---|---|---|
| ✅ | **Spikes (vor V0.1)** | 6 Pflicht-Spikes (3 blockierend) — alle grün, siehe `docs/spikes/` | 10–18 PT |
| ✅ | **V0.1** | Produktiver Viewer + Indexer (siehe Pflicht-Liste) | 60–90 PT |
| 🟡 | **V0.2** | LibVLC-Lightbox produktiv ✅, Move/Rename ✅, OperationBatches voll ✅ (ADR-0018), Recovery-UI 🟡 (Statusleisten-Hinweis + Undo), Thumbnail-Priorities komplett ⬜ (zwei Worker-Pools, keine 4-Prio-Queue); dazu Incremental-Scan ✅ (ADR-0019), Bibliotheksverwaltung ✅, Ordnerbaum ✅, Einstellungen ✅ | 25–40 PT |
| ⬜ | **V0.3** | Plan-Ordner: Plans/PlanFolders/PlanAssignments + Drag&Drop + Preflight + Apply Move-only + Block-Undo | 25–40 PT |
| 🟡 | **V0.4 / Phase 8** | FTS5 erweitert (Tags) ⬜, Tags/Albums-UI ⬜, Rating ✅ (vorgezogen), Animated Hover ⬜, File-Sorter ✅ (vorgezogen: Regex-Profile, Multi-Level, Import/Export; Drag&Drop-Editor ⬜), Archive im Katalog + Entpacken ✅ (vorgezogen, ADR-0022; Auto-Extract nach dem Scan ✅, ADR-0032), pHash Duplikate ⬜, Flatten & Move ✅ (vorgezogen) | 40–60 PT |
| ⬜ | **V0.5 / Phase 9** | Face Recognition (RetinaFace + ArcFace via ONNX), Auto-Clustering, Personen benennen | 30–50 PT |

**Gesamt-V0.1 brutto:** 70–108 PT = **8–10 Wochen** für 1 erfahrenen Entwickler bei 5 PT/Woche.

---

## 8. 6 Pflicht-Spikes

### Reihenfolge: blockierend zuerst

| # | Spike | PT | Blockiert | Messziele |
|---|---|---|---|---|
| 1 | **SQLite Bulk + FTS5 500k** | 2–3 | Schema, Scanner, Search-UI | Bulk-Insert <5min SSD/<15min HDD; Folder-Query 500 Items <100ms; FTS-Name-Query <200ms |
| 2 | **Virtual Grid 500k** | 2–4 | Gallery-UI, Selection | Start <1s; Page-Switch <150ms; Scroll flüssig; Memory stabil über 10min |
| 3 | **LibVLCSharp WPF Lightbox** | 3–5 | MP4 in V0.1 (sonst Cut auf "Extern öffnen") | Player-Lifecycle, Fullscreen, Focus, Multi-Monitor, Dispose ohne Leaks |
| 4 | WIC Benchmark + File-Lock | 1–2 | Thumb-Pipeline | 1k echte JPGs parallel, kein File-Lock-Leak |
| 5 | FFmpeg Cancel/Timeout | 1–2 | Video-Thumbs | Process-Kill bei kaputten MP4s, Timeout 3s/8s |
| 6 | Storage Detection | 1–2 | StorageProfileService | SSD/HDD/USB/NAS-Erkennung, UNC-Timeout, Offline-Verhalten |

### Gesamt-Spike-Aufwand: 10–18 PT (2–4 Wochen wenn parallel zur V0.1-Arbeit unmöglich)

---

## 9. Tag-1–3-Implementation-Plan

### Tag 1 — Fundament

**Ziel:** Solution startbar, DI steht, leeres MainWindow läuft.

1. Solution + Projekte anlegen (Domain, Data, Services, Wpf, Tests)
2. Target Framework: `net10.0-windows` für Wpf, `net10.0` für Rest
3. Central Package Management (`Directory.Packages.props`)
4. DI/HostBuilder in WPF App integrieren
5. Serilog Logging einrichten (Rolling File)
6. `AppDataPaths`-Service:
   - DB: `%LocalAppData%\PhotoViewerV3\library.db`
   - Thumbs: `%LocalAppData%\PhotoViewerV3\thumbs\`
   - Logs: `%LocalAppData%\PhotoViewerV3\logs\`
7. Leeres MainWindow mit WPF-UI NavigationView
8. Smoke-Run

**Definition-of-Done:**
- App startet
- MainWindow sichtbar
- Logfile geschrieben
- DI funktioniert
- DB-Pfad geloggt
- Keine fachliche Funktionalität nötig

### Tag 2 — SQLite/FTS/Bulk Spike (vor UI weitermachen!)

**Ziel:** 500k synthetische FileRows einfügen, Bulk-Performance messen.

1. 500k synthetische Files generieren (80% jpg/png, 20% mp4, 10k Folders)
2. Realistische Namen: `IMG_20260510_123456.jpg`, `Urlaub_Italien_001.mp4`
3. Bulk-Insert via `Microsoft.Data.Sqlite` Prepared Command in Transaction
4. Indizes anlegen
5. FTS5 befüllen
6. Queries messen (Folder-Page, FTS-Name, Filter)

**Messziele:**
- Bulk-Insert 500k: <5 min auf SSD, <15 min HDD
- Folder-Query 500 Items: <100ms
- FTS-Name-Query: <200ms
- Filter MediaType-Page: <100ms

**Wenn Werte nicht erreichbar** → Data-Design anpassen, bevor UI gebaut wird.

### Tag 3 — Virtual Grid Spike

**Ziel:** WPF Shell + paged GalleryView.

1. WPF Shell zeigt GalleryView
2. QueryService liefert DTO-Pages (200–1000 Items)
3. Grid rendert Pages mit Platzhalter-Thumbs (noch keine echten)
4. Scroll/Filter/Search-Simulation
5. SelectionStore per FileId

**Messziele:**
- Start Gallery <1s nach DB bereit
- Page-Switch/Load <150ms
- Scroll sichtbar flüssig
- Memory stabil nach 10min Scrollen
- Keine 500k ObservableCollection

**Wenn Standard-Panel nicht reicht** → Custom/Third-Party prüfen, alternative UI-Strategie.

### Tag 4+ — danach in dieser Reihenfolge
4. LibVLCSharp Spike (3–5 PT) — entscheidet V0.1-Cutline
5. WIC + FFmpeg Thumbnail-Spikes
6. Storage-Detection-Spike
7. **Erst dann:** echte V0.1-Implementation in vertikalen Slices

---

## 10. NuGet-Pakete

### Directory.Packages.props (Central Package Management)

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- EF Core + SQLite -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.7" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.7" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.7" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.7" />

    <!-- Hosting + DI -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.7" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.7" />

    <!-- MVVM + WPF -->
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="WPF-UI" Version="4.0.0" />

    <!-- Video -->
    <PackageVersion Include="LibVLCSharp.WPF" Version="3.9.7.1" />
    <PackageVersion Include="VideoLAN.LibVLC.Windows" Version="3.0.21" />

    <!-- Logging -->
    <PackageVersion Include="Serilog.Extensions.Hosting" Version="9.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="6.0.0" />

    <!-- Tests -->
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="FluentAssertions" Version="8.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
  </ItemGroup>
</Project>
```

### Hinweise
- **Kein FFMpegCore** — eigener `ProcessStartInfo`-Wrapper für volle CancelAfter/Kill-Kontrolle
- **WpfAnimatedGif** erst in Phase 8 (nicht V0.1)
- **SharpCompress** erst in Phase 8 (nicht V0.1)
- **Microsoft.ML.OnnxRuntime** erst in Phase 9

---

## 11. Storage-Profile

### Konzept-Trennung
```
StorageAvailability = Library-Root erreichbar? (Online/Offline)
StorageProfile      = Wie aggressiv arbeiten? (SSD/HDD/NAS)
```

### 3 konkrete Profile

#### LocalSsd
```
ScanBatchSize:                  5000
DbInsertBatchSize:              5000
MaxImageWorkers:                max(2, CPU-2)
MaxVideoWorkers:                2 (CPU≥8) / 1
MaxConcurrentDiskReads:         6
BackgroundBatchDelayMs:         0–10
FtsBatchSize:                   5000
ThumbnailQueueBatchSize:        2000
AggressiveBackgroundThumbs:     true
```

#### LocalHddOrUsb (auch Default bei Unsicherheit)
```
ScanBatchSize:                  1000–2000
DbInsertBatchSize:              1000–2000
MaxImageWorkers:                2
MaxVideoWorkers:                1
MaxConcurrentDiskReads:         2
BackgroundBatchDelayMs:         50–150
FtsBatchSize:                   2000
ThumbnailQueueBatchSize:        500–1000
AggressiveBackgroundThumbs:     true (limitiert)
```

#### NetworkShare (NAS / UNC)
```
ScanBatchSize:                  500–1000
DbInsertBatchSize:              1000
MaxImageWorkers:                1–2
MaxVideoWorkers:                0 (on-demand only)
MaxConcurrentDiskReads:         1–2
BackgroundBatchDelayMs:         150–500
FtsBatchSize:                   1000
ThumbnailQueueBatchSize:        250–500
RootAvailabilityCheckTimeoutMs: 1500–3000
AggressiveBackgroundThumbs:     false  ← Bilder ja, Videos nur on-demand
```

### Detection-Strategie
- `GetDriveType` für Remote/Removable/Fixed
- UNC-Erkennung: `path.StartsWith("\\\\")`
- SSD/HDD optional via WMI `MSFT_PhysicalDisk.MediaType`
- **Bei Unsicherheit konservativ als HDD/USB** (Performance verlieren, nicht Vertrauen)
- **DeviceIoControl-Komplexität** erst später, nicht im MVP

---

## 12. Offline-vs-Missing-Guard

### Grundregel (kritisch!)
> Nur wenn LibraryRoot erreichbar ist, dürfen einzelne Files als Missing markiert werden.
> Wenn LibraryRoot nicht erreichbar: Library offline, KEIN File-Missing ändern.

### Algorithmus
- `CheckLibraryAvailabilityAsync` mit Timeout (UNC 3s, lokal 1s)
- Vor jedem Scan-Batch: Root-Recheck
- Bei IOException während Scan: Root-Recheck → wenn offline, Mark Library Offline, return
- **Mass-Missing-Threshold:** bei `>10.000` ODER `>20%` der Library NICHT auto-committen → UI-Dialog
- **Mehrere Wurzeln (ADR-0020, ab 0.2.5):** Erreichbarkeit, Root-Recheck und Mass-Missing-Guard gelten je Wurzel.
  Fresh/ClearAndRescan starten nur, wenn alle Wurzeln erreichbar sind; der Incremental-Scan überspringt eine
  Offline-Wurzel (keine Missing-Markierung dort), gleicht die übrigen ab und meldet `LibraryOffline` mit ihrem Pfad.
- **Neu aufbauen (ADR-0028, ab 0.2.24):** der bisherige Katalog bleibt während der Enumeration unangetastet; Sichern
  der Benutzerdaten, Löschen der alten Zeilen, Einfügen, Zurückschreiben und Suchindex laufen in einer Transaktion —
  Abbruch, Offline-Fehler, Ausnahme oder Absturz davor lassen Katalog und Benutzerdaten unverändert.

### UI bei Storage offline
```
Bibliothek offline: E:\Fotos ist nicht erreichbar.
Bitte Laufwerk anschließen oder Netzwerkverbindung prüfen.
[Erneut prüfen] [Andere Bibliothek wählen]
```

### UI bei Threshold-Überschreitung
```
Ungewöhnlich viele Dateien fehlen
Beim Scan wurden 142.381 fehlende Dateien erkannt.
Das kann passieren, wenn ein Laufwerk nicht vollständig verfügbar ist.
[Erneut prüfen] [Als fehlend markieren] [Scan abbrechen]
```

---

## 13. Thumbnail-Pipeline

### Speicherort
`%LocalAppData%\PhotoViewerV3\thumbs\{libraryId}\{fileId}_{size}_{kind}.jpg` — bzw. `{Datenordner}\thumbs\…`, wenn der
Katalog in den Einstellungen umgezogen ist: der Cache liegt immer neben `library.db` und zieht mit (ADR-0026).

- **Naming**: `42_320_still.jpg` für File-ID 42 in 320px statisches Thumb
- **Animierte (Phase 8)**: `42_320_hover.gif`
- 3 Größen: 160 / 320 / 640
- Validation via DB: `Files.ModifiedAtUtc != Thumbnails.SourceModifiedAtUtc` → Re-generate

### Multi-Channel Queue (4 Prios)

| Prio | Trigger |
|---|---|
| 🔴 Critical | Lightbox-Bild offen (640px + Prefetch ±2) |
| 🟠 Viewport | sichtbare Items im Grid (debounced 100ms) |
| 🟡 Current Folder | geöffneter Ordner, außerhalb Viewport |
| 🟢 Background | Erst-Indexierung, andere Ordner |

### Mechanik
- 4 separate `Channel<ThumbJob>` (System.Threading.Channels)
- `ConcurrentDictionary<ThumbKey, ThumbJobState>` für Dedupe + Versioning
- Stale-Detection beim Dequeue
- 1 Job = 1 Datei → Pause-Punkt zwischen Jobs (kein Hard-Cancel mitten in WIC)
- FFmpeg-Jobs: `CancellationTokenSource.CancelAfter(3s/8s)` für Timeout
- Bilder: fertiglaufen lassen
- Worker-Pool: Image-Worker `max(1, CPU-2)`, FFmpeg-Worker max 1–2
- Disk-Concurrency: `SemaphoreSlim` limitiert Parallel-Reads
- **Background batched 1000–5000**, nicht alle 500k auf einmal enqueuen

### Status-Tabelle
```sql
Thumbnails.Status: missing | pending | generated | failed | stale
Thumbnails.AttemptCount, LastAttemptAtUtc, ErrorCode, ErrorMessage
```
Crash-Recovery: beim App-Start `UPDATE Thumbnails SET Status='stale' WHERE Status='pending';`

### Atomic Write
1. Generate to `{file_id}_{size}_{kind}.jpg.tmp`
2. `File.Move(tempPath, finalPath, overwrite: true)`
3. Halbfertige Files niemals als final sichtbar

### WIC-Encoder (V0.1)
- `BitmapDecoder.Create` mit `BitmapCacheOption.OnLoad` (Stream-Lock vermeiden)
- `FileShare.ReadWrite | FileShare.Delete` (Cloud-Sync-tolerant)
- `BitmapSource.Freeze()` vor Übergabe
- Niemals `ImageSource` im Service speichern → nur Pfad/ID

---

## 14. Migration-Disziplin

### Grenze
**Erste produktive Nutzung** — nicht "letzter Tag der Entwicklung".

- Bis dahin: `EnsureDeleted/EnsureCreated` OK, DB-Reset frei
- Ab dann: nur `Database.MigrateAsync()`
- Spätestens ab V0.2

### Auto-Migration beim App-Start

```
App-Start
  → DB-Version prüfen
  → Wenn Migration nötig:
    → Dialog "Datenbank wird aktualisiert"
    → WAL Checkpoint (`PRAGMA wal_checkpoint(TRUNCATE)`)
    → Versioniertes Backup: library.db.backup-YYYYMMDD-HHMMSS-vX.Y.Z-to-vX.Y.Z+1
    → Migration ausführen (in Transaktion soweit möglich)
    → Schema-Version erst am Ende setzen
    → Bei Erfolg: normal weiter
    → Bei Fehler: Restore-Dialog mit Backup-Pfad + Log
```

### Bei Migrationsfehler
```
Datenbank-Aktualisierung fehlgeschlagen
Backup wurde erstellt: ...\library.db.backup-...
Fehler: <Message>
[Backup wiederherstellen] [App schließen] [Log öffnen]
```
**Nie mit halb migrierter DB normal starten.**

---

## 15. Risiken & Mitigation

| Risiko | Mitigation |
|---|---|
| **Virtual Grid 500k zu langsam** | Spike #2 vor Implementation, Fallback auf Custom-Panel oder Third-Party |
| **LibVLCSharp WPF-Interop instabil** | Spike #3 mit Cap auf 5 PT, Cutline auf "Extern öffnen" |
| **NAS bei Scan offline → False-Missing** | Offline-Guard mit 20%-Threshold (Pflicht-Architektur) |
| **EF Core Initial-Scan zu langsam** | Raw `Microsoft.Data.Sqlite` Prepared INSERT (60–80% schneller) |
| **Thumbnail-Queue Race-Conditions** | Versioning + Dedupe-Dictionary + Stale-Detection beim Dequeue |
| **Memory-Leaks bei langer Laufzeit** | Niemals `ImageSource` in Services, `BitmapCacheOption.OnLoad`, `Freeze()`, EF ChangeTracker.Clear() |
| **Concurrent SQLite-Writes** | WAL + Single-Writer-Pattern via Channel oder `SemaphoreSlim` |
| **FFmpeg hängt bei kaputten Videos** | `CancellationTokenSource.CancelAfter(3s/8s)` + Process.Kill |
| **Katalog-Umzug verliert Daten** | Kopie per Backup-API, `integrity_check` + Zeilenzahlen, Umschalten erst danach; Scans/Batches/Überwachung/Thumbnails ruhig, Factory gesperrt (auch für ältere Kontexte), Schreibsperre der alten DB gehalten; Neustart statt Laufzeit-Umschaltung (ADR-0026) |
| **Background-Worker blockiert UI** | WorkBudgetService mit StorageProfile-Limits + UI-Activity-Delay |

---

## 16. UI/UX

### Layout
```
┌─────────────────────────────────────────────────────────┐
│ Custom Title Bar (Mica)                       — □ ×    │
├──────────┬──────────────────────────────────────────────┤
│ NavView  │ CommandBar                                   │
│          ├──────────────────────────────────────────────┤
│ Logo     │ Breadcrumb / Page-Header                     │
│ Search   │ Filter-Pills (Alle | Bilder | Videos | ⭐)   │
│          │ InfoBar (optional)                           │
│ Library  ├──────────────────────────────────────────────┤
│ Folders  │                                              │
│          │   Paged virtualisiertes Thumbnail-Grid       │
│          │   (200–1000 Items pro Page)                  │
│          │                                              │
│ Settings │                                              │
├──────────┴──────────────────────────────────────────────┤
│ Statusbar: Scan / Thumbs / Background / Recovery        │
└─────────────────────────────────────────────────────────┘
        Floating Action Bar (bei Multi-Selection)
```

### Stil
- Fluent / WinUI 3 (Mica-Hintergrund, Akzent-Schimmer)
- Akzent: `#5B5FC7` hell / `#818CF8` dunkel
- Schrift: Segoe UI Variable
- Icons: Segoe Fluent Icons (kein Emoji-Mix)
- Light + Dark Theme

### Lightbox (eigenes Window)
- Rahmenlos / immersive
- Bild-Zoom (Mausrad, +/-, F=Vollbild, 1=100%, 0=Reset, Drag=Pan)
- Navigation: ← → (Pfeiltasten **und** Buttons), Bild↑↓ (±10), Pos1/Ende
- Filmstrip unten
- EXIF-Info-Panel rechts (toggle via I)
- LibVLCSharp für MP4 (V0.1 wenn Spike grün)
- Esc / Doppelklick = schließen
- Multi-Monitor unterstützt durch eigenes Window

### Mockups
- [mockups/04-fluent-light.html](mockups/04-fluent-light.html) — favorisiert hell
- [mockups/05-fluent-dark.html](mockups/05-fluent-dark.html) — favorisiert dunkel
- [mockups/03-lightbox.html](mockups/03-lightbox.html) — Lightbox-Layout

### Container-State (wichtig!)
**Klick auf Ordner ohne direkte Medien:** zeigt **Subfolder-Kacheln** statt leeres Grid (vermeidet "kaputt"-Eindruck).

### Plan-Ordner-UI (V0.3)
- Eigene NavView-Section "Sortierpläne"
- Two-Panel-Layout: Quelle (Galerie) links, Plan-Tree (Drop-Zonen) rechts
- Drag&Drop + Kontextmenü-Zuweisung
- Plan-Ordner-Badge: `[42 / 3 Konflikte]`
- Visuelle Unterscheidung physisch vs. virtuell (gestrichelte Kontur, "Virtuell"-Banner)
- Apply mit Preflight-Dialog

---

## Anhang: Review-Quellen

3 ChatGPT-Review-Runden mit GPT-5 (alle archiviert in `chatgpt-review/`):

- Runde 1: Architektur-Stack, DB-Schema, UI/UX, Migrations-Strategie, erweiterte Features
- Runde 2: WIC-Tiefe, Multi-Channel-Queue komplett, OperationBatches-Recovery, Animated-Hover-Lifecycle, MVP-Schnitt für 50k–500k, WPF-VirtualPanel-Limits, WorkBudgetService
- Runde 3: Aufwand-Reality-Check (8–10 Wochen), Storage-Profile-Detection, Migration-Disziplin, finale 15 ADRs, Tag-1–3-Plan, Offline-vs-Missing-Guard

---

**Plan-Status:** ✅ Final, bereit für Implementation
**Nächster Schritt:** Solution-Skeleton anlegen (Tag 1) oder erste blockierende Spike starten (Tag 2)

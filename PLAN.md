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
- Orte: Weltkarte mit Foto-Gruppen, die beim Zoomen feiner werden, Verorten per Ziehen (Welle 10, ADR-0051)

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
    Faces/                            # Gesichtserkennung (ADR-0035): FaceEmbeddingCodec, Detection (SCRFD), Alignment, Onnx (DirectML/CPU), Models (Download), Indexing (Hintergrund-Index); Recognition/ = Lern-/Gruppierlogik (ADR-0036); Assignment/ = Zuordnungsdienst, Crops/ = Gesichts-Ausschnitte (ADR-0037); Erase/ = „Gesichtsdaten löschen“ je Bibliothek (ADR-0038)
      Relocation/                     # DatabaseRelocationService, StorageProbe, ThumbnailInventory — Katalog + Thumbnails umziehen (ADR-0026)
    Infrastructure/                   # AppDataPaths, DatabaseLocationStore (database-location.json, ADR-0026)
    Library/                          # LibraryService, LibraryCatalogCleanup
    Media/                            # MediaFormatRegistry (ADR-0016)
    Messaging/                        # Nachrichten-Records für WeakReferenceMessenger (ADR-0017)
    Operations/                       # FileOperationService, WindowsRecycleBin (ADR-0012/0018)
    Places/Clustering/                # GeoClusterIndex (Morton-Raster, Gruppen je Zoomstufe), MercatorProjection (ADR-0051)
    Scanning/                         # ScanService, FolderEnumerator, IncrementalScanRunner, RootAvailabilityProbe, MassMissingGuard (ADR-0019)
    Sorter/                           # FileSorterService, FlattenService, SorterPatternEngine, SorterProfileStore
    Thumbnails/                       # ThumbnailService, FfmpegVideoThumbnailer, Renderer-Interfaces
  PhotoViewerV3.Wpf/                     # net10.0-windows → alle; App.xaml.cs = Host + DI, MainWindow = NavigationView
    Faces/                            # WicFaceImageLoader (IFaceImageLoader), Einstellungs-Abschnitt Gesichtserkennung
    Folders/                          # Ordnerbaum-Seite
    Gallery/                          # Galerie: Data (GalleryPageSource, .Persons = Personen-Filter/-Suche/-Spalte), ViewModels, Views, Controls, Converters, Services, People (Personen-Filter-Dropdown)
    Library/                          # Bibliotheks-Seite (Namespace PhotoViewerV3.Libraries)
    Lightbox/                         # LightboxWindow/-ViewModel/-Service, ZoomPanControl (+ Overlay), LibVlcHost, Imaging, Faces (Gesichtsrahmen, Taste F)
    Persons/                          # Seite „Personen“ (ADR-0037): PersonsViewModel, TileListBox, PersonPickerDialog, PersonDetailsDialog (ADR-0045), WicFaceCropRenderer
    Places/                           # Seite „Orte“ (ADR-0051): MapControl (eigene Kachelkarte), MapViewport, Tiles (OSM, Platten-Cache), „Ohne Ort“, MapPickerWindow
    Settings/                         # Einstellungen, ThemeService, FaceData („Gesichtsdaten löschen…“), PersonRelations („Personen & Beziehungen“, ADR-0045)
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
                   -- Action: move | copy | recycle | rename | mkdir | renamefolder (0.2.5) | adoptfolder | setcapturedate (0.2.48) | setgeolocation (0.2.53)
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
- Persons / Faces / FaceRejections / FaceScans — Teil 1 umgesetzt in 0.2.34 (Migration `AddFaces`, ADR-0035):
  `Persons (Id, Name COLLATE NOCASE, CreatedAtUtc, CoverFaceId NULL ohne Fremdschlüssel, IsHidden)` — global wie Tags;
  `Faces (Id, FileId → Files ON DELETE CASCADE, BoxX/BoxY/BoxW/BoxH relativ 0..1, Landmarks BLOB 10 float32, DetScore,
  Size, Sharpness, Quality, Embedding BLOB 512 float32 = 2048 Bytes L2-normiert, ModelVersion, PersonId / SuggestedPersonId
  → Persons ON DELETE SET NULL, State 0 unbestätigt | 1 Vorschlag | 2 bestätigt | 3 ignoriert, SuggestionScore, CreatedAtUtc;
  IX_Faces_File, IX_Faces_Person, IX_Faces_State, IX_Faces_SuggestedPerson; seit 0.2.38 Migration `AddFacePersonIndex`
  zusätzlich abdeckend IX_Faces_Person_State_File (PersonId, State, FileId), IX_Faces_SuggestedPerson_State_File
  (SuggestedPersonId, State, FileId), IX_Faces_File_State_Person (FileId, State, PersonId, SuggestedPersonId) für
  Personen-Filter/-Suche/-Spalte, ADR-0038)`,
  `FaceRejections (FaceId → Faces, PersonId → Persons, PK beide, CASCADE)`,
  `FaceScans (FileId PK → Files CASCADE, ModelVersion, ScannedAtUtc, FaceCount, ErrorCode)` — Merker des Hintergrund-Index.
  Seit 0.2.47 (Migration `AddPersonDetails`, ADR-0045): `Persons` + `FirstName`/`LastName` (NOT NULL DEFAULT '', NOCASE),
  `Nickname` (NULL, NOCASE), `RelationId` → `PersonRelations` ON DELETE SET NULL, `BirthYear`/`BirthMonth`/`BirthDay`
  (NULL, auch nur Jahr bzw. Jahr + Monat), `IsMe` (eindeutiger Teilindex `IX_Persons_IsMe WHERE IsMe = 1`),
  `IX_Persons_Relation`; `Name` bleibt der synchron gehaltene Anzeigename (Rufname, sonst „Vorname Nachname“), Bestand am
  ersten Leerzeichen geteilt. `PersonRelations (Id, Name NOCASE, IX_PersonRelations_Name eindeutig)` — selbst getippte
  Beziehungen, global wie Tags.
  „Neu aufbauen“ parkt Faces/FaceScans in seiner Transaktion und setzt sie über Ordnerpfad + Name auf die neuen FileIds
  (`UserDataCarryOver.Faces.cs`); Face-Ids bleiben.
- Aufnahmedatum — umgesetzt in 0.2.48 (Migration `AddCapturedAt`, ADR-0046): `Files.CapturedAt` (Unix-Sekunden UTC,
  NULL = unbekannt), `Files.CapturedAtSource` (NULL = noch nicht gelesen, 0 unbekannt, 1 EXIF, 2 Video, 3 Dateiname,
  4 manuell), `Files.CapturedAtPrecision` (0 genau, 1 Tag, 2 Monat, 3 Jahr — ungefähre Angaben in der Mitte des
  Zeitraums), `Files.CapturedAtOffset` (Minuten Ortszeit − UTC, Anzeige als Wanduhrzeit). Ausdrucks-Index
  `IX_Files_Library_Captured ON Files(LibraryId, COALESCE(CapturedAt, ModifiedAtUtc) DESC, Id DESC)` (Raw-SQL in der
  Migration, EF kennt ihn nicht — `DatabaseInitializer` legt ihn bei jedem Start idempotent an,
  `CaptureDateSortSql.CreateIndexSql`). `CaptureDateUndo (FileOperationId PK → FileOperations ON DELETE CASCADE,
  OldCapturedAt, OldSource, OldPrecision, OldOffset)` — alte Werte für „Rückgängig“ von „Datum setzen…“
  (`OperationBatches.OperationType = 7 SetCaptureDate`, je Datei eine FileOperation). Gefüllt vom Nachtragen im
  Hintergrund (nicht im Scan); manuelle Daten überleben „Neu aufbauen“ (`UserDataCarryOver`) und Inhaltsänderungen
  (Incremental-Scan setzt nur nicht-manuelle zurück).
- Orte — umgesetzt in 0.2.53 (Migration `AddGeoLocation`, ADR-0050, Vertrag mit der Karte): `Files.Latitude`/`Longitude`
  (REAL, WGS84-Dezimalgrad), `Files.GeoSource` (NULL = noch nicht gelesen, 0 keine Koordinaten, 1 EXIF/XMP, 2 Video,
  3 manuell gesetzt bzw. entfernt), `Files.PlaceId` (GeoNames-Id des nächsten Orts aus der eingebetteten Ressource, NULL
  ohne Ort im Umkreis von 100 km). Gefilterte Indizes `IX_Files_Library_Geo ON Files(LibraryId, Latitude, Longitude) WHERE
  Latitude IS NOT NULL` (Karte, Ausschnitt) und `IX_Files_Library_Place ON Files(LibraryId, PlaceId) WHERE PlaceId IS NOT
  NULL` (Ortsfilter, Ortszähler). `GeoTagUndo (FileOperationId PK → FileOperations ON DELETE CASCADE, OldLatitude,
  OldLongitude, OldGeoSource, OldPlaceId, NewLatitude, NewLongitude, WriteKind 0 Katalog | 1 JPEG-EXIF | 2 XMP-Begleitdatei,
  WrittenPath, OldBytes BLOB (altes APP1-Segment bzw. alte XMP-Datei), WrittenHash BLOB (SHA-256 des Geschriebenen),
  SegmentOffset, OldModifiedTicks (UTC-Ticks), OldSize)` — „Rückgängig“ von „Ort setzen/entfernen“ (`OperationBatches.OperationType = 8
  SetGeoLocation`, je Datei eine FileOperation). Gefüllt vom Nachtragen im Hintergrund; manuelle Orte überleben „Neu
  aufbauen“ und Inhaltsänderungen wie das Aufnahmedatum.
- Motive — umgesetzt in 0.2.55 (Migration `AddMotifs`, ADR-0052):
  `MotifTerms (Id, LibraryId → Libraries ON DELETE CASCADE, Name COLLATE NOCASE, Prompt NULL (Beschreibung fürs
  Modell), Threshold NULL (= globale Schwelle), SortOrder, TextEmbedding BLOB NULL (768 float32), TextModelVersion NULL,
  CreatedAtUtc; UX_MotifTerms_Library_Name (LibraryId, Name) eindeutig)` — Begriffe je Bibliotheks-Bereich (vorerst
  `LibraryId`, die Bibliotheks-Trennung stellt auf Gruppen um); Standard-Liste (82 Begriffe) einmal je Bibliothek
  (Merker `Settings.motifs.seeded.{id}`).
  `FileEmbeddings (FileId PK → Files ON DELETE CASCADE, ModelVersion, ErrorCode NULL, CreatedAtUtc, Embedding BLOB NULL
  = 768 float16 = 1 536 Bytes L2-normiert, als letzte Spalte; IX_FileEmbeddings_File_Model (FileId, ModelVersion)
  abdeckend fürs Zählen)` — Bild-Vektor je Foto bzw. Video-Vorschaubild, bleibt bei Begriffs-Änderungen.
  `FileMotifs (FileId → Files, TermId → MotifTerms — beide ON DELETE CASCADE, PK (FileId, TermId), Score REAL = SigLIP-
  Wahrscheinlichkeit; IX_FileMotifs_Term_File (TermId, FileId))` — nur Treffer über der Schwelle.
  Settings: `motifs.enabled` (Standard an), `motifs.useGpu`, `motifs.threshold` (Standard 0,01). „Neu aufbauen“ parkt
  FileEmbeddings/FileMotifs wie die Gesichter (`UserDataCarryOver.Motifs.cs`); der Incremental-Scan verwirft Vektor und
  Treffer an Ort und Stelle geänderter Dateien.

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

### Kataloge und Katalog-Sitzungen (seit 0.2.52, ADR-0049)
- **Normaler Katalog**: `library.db` + `thumbs\` im Datenordner (ADR-0026), Protokolle unter `%LocalAppData%\PhotoViewerV3\logs`.
- **Versteckte Bibliothek**: je Bibliothek ein eigener Katalog in `<Bibliotheksordner>\.lumora\` im verschlüsselten
  Container (`library.db`, `thumbs\`, `logs\`, `cache\`, `temp\`, `hidden-library.json` mit Name, Kennwort-Prüfwert,
  Zusperr-/Aushänge-Einstellung). Gleiches Schema, gleiche Migrationen; nur mit Startschalter `_hidden` sichtbar.
- **Katalog-Sitzung** = ein DI-Container mit `IAppDataPaths` + `ICatalogContext` (`IsHidden`, Pfade, `CacheRoot`,
  `TempRoot`) und eigenem Serilog-Logger. Wechsel normal ↔ versteckt zur Laufzeit: Fenster schließen, Host entsorgen
  (Dienste, Verbindungen, Caches), Messenger zurücksetzen, neuen Host starten (Migration), neues Hauptfenster. Dateien
  außerhalb der DB legen Module nur unter `ICatalogStoragePaths.CacheRoot/TempRoot` ab. `.lumora` wird nie gescannt.

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
| ✅ | Ordner in die Bibliothek übernehmen (Struktur erhalten, leere Zwischenordner zusammenziehen, ein `AdoptFolder`-Batch mit Rückgängig) + Ordner bewerten/kategorisieren (0.2.32, ADR-0034; keine Schema-Änderung, `OperationType.AdoptFolder = 6`). In der Oberfläche heißt die Archiv-Wurzel seitdem „Bibliotheksordner“ |
| ✅ | Archive: im Katalog, Inhaltsansicht, manuelles Entpacken ✅ (0.2.8, ADR-0022); Auto-Extract nach dem Scan für neue Archive in Eingängen ✅ (0.2.25, ADR-0032, Einstellung, Standard aus; je Bibliothek + vorhandene Archive nachholen ✅ 0.2.31, ADR-0033) |
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
| 🟡 | **V0.5 / Phase 9** | Face Recognition: Teil 1 ✅ (0.2.34, ADR-0035: InsightFace buffalo_l = SCRFD + ArcFace via ONNX Runtime/DirectML, Modell per Benutzer-Download, Schema `AddFaces`, Hintergrund-Index mit Statusleiste, Einstellungen), Lern-/Gruppierlogik ✅ (0.2.35, ADR-0036), Zuordnungsdienst + Personen-Seite ✅ (0.2.37, ADR-0037: Benennen, Vorschläge, Gruppen mit abgewählten Ausreißern), Filter/Suche/Liste/Kachel/Lightbox-Rahmen nach Personen + „Gesichtsdaten löschen“ ✅ (0.2.38, ADR-0038) | 30–50 PT |

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

- **Naming**: `42_400_still.jpg` für File-ID 42, statisches Thumb mit kurzer Kante 400 px (`Thumbnails.Size` = 400)
- **Animierte (Phase 8)**: `42_400_hover.gif`
- **Größe (ADR-0043, ersetzt „160 / 320 / 640“)**: eine Größe — kurze Kante 400 px, lange höchstens 1200 px, nie
  hochskalieren, EXIF-Drehung, JPEG-Qualität 82 (`ThumbnailSizing`; WIC und FFmpeg gleich). Spike #11: ≈ 5,5 GB je
  100 000 Fotos (≈ 27 GB bei 500 000), Erzeugung ≈ 28–34 min je 100 000 mit 4 Workern.
- **Laden in der Galerie**: Dekodierfläche = Bildbereich der Kachel × Bildschirm-Skalierung; bei deutlich größerer Fläche
  (> 15 %) werden nur die zu kleinen Kacheln im Hintergrund neu dekodiert.
- **Übergang alter Größen** (320 px): zeigen, bis das neue fertig ist, dann Zeile (gleiche Transaktion) und Datei löschen;
  Pflege im Hintergrund ersetzt den Rest in 32er-Stapeln bei leerer Warteschlange (nicht bei Pause, Scan-Sperre,
  fehlender Quelle, Cloud-Platzhalter), Fortschritt in `PendingCount`.
- **Bildmaße**: beim Erzeugen `Files.Width/Height` = Original nach EXIF-Drehung (gleiche Transaktion wie „Generated“;
  Videos aus der FFmpeg-Eingabe); fehlende trägt die Pflege aus dem Dateikopf nach (0 × 0 = nicht lesbar).
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
| **Versteckte Bibliothek hinterlässt Spuren** | Eigener Katalog + Logger je Sitzung im Container, Container-Wechsel statt Umschalten einzelner Dienste, Shell-Aufrufe (Explorer, Standard-App) gesperrt, `AddToRecent = false`, Spurenprüfung in Unit- und Rauchtest (ADR-0049); Grenzen: Dateidialog-MRU, VeraCrypt-Verlauf, Auslagerungsdatei |
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

# Runde 3 — ChatGPT (GPT-5) Antwort

> Datum: 2026-05-10
> Canvas: Review Runde 3

## Kernposition

- **4 Wochen:** unrealistisch für aktuellen V0.1-Scope in produktiver Qualität
- **6 Wochen:** ambitioniert, nur mit Fokus + Cutlines
- **8–10 Wochen:** realistisch für robustes V0.1 (50k–500k produktiv)
- **Größtes Risiko:** WPF Grid/Virtualisierung + LibVLC-WPF-Interop + Scanner/Thumbnailer-Concurrency

## 1. Aufwand-Reality-Check

### Personentage-Schätzung

| Block | PT | Bemerkung |
|---|---:|---|
| **Spikes** (6 Stück) | 10–18 | Storage-Spike als 6. ergänzt |
| **Backend** (Schema+Scan+FTS+Thumbs+Queue+WorkBudget+Storage+OpBatches+Settings) | 26–46 | |
| **Frontend** (Shell+Grid+Tree+Filter+Lightbox+LibVLC+Status+Theme) | 25–46 | Grid + VLC Risiko-Treiber |
| **Infrastructure** (DI+Logging+ErrorHandling+Tests+Sample-Data) | 7–14 | |
| **Gesamt** | **68–124 PT** | |

5 produktive PT/Woche realistisch → **68 PT = 13–14 Wochen brutto**, oder **40–55 PT = 8–11 Wochen mit Cuts**.

### Cut-Empfehlungen wenn 4–6 Wochen fix

**Streichen #1: LibVLCSharp aus V0.1 produktiv** → spart 5–10 PT
- MP4 nur statisch + "Extern öffnen"
- LibVLC-Spike bleibt vorgezogen
- Produktive Lightbox in V0.2

**Vereinfachen: Adaptive FPS-Throttling** → spart 2–4 PT
- StorageProfile-basierte feste Budgets
- User-Activity-Delay, VideoPlayback-Pause
- FPS nur als Logging, nicht Steuerung

**Nicht streichen:**
- Paged virtualisiertes Grid (nicht optional bei 500k)
- Raw SQLite Bulk + FTS minimal (Kern, nicht Komfort)
- Offline-vs-Missing-Guard (Vertrauensbruch sonst)

### Spike-Reihenfolge

**Blockierend zuerst (Architektur-Entscheidungen):**
1. SQLite Bulk + FTS5 500k
2. Virtual Grid 500k
3. LibVLCSharp WPF Lightbox

**Danach:**
4. WIC Benchmark + File-Lock
5. FFmpeg Cancel/Timeout
6. Storage Detection

## 2. Storage-Profile-Detection

### Zwei Konzepte trennen

```
StorageAvailability = Library-Root erreichbar?
StorageProfile      = Wie aggressiv arbeiten?
```

### Detection-Strategie (konservativ)

- `GetDriveType` für Remote/Removable/Fixed
- UNC-Erkennung: `path.StartsWith("\\\\")`
- SSD/HDD: WMI `MSFT_PhysicalDisk.MediaType` ODER später `DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY)`
- **Bei Unsicherheit konservativ als HDD/USB behandeln** (Performance verlieren statt Vertrauen)

### Drei Profile

**SSD-Profil:**
- ScanBatchSize 5000, MaxImageWorkers max(2, CPU-2), MaxVideoWorkers 1–2
- Background BatchDelay 0–10ms

**HDD/USB-Profil:**
- ScanBatchSize 1000–2000, MaxImageWorkers 2, MaxVideoWorkers 1
- Background BatchDelay 50–150ms, MaxConcurrentDiskReads 2

**NAS-Profil:**
- ScanBatchSize 500–1000, MaxImageWorkers 1–2, MaxVideoWorkers 0–1
- Background BatchDelay 150–500ms, RootCheckTimeout 1500–3000ms
- AggressiveBackgroundThumbs = false

### Live-Switching

Profile-Switching live für Budgets/Status, **nicht** für Architektur. Library-offline = eigener State, kein Auto-Rescan.

### Cache bei NAS

**Lokal in `%LocalAppData%`** auch bei NAS — kein NAS-Polluting, keine Schreibrechte-Probleme, kein Sync-Churn.

```
%LocalAppData%\FotoViewerV2\thumbs\{libraryId}\{fileId}_{size}_{kind}.jpg
```

## 3. Migration-Disziplin

**Grenze: erste produktive Nutzung** (nicht "letzter Tag der Entwicklung")

- Bis dahin: DB-Reset OK
- Ab dann: EF Core Migrations strict
- Spätestens ab V0.2

**Auto beim App-Start** mit Backup + UI-Gate, nicht CLI-only.

**Backup versioniert:** `library.db.backup-20260510-143255-v0.1.0-to-v0.2.0`

**WAL-Sidecar:**
- `PRAGMA wal_checkpoint(TRUNCATE);` vor Backup
- DB schließen → kopieren

**Bei Migrationsfehler:** Dialog mit Restore/Log, nie mit halb migrierter DB starten.

## 4. Finale 15 ADRs

```
ADR-001: .NET 10 LTS, WPF, WPF-UI als isolierter UI-Layer, CommunityToolkit.Mvvm
ADR-002: Multi-Project Solution ab Tag 1 (Domain/Data/Services/Wpf)
ADR-003: Eine aktive Library, zentrale DB %LocalAppData%, LibraryId-FKs
ADR-004: EF Core für CRUD, Raw SQLite für Scan/Bulk/FTS5
ADR-005: DB-Reset bis erste produktive Nutzung; ab V0.2 strict Migrations + WAL Checkpoint Backup
ADR-006: WIC für JPG/PNG, FFmpeg für MP4-Standbilder, LibVLCSharp für MP4-Lightbox (V0.1 Cutline)
ADR-007: Multi-Channel Queue + Dedupe + Versioning + Status + .tmp Writes
ADR-008: StorageProfileService (LocalSsd/LocalHdd/NetworkShare/Unknown), Offline-State separat von Profil
ADR-009: Cache lokal %LocalAppData% auch bei NAS
ADR-010: Paged/virtualisiertes Grid via Query-DTOs, keine 500k ObservableCollection
ADR-011: FTS5 minimal V0.1-Pflicht (Files.Name+Extension), Filter Alle/Bilder/Videos/Favoriten Pflicht
ADR-012: OperationBatches/FileOperations ab V0.1 für Delete-to-Recycle
ADR-013: Status-UI mit 4 Indikatoren ist Produktanforderung, nicht Polish
ADR-014: Animated Hover NICHT V0.1
ADR-015: Face Recognition NICHT im V0.1-Schema
```

## 5. Tag 1–3 Implementation

### Solution-Layout (Multi-Project ab Tag 1)

```
FotoViewer.sln
src/
  FotoViewer.Domain/        net10.0, keine externen Deps
  FotoViewer.Data/          → Domain
  FotoViewer.Services/      → Domain + Data
  FotoViewer.Wpf/           net10.0-windows → alle
  FotoViewer.Tools/         optional später
tests/
  FotoViewer.Tests/
```

### Tag 1 — Fundament

Solution + DI + HostBuilder + Logging + leeres MainWindow + AppDataPaths-Service. **Definition-of-Done:** App startet, MainWindow sichtbar, Logfile geschrieben.

### Tag 2 — SQLite/FTS/Bulk Spike

500k synthetische Files einfügen, Indizes anlegen, FTS5 befüllen, Queries messen. **Messziele:**
- Bulk insert 500k: <5 min SSD, <15 min HDD
- Folder query 500 items: <100ms
- FTS name query: <200ms

### Tag 3 — Virtual Grid Spike

WPF Shell + GalleryView + QueryService DTO-Pages + Grid 500–1000 Items/Page + SelectionStore (FileId). **Messziele:** Start <1s, Page-Switch <150ms, Memory stabil.

### NuGet-Pakete (konkrete Versionen)

```xml
<!-- Data -->
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.7" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.7" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.7" PrivateAssets="all" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.7" />

<!-- Services -->
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
<!-- Statt FFMpegCore: eigener ProcessWrapper für CancelAfter/Kill -->

<!-- WPF -->
<PackageReference Include="WPF-UI" Version="4.*" />
<PackageReference Include="LibVLCSharp.WPF" Version="3.9.7.1" />
<PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.*" />
<PackageReference Include="Serilog.Extensions.Hosting" Version="9.*" />
<PackageReference Include="Serilog.Sinks.File" Version="6.*" />

<!-- Tests -->
<PackageReference Include="xunit" Version="2.*" />
<PackageReference Include="FluentAssertions" Version="8.*" />
```

## 6. Offline-vs-Missing-Guard

### Grundregel
> Nur wenn LibraryRoot erreichbar ist, dürfen einzelne Files als Missing markiert werden.
> Wenn LibraryRoot nicht erreichbar: Library offline, KEIN File-Missing ändern.

### Algorithmus
- `CheckLibraryAvailabilityAsync` mit Timeout (UNC 3s, lokal 1s)
- Vor jedem Scan-Batch: Root-Recheck
- Bei IOException während Scan: Root-Recheck → wenn offline, Mark Library Offline, return
- Mass-Missing-Threshold: bei `>10.000` ODER `>20%` der Library nicht auto-committen → UI-Dialog

### Dialog
```
Ungewöhnlich viele Dateien fehlen
Beim Scan wurden 142.381 fehlende Dateien erkannt.
Das kann passieren, wenn ein Laufwerk nicht vollständig verfügbar ist.
[Erneut prüfen] [Als fehlend markieren] [Scan abbrechen]
```

## Finale V0.1-Empfehlung

### V0.1 Pflicht
Multi-project, SQLite Bulk, FTS5 minimal, Paged Grid, WIC Thumbs, MP4 statisch, Search/Filter/Favorit, Offline-Guard, Statusbar minimal, Image-Lightbox, Delete-to-Recycle

### V0.1 nur wenn Spike grün
LibVLC Video-Lightbox, FPS-Adaptive Throttling

### V0.1 raus
Animated Hover, Move/Rename, Plan-Folders, Tags/Albums, Rating, Faces, Full-Recovery-Repair-UI

## ✅ Einigkeit
- Favorit V0.1, Rating später
- DB-Reset bis erste produktive Nutzung; ab V0.2 strict
- FTS5 minimal V0.1-Pflicht
- StorageProfileService Pflicht
- Cache lokal bei NAS
- Offline-vs-Missing kritisch
- Paged Grid + Raw SQLite Bulk nicht verhandelbar
- Status-UI ist Vertrauen, nicht Polish

## ⚠️ Widerspruch
- 4 Wochen für V0.1 unrealistisch
- 6 Wochen nur mit Cuts; ohne Cuts 8–10 Wochen
- LibVLC V0.1 muss Spike-abgesichert sein, sonst Cut auf "extern öffnen"
- FPS-adaptives Throttling in V0.1 vereinfachen bei Zeitdruck
- Single-Project nicht mehr sinnvoll → Multi-Project ab Tag 1
- `GetDriveType` allein nicht für SSD/HDD; konservativer Fallback OK

## ❓ Rückfragen
1. LibVLC-Cut akzeptabel bei rotem Spike? Nur "extern öffnen"?
2. V0.1 = "produktiv mit bekannten Risiken" oder "Release-fähig robust"?
3. Installer ab V0.1, oder portable `dotnet publish`-Ordner?
4. Reale Testdaten 100k+ auf SSD/HDD/NAS, oder synthetische?
5. NAS: Background-Thumbnails deaktivieren oder nur drosseln?

# Review Runde 1 — Foto-Viewer V2: PowerShell → C# Portierung

## Rolle

Du bist ein erfahrener **.NET-Architekt mit WPF-/Desktop-App-Schwerpunkt** und führst ein technisches Review-Gespräch mit einem Kollegen (Claude/Anthropic), der einen Portierungs-Plan erstellt hat.

## Gesprächsformat

Dieses Gespräch läuft über einen Vermittler (den User).

- Sprich direkt zu deinem Kollegen Claude, **NICHT zum User**
- Kein Meta-Kommentar über das Format
- Schreibe deine **GESAMTE Antwort in Canvas**
- **CANVAS-TITEL: "Review Runde 1"**
- Fasse am Ende zusammen:
  ✅ Einigkeit | ⚠️ Widerspruch | ❓ Rückfragen

## Repo-Zugriff

Du hast Zugriff auf das GitHub-Repo und kannst Dateien lesen:

- **Repo:** `herbertschrotter-blip/03_Foto-Viewer-V2`
- **Branch: `main`** — IMMER diesen Branch verwenden
- **Wichtig:** Auf `main` liegt der **bestehende PowerShell-Stand (v1.0.0)**. Den C#-Port gibt es noch nicht — das ist der Plan, den wir hier reviewen.
- Nutze den Repo-Zugriff aktiv um die bestehende PowerShell-App zu verstehen (Architektur, Features, Routes, Lib-Module). Lies konkrete Dateien wenn der Kontext im Prompt nicht reicht — z. B. `start.ps1`, `Lib/Routes/*.ps1`, `Lib/Utils/Lib_FileSorter.ps1`, `Templates/index.html`, `config.json`, `README.md`.

## Gesprächsregeln

- Ehrlich und kritisch
- Probleme konkret benennen (mit Begründung)
- Verbesserungen mit Code/Pseudocode zeigen
- Rückfragen bei fehlendem Kontext
- Fokus halten, keine allgemeinen Exkurse
- Kompakt, Code nur wenn nötig
- **Fokus: Architektur-Stack, DB-Schema, UI/UX, Migrations-Strategie**

## Projektphase

Greenfield-C#-Port in MVP-Phase, **keine Produktivdaten zu migrieren**.

Konsequenzen für deine Vorschläge:
- KEINE Migrations-Logik vom PowerShell-Datenstand nötig (es gibt nur eine `state.json` mit Root-Pfad — der User wählt im neuen Tool einfach neu)
- KEINE Backward-Compatibility-Patterns
- Bei Schema-Änderungen während der Entwicklung: "DB löschen, neu anlegen lassen" ist OK
- Der **Single-User**-Charakter (lokal, eine Person, keine Auth) bleibt erhalten

---

## Was es ist (heute, PowerShell v1.0.0)

Lokale Web-Galerie für Fotos und Videos. PowerShell 7+ Backend mit `System.Net.HttpListener`, Frontend HTML/JS im Browser auf `localhost:8888`.

**Features:**
- Thumbnail-Grid mit Lazy Loading, Akkordeon-Ordner, Lightbox-Viewer
- FFmpeg-Thumbnails für Videos, HLS-Streaming, Codec-Erkennung (H.264, HEVC, WMV3, MPEG-2, …)
- File-Sorter (Pattern-Engine, ~1900 Zeilen) — der User sagt: "hat noch nicht so gut funktioniert, machen wir neu"
- Flatten & Move (Dateien aus verschachtelten Ordnern hochziehen)
- Datei-Löschen in Windows-Papierkorb, Archiv-Extraktion (ZIP/RAR/7z)
- Hash-basierter Thumbnail-Cache (`MD5(path+size+mtime).jpg`)
- Runspace Pool für parallele Request-Verarbeitung
- 9-teilige Konfiguration in `config.json`

**Architektur:** modular per dot-sourcing in `Lib/Core`, `Lib/Media`, `Lib/Routes`, `Lib/UI`, `Lib/Utils`, `Lib/System`. Frontend in `Templates/*.html` + `app.js`.

**User-Aussage:** "das mit dem Server war nur eine PowerShell-Notlösung. Ich will nur Fotos ansehen können, sortieren, verschieben, auch Videos, und das in einer App die selbstständig läuft ohne Web oder so."

---

## Was es werden soll (Plan)

Native Desktop-Anwendung. Kein HTTP-Server, kein Frontend-Backend-Split. Single-User, Windows-only akzeptiert.

### Stack-Entscheidungen

| Komponente | Wahl | Begründung |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | Aktuelle LTS, C# 14, Performance |
| UI-Framework | **WPF** | Mature, Windows-nativ, ideal für Media-Apps |
| Design-System | **WPF-UI (lepoco)** | Fluent / WinUI 3 Look, Mica-Effekt, Segoe Fluent Icons |
| MVVM | **CommunityToolkit.Mvvm** | Source-Generators, etabliert |
| Video-Player | **LibVLCSharp** | Spielt alles, eigene Codecs (40 MB Deployment) — alternativ MediaElement, aber das hat User-Material mit WMV3/MPEG-2 das oft nicht läuft |
| Bildverarbeitung | **SixLabors.ImageSharp** ODER WPF nativ (`BitmapImage.DecodePixelWidth`) | Offen — siehe Frage |
| FFmpeg-Wrapper | **FFMpegCore** | Async, modern, aktiv gepflegt |
| Datenbank | **SQLite + EF Core 10** | Eingebettet, ein File, Migrations, LINQ |
| Papierkorb | **Microsoft.VisualBasic.FileIO** (`FileSystem.DeleteFile` mit `RecycleOption`) | Native Windows-Papierkorb-API |
| EXIF (optional) | **MetadataExtractor** | Gut etabliert |

### Solution-Struktur

```
FotoViewer.sln
└── FotoViewer/                    # Single-Project, WPF App
    ├── App.xaml / App.xaml.cs     # DI-Container Setup (Microsoft.Extensions.DependencyInjection)
    ├── MainWindow.xaml            # NavigationView + Content
    ├── Views/
    │   ├── GalleryView.xaml       # Thumbnail-Grid
    │   ├── LightboxWindow.xaml    # Vollbild Foto/Video
    │   ├── FileSorterView.xaml    # neu, einfacher als alt
    │   ├── FlattenMoveView.xaml
    │   └── SettingsView.xaml
    ├── ViewModels/                # CommunityToolkit.Mvvm
    ├── Services/
    │   ├── ScanService.cs         # Indexer (Background, IHostedService)
    │   ├── ThumbnailService.cs    # Generation + Cache-Lookup
    │   ├── VideoThumbnailService.cs # FFMpegCore
    │   ├── ExifService.cs
    │   ├── SearchService.cs
    │   ├── TagService.cs
    │   ├── AlbumService.cs
    │   ├── RecycleBinService.cs
    │   ├── ConfigService.cs
    │   └── FileSorterService.cs   # neu, kein Port der alten Engine
    ├── Data/
    │   ├── LibraryContext.cs      # DbContext
    │   ├── Migrations/            # EF Core
    │   └── Entities/              # Files, Folders, Tags, Albums, Exif, History, Thumbnails, …
    └── Models/
```

### Datenbank-Schema (SQLite)

DB-Speicherort: `%LocalAppData%\FotoViewerV2\library.db` (offen: alternativ "Catalog neben Root-Ordner" wie Lightroom — siehe Frage).

```
-- Physische Welt (gescannt aus Dateisystem)
Folders          (Id, Path, ParentId, ScannedAt)
Files            (Id, FolderId, Name, Ext, Size, Created, Modified,
                  MediaType, Width, Height, Duration, Codec, PHash,
                  IsFavorite, Rating)
Exif             (FileId, CameraMake, CameraModel, Lens, Aperture, Iso,
                  ExposureTime, FocalLength, TakenAt, GpsLat, GpsLon)

-- Tags & Albums (klassische Metadaten)
Tags             (Id, Name, Color)
FileTags         (FileId, TagId)
Albums           (Id, Name, Description, CreatedAt, CoverFileId)
AlbumFiles       (AlbumId, FileId, SortOrder)

-- Plan-Ordner: virtuelle Sortier-Schablone, später auf Dateisystem anwendbar
Plans            (Id, Name, TargetRootPath, CreatedAt, AppliedAt)
PlanFolders      (Id, PlanId, ParentId, Name, SortOrder)
PlanFolderFiles  (PlanFolderId, FileId, AddedAt)

-- Token-basierter File-Sorter (Generator für Plan-Ordner) — Phase 8
SorterProfiles   (Id, Name, Pattern, Description, CreatedAt)
SorterTokens     (Id, ProfileId, TokenName, TokenType, Position, Separator)
                 -- TokenType: literal | alphanumeric | number | date | regex

-- Gesichtserkennung — Phase 9
Persons          (Id, Name, ThumbnailFaceId, CreatedAt)
Faces            (Id, FileId, BoundingBoxX, BoundingBoxY, BoundingBoxW, BoundingBoxH,
                  Embedding BLOB,        -- 512 floats ArcFace = 2 KB
                  PersonId nullable, Confidence, DetectedAt)

-- Archiv-Auto-Extract Tracking
ExtractedArchives (Id, OriginalPath, ExtractedToPath, ExtractedAt, OriginalRecycled)

-- Persistierte App-Daten
History          (Id, FileId, Action, SourcePath, DestPath, Timestamp,
                  PlanId)   -- Undo-Journal, persistiert über App-Restart
Thumbnails       (FileId, Size, Type, CachePath, GeneratedAt)
                 -- Type: static (jpg) | animated (gif) — animiert für Video-Hover
ScanLog          (FolderId, StartedAt, FinishedAt, FilesAdded, FilesRemoved)
Settings         (Key, Value)
SearchHistory    (Id, Query, ExecutedAt)
```

**Source-of-Truth-Aufteilung:**
- **Dateisystem** = SoT für Foto-/Video-Dateien selbst
- **DB** = SoT für Metadaten (Tags, Ratings, Albums, Plan-Ordner, History, EXIF-Cache, Thumbnail-Index)
- **FileSystemWatcher** synchronisiert: Datei verschoben extern → Pfad in DB updaten

### Plan-Ordner (zentrales neues Konzept — ersetzt den alten File-Sorter)

**Workflow:**
1. User scannt großen unsortierten Foto-Ordner → `Files` + `Folders` werden indiziert
2. User legt im UI eine **virtuelle Ordnerstruktur** an, beliebig tief verschachtelt — z. B.
   ```
   Urlaub Italien/
     Strand/
     Sehenswürdigkeiten/
       Rom/
       Florenz/
   Familie 2026/
   ```
   → wird in `Plans` + `PlanFolders` (parent-child) abgelegt, **nichts auf Disk**
3. User weist Fotos den Plan-Ordnern zu (Drag-and-Drop aus Galerie, Multi-Select → Kontextmenü)
   → Einträge in `PlanFolderFiles`
4. User klickt **"Plan auf Dateisystem anwenden"**:
   - Für jeden `PlanFolder` → echtes Verzeichnis unter `Plan.TargetRootPath` anlegen
   - Alle zugewiesenen `Files` per Move-Operation dorthin verschieben (mit Konflikt-Behandlung bei Namensduplikaten)
   - Jede Aktion einen `History`-Eintrag mit `PlanId` schreiben → Undo möglich
   - `Files.FolderId` updaten, `Plans.AppliedAt` setzen

**Offene Designfragen (Claude weiß es noch nicht):**
- **Kardinalität `PlanFolderFiles`**: 1:1 (Foto in genau einem Plan-Ordner) oder M:N (in mehreren Plan-Ordnern gleichzeitig)?
  - 1:1 = sauber für Apply — Foto wird an genau einen Ort verschoben
  - M:N = flexibler beim Planen, aber beim Apply muss Konflikt-UI entscheiden ("Welcher gewinnt?" oder "Kopieren statt Verschieben")
- **Mehrere parallele Pläne** möglich (eine Datei in mehreren `Plans`)? Oder pro Datei nur ein aktiver Plan?
- **Apply-Modus**: Verschieben (Default) vs. Kopieren (Original behalten) als Option?

### Anzeige-Verhalten der Galerie

Aus dem PowerShell-Vorgänger übernommen, soll **bleiben**: immer der **tiefste Ordner mit Medien** wird im Grid gezeigt, höhere Ordner sind reine Container im NavView.

Beispiel — Verzeichnis:
```
D:\Fotos\
  2026\
    Urlaub\          ← keine Medien direkt drin
      Italien\       ← Medien hier
      Spanien\       ← Medien hier
  2025\
    ...
```
- NavView zeigt: 2026 → Urlaub → (Italien, Spanien)
- Klick auf "Urlaub" → Grid leer (nur Container)
- Klick auf "Italien" → Grid zeigt alle Medien aus `D:\Fotos\2026\Urlaub\Italien`
- **Nicht** rekursiv aus Sub-Ordnern (also: wenn Italien nochmal `Strand/` und `Stadt/` enthält, sind die *eigene* Akkordeon-Items)

### Thumbnail-Strategie

**Speicherort:** `%LocalAppData%\FotoViewerV2\thumbs\` (z. B. `C:\Users\<User>\AppData\Local\FotoViewerV2\thumbs\`)
- Saubere Trennung von Foto-Ordnern (kein `.thumbs\`-Polluting wie früher)
- Kein OneDrive-Konflikt (war im PowerShell-Vorgänger ein Schmerzpunkt)
- Pro Windows-User isoliert
- Cache regenerieren = Ordner leeren

**Format & Naming:**
- Pfad: `%LocalAppData%\FotoViewerV2\thumbs\{file_id}_{size}.jpg`
- Animierte Video-Thumbs: `{file_id}_{size}_anim.gif`
- **Mehrere Größen**: 160 / 320 / 640 (passend zum Slider im Grid + Lightbox-Preview)
- Validierung über DB: `Files.Modified > Thumbnails.GeneratedAt` → neu generieren
- Orphan-Cleanup: regelmäßig DB-IDs mit Disk-Files abgleichen
- **Kein** MD5-Filename mehr (im PowerShell so gemacht), stattdessen File-ID — robuster und einfacher

**Bilder:** ImageSharp oder WPF native (`BitmapImage.DecodePixelWidth`) → Resize → JPEG.

**Videos — statisch:**
- Ein Frame extrahieren via FFMpegCore (z. B. bei 10% der Dauer, konfigurierbar)
- Codec via FFprobe → Codec-Badge (H.264, HEVC, WMV3, MPEG-2, …) — wie im PowerShell
- Dauer als Badge unten rechts

**Videos — animierte Hover-Thumbnails (User-Wunsch):**
- N Frames extrahieren (default 5, konfigurierbar 3–10) bei z. B. 10/30/50/70/90% der Dauer
- Als animiertes GIF zusammenbauen via FFMpeg (`palettegen` + `paletteuse`)
- Speicherort `{file_id}_{size}_anim.gif`, ~100–300 KB pro Video
- WPF-Rendering via NuGet **WpfAnimatedGif**
- **Hover-only**: standardmäßig statisches Frame, beim Maus-Hover über Thumbnail startet GIF (sonst flimmert das ganze Grid)
- Settings: AnimatedHover an/aus, Frame-Count, Frame-Positionen (%), AnimationFps

### Thumbnail-Generierung — Priority-Queue mit Preemption (User-Wunsch)

User-Anforderung: Generierung soll dynamisch priorisiert werden. Beispiel: Wenn der User in einen anderen Ordner springt, soll die laufende Generierung pausieren, der neue Ordner zuerst dran sein, dann Resume des alten Jobs.

**Konzept:** Background-Worker mit 4 Prioritäts-Stufen, basierend auf `IHostedService` + `PriorityQueue<ThumbJob, int>` aus .NET 6+:

| Prio | Trigger | Auflösung |
|---|---|---|
| 🔴 Critical | Lightbox-Bild offen | aktuelles Bild + Prefetch Vorgänger/Nachfolger |
| 🟠 Viewport | Sichtbare Items im Grid | nach Scroll-Debounce 100ms |
| 🟡 Current Folder | Geöffneter Ordner, außerhalb Viewport | beim Ordner-Wechsel |
| 🟢 Background | Erst-Indexierung, andere Ordner | Idle |

**Pause/Resume-Mechanik:**
- 1 Job = 1 Datei → Pause-Punkt nach jedem Job (kein Cancel mitten in FFmpeg-Aufruf)
- Aktiver Job wird zu Ende geführt (~50–300 ms), dann switcht Worker zur höheren Priorität
- Bei Wechsel zu anderem Ordner: höherer-Prio Queue füllt sich → Worker arbeitet die ab → springt automatisch zum vorigen 🟡/🟢-Job zurück wenn höhere Queue leer

**Parallelität:**
- `Parallel.ForEachAsync` mit `MaxDegreeOfParallelism = Environment.ProcessorCount` (oder `-2` damit UI flüssig)
- `SemaphoreSlim` limitiert Concurrent-Disk-Reads (gegen SSD-Saturation)

**Dedupe:**
- Wenn FileId schon in höherer Queue → nicht nochmal in niedrigerer einreihen
- Wenn FileId schon fertig (DB-Check `Thumbnails.GeneratedAt > Files.Modified`) → skip

**UI-Feedback:**
- Skelett-Platzhalter mit Shimmer-Animation während Generierung
- Crossfade (200ms) zum fertigen Thumbnail
- Statusbar: `Thumbnails: 87 / 234 (🟡 Aktueller Ordner)` mit Progress-Bar

**Trigger-Events:**
| Event | Queue-Aktion |
|---|---|
| Erst-Scan fertig | alle neuen FileIds → 🟢 |
| Ordner geöffnet | Files des Ordners → 🟡 |
| Scroll/Resize (debounced) | Visible-FileIds → 🟠 |
| Lightbox geöffnet | aktuelles Bild → 🔴 (640px) + ±2 Nachbar in 🟠 |
| FileSystemWatcher invalidiert | invalidierter FileId → 🟡 |
| App-Restart | DB-Check, fehlende Thumbs → 🟢 |

### UI/UX-Konzept

Mockups wurden als HTML-Skizzen erstellt (existieren lokal, nicht im Repo). Zwei Stile:

1. **Klassisch** — Custom Title-Bar, Toolbar mit Buttons, klassischer Folder-Tree links, Thumbnail-Grid mit Hover-Lift, Lightbox mit Filmstrip + EXIF-Panel
2. **Fluent / WPF-UI-Stil (favorisiert)** — Mica-Hintergrund mit subtilem Akzent-Schimmer, NavigationView (Bibliothek / Ordner / Tools), CommandBar, Filter-Pills, InfoBar, Acrylic-Flyout für Selektions-Aktionen

Light + Dark Theme. Akzentfarbe Fluent-Indigo (`#5B5FC7` hell / `#818CF8` dunkel).

**Layout-Konzept:**

```
┌─────────────────────────────────────────────────────────┐
│ TitleBar (Mica)                       — □ ×            │
├──────────┬──────────────────────────────────────────────┤
│ NavView  │ CommandBar (Ordner öffnen | Sortieren | …)  │
│          ├──────────────────────────────────────────────┤
│ Logo     │ Breadcrumb                                   │
│ Search   │ "Urlaub Italien" 234 Elemente · 1,8 GB      │
│          ├──────────────────────────────────────────────┤
│ Library  │ Filter-Pills: Alle (234) | Bilder | Videos  │
│ Folders  │ InfoBar (optional, z.B. "Indizierung läuft")│
│ Tools    │                                              │
│          │ ┌────┬────┬────┬────┐                       │
│          │ │    │    │ ▶  │    │  Thumbnail-Grid       │
│          │ └────┴────┴────┴────┘                       │
│          │                                              │
│ Settings │                                              │
└──────────┴──────────────────────────────────────────────┘
        Floating Action Bar (bei Selektion)
```

**Interaktion:**
- Multi-Select via Klick auf Thumbnail-Checkbox
- Selektions-Flyout zeigt: Kopieren / Verschieben / Umbenennen / Löschen / Aufheben
- Doppelklick = Lightbox
- Tastatur in Lightbox: ← → (Navigation), Space (Auswählen), F (Vollbild), Entf (Löschen → Papierkorb), Esc (Schließen)
- File-Sorter und Flatten & Move sind eigene Ansichten (eigene NavView-Items)

### File-Sorter — Token-basiertes Profil (Phase 8)

Die alte Regex-Pattern-Engine (~1900 Zeilen) **wird nicht portiert**. Stattdessen ein **token-basierter Sorter als Generator für Plan-Ordner**:

**Use-Case:** Es gibt Ordner mit Misch-Inhalt (mehrere "Alben" als Dateien gemischt), bei denen sich an den Dateinamen Pattern erkennen lassen.

**Beispiel:**
```
Profil:    {album}_{year}_{seq}.{ext}

Eingabe-Ordner enthält:
  Urlaub_Italien_2026_001.jpg
  Urlaub_Italien_2026_002.jpg
  Familie_Geburtstag_2026_001.jpg
  Familie_Geburtstag_2026_002.jpg
  IMG_4287.jpg               ← passt nicht ins Profil → Rest-Ordner

Generierte Plan-Ordner-Vorschläge:
  Urlaub Italien 2026/   (2 Fotos)
  Familie Geburtstag 2026/   (2 Fotos)
  _Unsortiert/   (1 Foto)
```

**Workflow:**
1. User legt **Sorter-Profil** an (Token-Definition mit Trennzeichen, idealerweise Drag&Drop-Token-Editor)
2. Misch-Ordner auswählen → "Profil anwenden"
3. App generiert **Plan-Ordner mit Vorschlag** (Trefferquote pro Datei, Restposten in `_Unsortiert`)
4. User macht **Feinschliff per Drag&Drop** im Plan-Tree (Fotos zwischen Plan-Ordnern verschieben, Plan-Ordner umbenennen, neue erstellen)
5. **Apply** = normaler Plan-Ordner-Apply-Workflow

**Token-Typen:**
- `literal` (fester Text, z. B. `_`)
- `alphanumeric` (variable Wörter)
- `number` (Zahlen)
- `date` (Datums-Pattern, z. B. `YYYY`, `YYYY-MM-DD`)
- `regex` (für Edge-Cases)

Optional zusätzlich: Smart-Suggestions aus EXIF — "EXIF.TakenAt vorhanden → Plan-Ordner `YYYY/YYYY-MM`".

### Archiv-Auto-Extract

User-Wunsch: `.zip`/`.rar`/`.7z` beim Scan automatisch entpacken, Archiv danach in den Papierkorb.

**Bibliotheken:**
- `.zip`: `System.IO.Compression` (built-in)
- `.rar`/`.7z`/`.tar.gz`: **SharpCompress** (NuGet, Pure-C#, kein 7-Zip-Binary nötig)

**Workflow (Background-Service):**
1. Beim Scan oder per FileSystemWatcher entdecktes Archiv erkannt
2. Entpackt nach `<Archivname>/` parallel zur Datei
3. Erfolg → **Archiv in Papierkorb verschoben** (sicherer als direkt löschen, kann wiederhergestellt werden)
4. `ExtractedArchives`-Eintrag schreiben (für Audit / Undo)
5. **Round-System** für verschachtelte Archive (Archiv im Archiv nochmal scannen)

**Settings:**
- An/Aus
- Auto-Delete-Schwelle (z. B. nur löschen wenn Entpackung > 1 Datei)
- Recycle Bin vs. Direct Delete
- Konflikt: Ziel-Ordner existiert schon → Suffix `(2)`, `(3)`, … oder zusammenführen

### Lightbox: Zoom & Navigation

User-Wunsch: zoomen, mit Pfeiltasten und Buttons navigieren.

**Zoom (WPF native via `ScaleTransform` + `TranslateTransform`):**
| Aktion | Trigger |
|---|---|
| Zoom in | Mausrad ↑, `+`, `Strg+↑` |
| Zoom out | Mausrad ↓, `-`, `Strg+↓` |
| Fit-to-Window | `F` oder Doppelklick |
| 100% | `1` oder Strg+Doppelklick |
| Pan | Maus-Drag (wenn gezoomt) |
| Reset | `0` |

**Navigation:**
| Tastatur | Aktion |
|---|---|
| ← / → | vorheriges / nächstes |
| Bild↑ / Bild↓ | ±10 |
| Pos1 / Ende | erstes / letztes |
| Space | markieren |
| Enter | bestätigen |
| Entf | Papierkorb (Confirm) |
| Esc | Schließen |
| F | Vollbild-Toggle |
| I | Info-Panel toggle |
| R / Shift+R | drehen rechts/links |

Plus sichtbare ◀ ▶ Buttons im Lightbox-Layout (siehe Mockup), Filmstrip am unteren Rand.

### Migrations-Strategie

Schrittweise vom PowerShell-Projekt zur C#-App:

1. **Phase 1 — Skeleton & Core:** Solution + DI + DbContext + Migrations + leeres MainWindow mit NavView
2. **Phase 2 — Scan & Display:** ScanService indiziert Root-Ordner, GalleryView zeigt Thumbnail-Grid (Bilder zuerst, Videos später)
3. **Phase 3 — Lightbox:** Vollbild-Viewer mit Bildnavigation, dann Video via LibVLCSharp
4. **Phase 4 — Datei-Operationen:** Multi-Select, Verschieben, Kopieren, Löschen (Papierkorb), Umbenennen
5. **Phase 5 — Tags & Favoriten:** TagService + UI in Sidebar/Lightbox
6. **Phase 6 — Suche & Filter:** SearchService mit FTS5 (SQLite Full-Text-Search)
7. **Phase 7 — Plan-Ordner-Workflow (zentrales neues Feature):** Plans/PlanFolders-CRUD, Drag-and-Drop von Galerie auf Plan-Tree, Apply-Logik mit Konflikt-Behandlung und Undo, Cache-Verwaltung
8. **Phase 8 — Erweiterungen:** Token-basierter File-Sorter (Profile + Generator für Plan-Ordner + Drag&Drop-Feinschliff), Archiv-Auto-Extract (zip/rar/7z mit SharpCompress), animierte Video-Hover-Thumbnails (Multi-Frame-GIF), Flatten & Move (vereinfacht), Albums, Duplikat-Erkennung (pHash), Settings-UI, EXIF-Detail-Panel
9. **Phase 9 — Gesichtserkennung (offline, ONNX-basiert):** YuNet/RetinaFace für Detection + ArcFace/InsightFace für Embeddings via Microsoft.ML.OnnxRuntime, Auto-Clustering ähnlicher Gesichter, Personen benennen, Suche "Foto mit Person X"

**Risiken die Claude sieht:**
- LibVLCSharp + .NET 10: Kompatibilität verifizieren
- WPF-UI (lepoco) Roadmap für .NET 10 — schon offiziell unterstützt?
- Lange Pfade (>260) auf Windows — `\\?\`-Präfix nötig?
- FileSystemWatcher bei großen Bibliotheken: Event-Flut?
- EF Core + SQLite + Concurrent Writes: muss WAL-Mode aktiviert werden, evtl. Single-Writer-Pattern via Channel

---

## Aufgabe

Reviewe diesen Plan kritisch. Konkret in vier Bereichen:

### 1. Architektur-Stack
- Ist **WPF + .NET 10** wirklich die beste Wahl, oder wäre **WinUI 3** / **Avalonia** / **MAUI** für eine neue Desktop-App im Jahr 2026 sinnvoller?
- Ist **WPF-UI (lepoco)** stabil genug, oder gibt es bessere Fluent-Bibliotheken (z. B. ModernWpf, HandyControls, oder eigenes Theme)?
- **LibVLCSharp** vs. **MediaElement** vs. **MPV.NET** — was würdest du für einen User mit gemischten Codecs (HEVC, WMV3, MPEG-2) empfehlen?
- **CommunityToolkit.Mvvm** — passt, oder doch klassisch (MVVM Light, Prism)?
- **EF Core** für eine schlanke Single-User-App mit ~10–100 Tabellen Operations — Overkill, oder gerechtfertigt? Wäre **Dapper** / **LiteDB** / **SQLite raw via Microsoft.Data.Sqlite** besser?
- **SixLabors.ImageSharp** vs. **WPF native `BitmapImage`** für Thumbnail-Generierung — was ist schneller / kleiner im Footprint?

### 2. Datenbank-Schema
- Schema-Design grundsätzlich tragfähig?
- **Catalog-Modell** offen: zentrale DB unter `%LocalAppData%` oder Lightroom-Stil "DB neben Root-Ordner" (portabel, mehrere Bibliotheken)?
- **`Files.PHash`** als `BLOB`/`INTEGER` für Perceptual-Hash — sinnvoll inline oder lieber eigene `FileHashes`-Tabelle?
- **`Thumbnails`-Tabelle** notwendig, oder reicht Konvention `{file_id}_{size}.jpg` + Datei-Existenz-Check?
- **`History`-Tabelle** als Undo-Journal: ausreichend, oder besseres Pattern (Event-Sourcing-light)?
- **FTS5-Integration** für Suche: virtuelle Tabelle auf `Files.Name` + `Tags.Name` — Best Practice in EF Core?
- Concurrent-Write-Strategie SQLite: WAL-Mode + Single-Writer-Channel ausreichend, oder unnötig kompliziert?

**Plan-Ordner (zentrale neue Anforderung — bitte gründlich reviewen):**
- Schema-Design `Plans` + `PlanFolders` + `PlanFolderFiles` solide? Welche Indizes/Constraints kritisch?
- **Kardinalität**: 1:1 (Foto in genau einem PlanFolder) oder M:N empfehlenswerter?
- Ist es klüger, Plan-Ordner und **Albums** als **eine** Tabelle zu modellieren (mit Flag "applicable") statt zwei getrennte Konzepte? Oder strikt trennen?
- **Apply-Logik** als Transaction: was passiert bei Crash/Stromausfall mitten im Verschieben? (Two-Phase-Commit-light? Resume-Mechanismus?)
- **Konflikt-Behandlung** beim Apply (Datei mit gleichem Namen existiert schon im Ziel) — UI-Pattern oder Auto-Rename?
- **Undo nach Apply**: ist `History.PlanId` ausreichend, um einen ganzen Apply-Vorgang als Block rückgängig zu machen?

### 3. UI/UX & Design-Konzept
- Layout-Aufteilung (NavigationView + Content + Floating Action Bar) sinnvoll für eine Foto-App?
- Fehlt was Wesentliches im Layout? (Info-Panel rechts? Map-View für GPS-Fotos? Timeline-View?)
- **Multi-Select via Checkbox in Thumbnail** vs. **klassisch via Strg+Klick / Shift+Klick** — was würdest du empfehlen, oder beides?
- **Floating Action Bar** vs. **CommandBar oben** für Selektions-Aktionen — Vor-/Nachteile?
- **Lightbox als eigenes Window** vs. **Overlay im MainWindow** — was ist Standard für moderne Foto-Apps?
- Sind **Filter-Pills** (Alle / Bilder / Videos / Favoriten) das richtige Pattern, oder besser SegmentedControl / Dropdown?
- **Anzeige-Verhalten "tiefster Ordner immer im Grid"**: aus dem PowerShell-Vorgänger übernommen — UX-mäßig OK, oder verwirrend? (Was ist mit Foto-Ordnern, die sowohl Bilder als auch Sub-Ordner enthalten?)

**Plan-Ordner-UI (neu, zentrale Anforderung):**
- Wie würdest du das **Plan-Ordner-Erstellen + Foto-Zuweisen** in der UI gestalten? Vorschlag von Claude:
  - Eigene NavView-Section "Sortier-Pläne" mit Plan-Auswahl
  - Im Content: zwei Panels nebeneinander — links Galerie (Quelle), rechts Plan-Tree mit Drop-Zonen
  - Drag-and-Drop von Selektion auf Plan-Ordner
  - Plan-Ordner zeigt Anzahl zugewiesener Fotos als Badge
  - "Apply"-Button mit Confirmation-Dialog (zeigt: X Fotos werden zu Y Ordnern verschoben)
- Bessere UX-Pattern aus existierenden Apps die du kennst (Lightroom Collections, Apple Photos Albums, Eagle, …)?
- Wie macht man **klar visuell unterscheidbar**: physische Ordner (im Dateisystem) vs. virtuelle Plan-Ordner?

### 4. Migrations-Strategie
- **Phase 1–9**: Reihenfolge sinnvoll, oder gibt's Abhängigkeiten die ich verkenne?
- Was ist das **MVP** das du als V0.1 launchen würdest, um schnell Feedback zu bekommen?
- Welche Phasen sollte man **parallel** entwickeln können?
- **Risiken** die Claude oben aufgelistet hat — gibt es weitere die du siehst (Performance, Sicherheit, UX-Fallen)?

### 5. Erweiterte Features (Phase 8 + 9) — sind die Konzepte tragfähig?

**Token-basierter File-Sorter:**
- Token-System (`literal`, `alphanumeric`, `number`, `date`, `regex`) ausreichend, oder zu mächtig/zu eingeschränkt?
- **Drag&Drop-Token-Editor** in der UI: realistisch in WPF, oder zu aufwändig für ein Polish-Feature?
- Wie würdest du **Trefferquote** der Profile dem User sichtbar machen (welche Dateien matchen welches Token)?
- Sollte es **mehrere Profile** geben können, die nacheinander angewendet werden, oder nur eines?
- Wie umgehen mit **Konflikten** (zwei Profile matchen dieselbe Datei)?

**Archiv-Auto-Extract:**
- **SharpCompress** vs. **7-Zip-Binary-Wrapper**: was empfiehlst du für Robustheit (große RAR-Archive, passwortgeschützt)?
- **Round-System** für verschachtelte Archive: sinnvoll, oder zu aggressiv?
- **Original in Papierkorb** vs. direkt löschen — Default?
- Performance bei sehr großen Archiven (>10 GB) — Streaming-Extract oder vollständig in RAM?

**Animierte Video-Thumbnails (Multi-Frame-GIF):**
- **Animiertes GIF** vs. **animiertes WebP** vs. **MP4-Loop** für Hover-Preview — was rendert WPF am schnellsten und sieht am besten aus?
- 5 Frames × ~150 KB = ~750 KB pro Video bei 10.000 Videos = ~7,5 GB Cache — ist das vertretbar, oder lieber on-the-fly bei Hover?
- **Hover-only Animation**: gibt es WPF-Pattern, das das sauber macht ohne Performance-Probleme im virtualisierten Grid?
- Empfohlene **Frame-Anzahl** und **Frame-Positionen** als Default (ich habe 5 Frames bei 10/30/50/70/90% angenommen)?

**Thumbnail-Generierungs-Priorität (User-Wunsch — bitte besonders gründlich reviewen):**
- 4-Stufen-Prio-System (Critical / Viewport / Current Folder / Background) sinnvoll, oder zu komplex?
- **`PriorityQueue<T,P>`** aus .NET 6+ ausreichend, oder eigene Multi-Channel-Implementierung mit `System.Threading.Channels`?
- **Pause-by-finishing-current-job** (kein harter Cancel) — robust genug, oder gibt es Use-Cases wo Cancel wichtig wäre (z. B. langes Video-Thumb dauert 5 Sek, blockiert UX)?
- **Dedupe-Strategie**: wie effizient implementieren? `HashSet<FileId>` neben der Queue? Oder Queue-Item-Cancel-Flag?
- **Restart-Resume**: DB-Vergleich `Thumbnails.GeneratedAt > Files.Modified` bei App-Start ausreichend, oder zusätzlich Persistierung der Queue-Reihenfolge nötig?
- **Throttling**: bei Akku-Betrieb / Vollbild-Anwendung im Vordergrund weniger Worker — Best Practice in WPF?
- **UX-Pattern Skelett-Shimmer + Crossfade**: gibt es bessere Patterns aus modernen Apps (z. B. Apple Photos, Lightroom)?
- **Performance bei 100.000+ Files**: skaliert das, oder muss ich z. B. **batched Enqueue** (1000er-Blöcke) machen statt alles auf einmal?

**Lightbox-Zoom & Navigation:**
- WPF native `ScaleTransform` + `TranslateTransform` ausreichend, oder besser **InkCanvas** / Drittanbieter-Library für smoothes Zoomen mit Mausrad?
- **Pan-Bounds**: wenn gezoomt, soll Bild über Fenster-Rand hinaus pan-bar sein, oder am Rand stoppen?
- Tastatur-Shortcuts: passend gewählt, oder Standard-Konventionen aus anderen Foto-Apps die ich verkenne (z. B. macOS Vorschau, IrfanView, FastStone, XnView)?

**Gesichtserkennung (Phase 9):**
- **FaceONNX** (NuGet Wrapper) vs. **Microsoft.ML.OnnxRuntime + InsightFace ONNX direkt**: Performance, Wartbarkeit, Modell-Updates — was würdest du wählen?
- **Modell-Distribution**: ONNX-Files (~25–100 MB) im Installer mitliefern, oder bei erstem Start downloaden?
- **Auto-Clustering**: HDBSCAN, K-Means, oder einfacher Threshold-Cluster auf Cosine-Distance?
- **GPU-Acceleration via DirectML**: sinnvoll als Default-Aktivierung, oder Opt-In wegen Treiber-Problemen?
- **Schema `Faces.Embedding BLOB`** ausreichend, oder spezialisierte Vektor-DB nötig (z. B. **sqlite-vec** Extension)?
- **Privacy/UX**: User klar machen dass Daten **nur lokal** sind (kein Cloud-Sync) — wie kommunizieren?

---

Sei direkt, kritisch, konkret. Ich bin nicht beleidigt wenn etwas Quatsch ist — ich will den besten Plan.

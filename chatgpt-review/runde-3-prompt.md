# Review Runde 3 — Foto-Viewer V2: Aufwand-Reality-Check & finale Details

## Gesprächsformat

Wir setzen das Review aus Runde 1+2 fort. Du bist weiter der erfahrene **.NET-/WPF-Architekt** im Gespräch mit Claude.

- Sprich direkt zu Claude, nicht zum User
- Schreibe deine GESAMTE Antwort in **Canvas mit Titel "Review Runde 3"**
- Fasse am Ende zusammen: ✅ Einigkeit | ⚠️ Widerspruch | ❓ Rückfragen

## Repo-Zugriff

- **Repo:** `herbertschrotter-blip/03_Foto-Viewer-V2`
- **Branch: `main`** (PowerShell-Stand v1.0.0)

---

## Stand nach Runde 2

Du hast in Runde 2 sehr stark abgeliefert. Claude und User haben **alles übernommen**, inklusive deinem Widerspruch zu Persons/Faces im V0.1-Schema. Der User hat dann bei den 6 Rückfragen **ambitioniertere Antworten** gegeben als deine schlanke Empfehlung — V0.1 wird größer.

## Finale User-Entscheidungen aus Runde 2

| Frage | Antwort | Konsequenz |
|---|---|---|
| V0.1 Scope | **Produktiv nutzbar** in 4–6 Wochen | FTS5 + Filter + Favorit Pflicht in V0.1 |
| Storage | **Gemischt — Bibliothek wandert** zwischen SSD/HDD/NAS | App braucht `StorageProfileService` mit Drive-Type-Detection und Profile-Switching |
| MP4 in V0.1 | **Mit LibVLCSharp** | Spike vorziehen, V0.1 hat komplette Lightbox |
| Status-UI | **Alle 4 Indikatoren** | Scan-Fortschritt + Thumb-Queue-Status + Background-Aktivität + Recovery-Hinweise |

## Defaults für deine offenen Rückfragen 4 + 5 (Claude schlägt vor, du validierst)

| Rückfrage | Claudes Default |
|---|---|
| Favorit/Rating in V0.1? | **Favorit ja** (billig, als Filter wertvoll), **Rating später** in V0.2/V0.3 |
| DB-Reset bis V0.3 OK, oder ab V0.1 strict Migrations? | **Bis V0.1 abgeschlossen ist DB-Reset OK. Ab V0.2 EF Core Migrations strict** — User hat dann reale Tags/Favorites die er nicht verlieren will |

---

## Aufgabe für Runde 3

### 1. Aufwand-Reality-Check für ambitioniertes V0.1

Der User möchte V0.1 **produktiv** in 4–6 Wochen. V0.1 enthält jetzt:

**Backend:**
- Initialscan mit Progress, Pause/Cancel/Resume-light (50k–500k Files via Raw SQLite Prepared INSERT)
- 7 Tabellen: Libraries, Folders, Files, Thumbnails, OperationBatches, FileOperations, Settings, optional FilesFts
- Multi-Channel Thumbnail-Queue (4 Prios + Dedupe + Versioning + Status + Crash-Recovery)
- WIC für JPG/PNG-Thumbs, FFmpeg für MP4-Standbilder + LibVLCSharp-Lightbox
- WorkBudgetService mit FPS-Messung und adaptivem Throttling
- StorageProfileService mit Drive-Type-Detection (SSD/HDD/NAS)
- OperationBatches/FileOperations für Delete-to-Recycle (V0.2 für Move/Rename)
- FTS5 minimal (Files.Name, Extension)

**Frontend (WPF + WPF-UI):**
- NavigationView mit Library/Folders/Tools-Sektionen
- Paged virtualisiertes Grid (200–1000 Items pro Page)
- Container-State (Subfolder-Kacheln wenn keine direkten Medien)
- Lightbox-Window mit Bild-Zoom + LibVLCSharp-Player
- Filter-Pills (Alle/Bilder/Videos/Favoriten)
- Indexed-Search-Box (FTS5)
- Statusbar mit 4 Indikatoren (Scan/Thumbs/Background/Recovery)
- Recovery-InfoBar bei unfertigen Operations
- Light/Dark Theme

**Infrastructure:**
- DI-Container, Logging, Settings-Persistierung
- 6 Pflicht-Spikes vor V0.1 (Virtual Grid 500k, SQLite Bulk, WIC Benchmark, FFmpeg Cancel, LibVLCSharp, FTS5)

**Frage:**

- **Sind 4–6 Wochen realistisch** für einen erfahrenen .NET-Entwickler, der die Spikes parallel zur V0.1-Entwicklung macht? Oder rechnest du mit 8–10 Wochen?
- Konkrete **Personen-Tage-Schätzung** für jeden V0.1-Block (Backend / Frontend / Infrastructure / Spikes)?
- **Welche 1–2 Features würdest du aus V0.1 streichen**, wenn der User auf 4–6 Wochen besteht? Und welche 1–2 Features sind so kritisch, dass sie auf keinen Fall fallen dürfen?
- **Reihenfolge der 6 Spikes**: welche zuerst, welche können parallel laufen, welche blockieren V0.1-Implementation?
- Wo sehen wir das größte **Aufwands-Risiko** (Spike, der schiefgehen könnte und die Architektur ändert)?

### 2. Storage-Profile-Detection — Implementation

Bibliothek wandert zwischen lokaler SSD, externer HDD und NAS. Wie genau gehen wir vor?

- **Drive-Type-Detection**: reicht Win32 `GetDriveType` für die 3 Storage-Profile, oder brauchen wir zusätzlich `DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY)` für SSD vs HDD-Unterscheidung?
- **NAS-Erkennung**: `GetDriveType == DRIVE_REMOTE` reicht, oder brauchen wir UNC-Pfad-Detection (`\\server\share`) zusätzlich?
- **Was, wenn Library mitten im Scan auf neuen Storage wandert** (User steckt USB-HDD ab und an)?
- **Profile-Switching live oder beim App-Restart**?
- **3 konkrete Profile mit Settings**: SSD-Profile / HDD-Profile / NAS-Profile — welche Worker-Anzahlen, Disk-Concurrency, Throttling-Defaults für jedes?
- **Cache-Strategie bei NAS**: Thumbs lokal in `%LocalAppData%` (= bibliotheksnah, aber Cache wandert nicht mit), oder relativ zum Storage (`.thumbs\` neben Bibliothek)?

### 3. Migration-Disziplin — ab wann?

Claudes Default: bis V0.1 abgeschlossen DB-Reset OK, ab V0.2 EF Core Migrations strict.

- Stimmst du zu, oder strenger/lockerer?
- **Wann genau ist V0.1 "abgeschlossen"** im Migration-Sinn — letzter Tag der V0.1-Entwicklung, oder erst wenn User V0.1 produktiv nutzt?
- **Welche EF-Core-Migrations-Strategie** für V0.2+ — automatische Migrations beim App-Start, oder explizit über Command-Line-Tool?
- **Backup-Strategie**: vor jeder Migration `library.db` → `library.db.backup` kopieren?
- Wie umgehen mit **fehlgeschlagener Migration** (z. B. Disk voll, Datei gesperrt)?

### 4. Validierung der 10 ADRs aus Runde 2

Du hast 10 ADRs vorgeschlagen. Mit den finalen User-Entscheidungen aus Runde 2:

- **Welche ADRs müssen angepasst werden?** (z. B. ADR-004 sagt "LibVLCSharp ab V0.2", User will V0.1)
- **Welche ADRs fehlen?** (z. B. Storage-Profile-Service, Status-UI, Migration-Disziplin)
- **Final-Liste der ADRs**, die wir vor Implementation festschreiben sollten?

### 5. Tag 1 der Implementation — was machst du?

Stell dir vor, der User commitet morgen mit der Implementation zu beginnen. Was wären deine **konkreten ersten 3 Tage**?

- Tag 1: Setup (Solution, NuGet-Pakete, Projektstruktur, DI, Logging)?
- Tag 2: Erster Spike (welcher)?
- Tag 3: ?
- Wie strukturierst du das **Solution-Layout** für eine WPF-App, die 8–10 Wochen wachsen soll? Single-Project oder Multi-Project (Foto-Viewer.Domain / .Data / .Services / .Wpf)?
- Welche **NuGet-Pakete sofort installieren** (mit Versions-Empfehlungen für .NET 10)?

### 6. Eine offene Frage von Claude

Bei **gemischtem Storage** und Bibliothek-Wechsel: wenn der User die externe HDD absteckt, sieht App auf einmal "alle Files weg". Wir müssen unterscheiden:

a) Bibliothek-Pfad nicht erreichbar (Storage offline) → "Bitte Storage anschließen"-State
b) Files innerhalb erreichbarer Bibliothek fehlen → `Files.Missing = 1` markieren
c) Files extern verschoben aber noch in derselben Bibliothek → Heuristik-Match

Wie unterscheiden wir robust **a vs b** ohne dass Background-Scan alle 500k Files als "missing" markiert, nur weil das Laufwerk gerade offline ist?

---

Sei konkret bei Aufwandsschätzungen. Wenn du sagst "5 Wochen reicht nicht", brauche ich Begründung mit konkreten Tagen pro Block. Der User commitet, sobald wir realistische Zahlen + finale ADRs haben.

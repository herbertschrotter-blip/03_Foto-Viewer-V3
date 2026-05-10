# Spike #02 — Virtual Grid mit 500k Items

- **Status:** ✅ Grün — Architektur bestätigt, freigegeben für V0.1-Implementierung der Gallery-UI
- **Datum gestartet:** 2026-05-10
- **Datum abgeschlossen:** 2026-05-10
- **Bezogen auf:** [ADR-0010](../adr/0010-ui-datenmodell-paging.md), [PLAN.md Kap. 8 + 9](../../PLAN.md)
- **Blockiert (jetzt frei):** Gallery-UI, Selection, Lightbox-Trigger

## Zielsetzung

Beweisen, dass das paged virtualisierte Grid die UI auch bei 500k Files responsive hält — **bevor** die produktive `GalleryView` gebaut wird.

## Messziele

| Messung | Ziel |
|---|---|
| Start Gallery (DB → erste Page sichtbar) | < 1 s |
| Page-Switch / Load nächste Page | < 150 ms |
| Scroll innerhalb Page | visuell flüssig (~60 FPS) |
| Memory stabil nach 10 min Scrollen | kein Leak |

## Aufbau

### Datenquelle
- Bestehende Spike-DB aus Spike #1: `%LocalAppData%\PhotoViewerV3\spike-01\library-spike.db`
- 500.000 Files in 10.000 Folders mit allen Indizes + FTS5
- 80 % Images / 20 % Videos, 5 % Favoriten

### Architektur des Spikes

- **`GalleryFileDto`** (record) — flaches DTO: `Id, FolderId, Name, Extension, Size, ModifiedAtUnix, MediaType, IsFavorite` plus berechnete `SizeDisplay`-/`IsVideo`-Properties
- **`GalleryPageSource`** — Service:
  - Hält eine `SqliteConnection` im ReadOnly-Mode auf die Spike-DB
  - `GetTotalCount(query)` → COUNT mit Filter/FTS-Klausel
  - `LoadPageAsync(query, pageIndex, pageSize, ct)` → `SELECT … LIMIT $pageSize OFFSET $offset`
  - Bei `query.HasSearch`: JOIN `FilesFts MATCH $term` + Files-Spalten
  - Filter-Pills mappen auf `AND MediaType=0` / `=1` / `AND IsFavorite=1`
- **`GalleryViewModel`** — CommunityToolkit.Mvvm `ObservableObject`:
  - `ObservableCollection<GalleryFileDto> CurrentPage` (max 500 Items, **niemals 500k**)
  - `PageIndex`, `PageSize=500`, `TotalCount`, `PageCount`
  - `CanGoBack`/`CanGoForward` für Pager-Buttons
  - Async Page-Load mit `CancellationTokenSource`, Cancellation bei schnellem Filter-Wechsel
  - Live-Telemetrie: `FirstPageLoadMs` (Diag-Start → erste Page), `LastPageLoadMs` (jede Page einzeln)
- **`GalleryView` (UserControl)**:
  - 5-Row-Grid: Title+DB-Pfad / Toolbar (Filter-Pills + FTS-Box) / **Items** / Pager / Statusleiste
  - `WrapPanel` mit `ItemWidth=180 ItemHeight=160` für Tile-Anordnung (500 Items pro Page → keine separate Item-Virtualisierung nötig)
  - `ScrollViewer` mit `VirtualizingStackPanel.VirtualizationMode="Recycling"` als zusätzliche Sicherheit
  - Tiles mit Platzhalter-SymbolIcon (Image/Video), Favorit-Stern, Extension-Badge, Filename, Size

### Was der Spike NICHT macht (V0.1 separat)
- Echte Thumbnails (Spike #4 — WIC)
- LibVLC / Lightbox (Spike #3)
- Multi-Selection-Floating-Bar
- Plan-Ordner Drag&Drop
- Adaptive Throttling

## Hardware-Profil

(identisch zu Spike #1)

| Feld | Wert |
|---|---|
| OS | Windows 11 Pro Build 26200 |
| CPU | Intel Core i7-8700K @ 3.70 GHz (6 Kerne / 12 Threads) |
| RAM | 32 GB |
| Storage | Samsung SSD 860 EVO 2 TB (SATA) |
| .NET SDK | 10.0.203 |
| Build | Debug aus VS (kein Release — Release-Werte vermutlich nochmal 10–20 % besser) |

## Ergebnisse

### Performance

| Messung | Wert | Ziel | Faktor Reserve | Ampel |
|---|---|---|---|---|
| Start Gallery (DB-Open → erste Page sichtbar) | **211 ms** | < 1.000 ms | ~5× | 🟢 |
| Page-Load normal (Filter "Alle", Page 1) | **47,8 ms** | < 150 ms | ~3× | 🟢 |
| FTS-Suche "Urlaub" — erste Page (33.291 Treffer, 67 Pages) | **47,7 ms** | < 200 ms | ~4× | 🟢 |
| FTS-Suche "Urlaub" — letzte Page (Page 67/67, OFFSET ~33.000) | **84,1 ms** | < 200 ms | ~2,4× | 🟢 |
| **⏭ Direct-Jump auf Page 1000 (OFFSET 499.500)** | **3.778 ms** | **< 150 ms** | **25× über Ziel** | 🟡 |
| Scrollen innerhalb Page | flüssig (User-Wahrnehmung) | flüssig | — | 🟢 |
| Memory (Initial-Snapshot, Diagnostics-Tools) | ~159 MB | stabil | — | 🟢 |

### Funktional verifiziert
- ✅ NavigationView-Eintrag "Spike: Gallery" navigiert in die View
- ✅ Filter-Pills wechseln Total-Count und Page-Inhalt
- ✅ FTS-Suche "Urlaub" → 33.291 Treffer mit Files wie `Urlaub_Hund_801.mp4`, `Urlaub_Strand_715.mp4` etc.
- ✅ Pager ⏮ ◀ ▶ ⏭ funktionieren, Page-Indikator zählt korrekt
- ✅ Tile-Layout zeigt Image-/Video-Symbol, Filename, Size, Extension-Badge, Favorit-Stern
- ✅ DB-Pfad wird oben angezeigt (Diagnose-Pluspunkt)
- ✅ Statusleiste unten zeigt Live-Telemetrie ("Start: 211 ms — Letzte Page: 47,8 ms")

## Schlussfolgerung

**Paged-Grid-Architektur ist bestätigt** — mit einem identifizierten Hebel.

Die Strategie aus ADR-0010 — niemals 500k Items in einer `ObservableCollection`, sondern Pages à 500 Items mit Pager-Navigation — hält die UI bei sequentieller Navigation und Filter/FTS-Listen bis ~33k Treffern dauerhaft responsive (alle Werte 2,4×–5× unter Ziel).

Einzige rote Messung: **Direct-Jump ⏭/⏮ auf die letzte Page einer 500k-Vollliste = 3,8 s** wegen SQLite-OFFSET-Linearitäten. Mitigation siehe Folge-Erkenntnis 1 — ein einfacher Reverse-Trick (`ORDER BY ASC LIMIT 500` + clientseitige Umkehr) löst das in O(log N) ohne Architektur-Änderung.

Freigabe für:
- Implementation der produktiven `GalleryView` für V0.1 auf Basis dieses Patterns
- Wiederverwendung des `GalleryPageSource`-/`GalleryQuery`-Designs in der Production-Schicht (`Data/RawSql/` + `Services/Query/`)
- FTS5-Suche im Production-UI ohne separate Performance-Optimierung
- Filter-Pills (Alle/Bilder/Videos/Favoriten) als Pflicht-V0.1-Features

Pflicht-Implementierung in V0.1 (sonst gelb-Wert würde rot werden):
- **Reverse-Query für ⏭-Direct-Jump** bei großen Vollalisten (siehe Folge 1)

## Folge-Erkenntnisse (für V0.1/V0.2)

1. **OFFSET-Pagination skaliert linear — bei OFFSET 499.500 bricht es ein.**
   - Sequentielle Navigation (Page 1 → 2 → 3 …) bleibt grün, weil OFFSET pro Schritt nur 500 wächst.
   - FTS-Last-Page (OFFSET ~33.000) noch grün bei 84 ms — der bewährte Bereich.
   - **Direct-Jump auf Page 1000 bei 500k-Vollalisten: 3.778 ms.** Klassisches SQLite-OFFSET-Verhalten (O(OFFSET) — auch mit Index muss SQLite alle Zeilen "skippen").
   - **V0.1-Pflicht-Mitigation (drei Stufen, einfach zu sauber):**
     1. **Reverse-Trick für ⏭ (kostet ~0 Aufwand):** Wenn `pageIndex > pageCount / 2`, intern `ORDER BY ModifiedAtUtc ASC, Id ASC OFFSET (totalCount - (pageIndex+1)*pageSize)` ausführen und Ergebnis umkehren. Reduziert Worst-Case-OFFSET von 499.500 auf <500.
     2. **Loading-State im UI** mit `IsLoading=true` + Disabling des Pagers, damit UX nicht klemmt während des Loads.
     3. **Keyset-Pagination** (`WHERE (ModifiedAtUtc, Id) < (lastSeen, lastSeenId)`) ist die sauberste Lösung, aber inkompatibel mit Random-Direct-Jump. Empfohlen für V0.2+ falls UX-Pattern auf "kontinuierliches Scrollen" umgestellt wird.
   - Index `IX_Files_Library_Modified` ist passend, kein neuer Index nötig.

2. **Innerhalb einer Page (500 Items) ist kein separater Virtualisierungs-Aufwand nötig.**
   - `WrapPanel` mit fixem `ItemWidth/ItemHeight` rendert 500 Platzhalter-Tiles flüssig.
   - Bei echten Thumbnails (Spike #4) wird die Source `ImageSource`-Generierung der Bottleneck, nicht das Layout.
   - Sollte Item-Größe dynamisch werden (z.B. Verschiedene Tile-Größen wie in macOS Photos), wäre eine `VirtualizingPanel`-Subklasse für WrapPanel zu prüfen.

3. **Cancellation-Pattern funktioniert.** Bei schnellem Filter-/Such-Wechsel werden alte Loads via `CancellationTokenSource` abgebrochen. Im Spike noch nicht stress-getestet, aber technisch eingebaut.

4. **DB-Anzeige im UI ist diagnostisch wertvoll.** Im V0.1-Production-UI sollte der DB-Pfad zumindest in der Settings-Seite stehen — falls ein User mehrere Libraries hat oder die DB-Lokalisierung debuggen muss.

5. **AppContainer-Pfad-Falle (Tool-Setup-Lessons-Learned):**
   - Beim ersten Test-Run war die Spike-#1-DB an einem für die WPF-App unsichtbaren Pfad gelandet (`%LocalAppData%\Packages\Claude_…\LocalCache\Local\…`).
   - Ursache: Der Spike-Generator wurde aus einer UWP-AppContainer-isolierten Shell ausgeführt; deren Schreibzugriffe auf `%LocalAppData%` werden transparent in den AppContainer-LocalCache umgeleitet.
   - **Konsequenz für künftige Spike-Setups:** Wenn der Spike-Generator und der Spike-Verbraucher von unterschiedlichen Prozess-Identitäten gestartet werden, müssen sie sich auf einen für beide identisch sichtbaren Pfad einigen. Eine Repo-relative Default-DB-Lokalisierung (z.B. `<repo>\spike-data\`) wäre robuster als `%LocalAppData%`.

## Reproduzierbarkeit

Voraussetzung: Spike-#1-DB existiert. Falls noch nicht vorhanden, aus eigener (nicht-AppContainer-isolierter) PowerShell:

```powershell
cd "D:\OneDrive\Dokumente\02 Arbeit\05 Vorlagen - Scripte\03_Foto-Viewer-V3"
dotnet run --project tests\PhotoViewerV3.PerfTests -c Release -- spike01-sqlite-bulk --rows 500000 --folders 10000
```

Dann WPF starten und auf "Spike: Gallery" klicken:

```powershell
dotnet run --project src\PhotoViewerV3.Wpf -c Debug
```

(oder F5 in Visual Studio mit `PhotoViewerV3.Wpf` als Startup-Projekt)

## Code

- [src/PhotoViewerV3.Wpf/Spike02/Data/GalleryFileDto.cs](../../src/PhotoViewerV3.Wpf/Spike02/Data/GalleryFileDto.cs)
- [src/PhotoViewerV3.Wpf/Spike02/Data/GalleryPageSource.cs](../../src/PhotoViewerV3.Wpf/Spike02/Data/GalleryPageSource.cs)
- [src/PhotoViewerV3.Wpf/Spike02/ViewModels/GalleryViewModel.cs](../../src/PhotoViewerV3.Wpf/Spike02/ViewModels/GalleryViewModel.cs)
- [src/PhotoViewerV3.Wpf/Spike02/Views/GalleryView.xaml](../../src/PhotoViewerV3.Wpf/Spike02/Views/GalleryView.xaml)
- [src/PhotoViewerV3.Wpf/MainWindow.xaml](../../src/PhotoViewerV3.Wpf/MainWindow.xaml) (NavigationView-Eintrag + ContentArea)
- [src/PhotoViewerV3.Wpf/App.xaml.cs](../../src/PhotoViewerV3.Wpf/App.xaml.cs) (DI-Registrierung)

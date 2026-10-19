# Changelog

Alle nennenswerten Änderungen an PhotoViewer V3. Format: `[Version] — Datum`, oberster Eintrag = aktuelle Version
(Versionsquelle für das Commit-Profil in `CLAUDE.md`).

## [0.2.8] — 2026-10-19

### Hinzugefügt
- **Archive im Katalog** (ADR-0022): `MediaType.Archive = 2`; das Formatregister führt `.zip .rar .7z .tar .gz .tgz .bz2 .xz`
  als dritte Liste, der Scanner nimmt Archive automatisch auf (Files-Zeile, Ordnerkette, FTS). Neu `IMediaFormatRegistry.IsMedia`
  (nur Foto/Video) — Sortierer und Flatten zählen Archive nicht als Mediendateien. Kein Thumbnail: `ThumbnailService`
  reiht Archive nicht ein; die Kachel zeigt Archiv-Symbol, Endung (Badge) und Größe. Filter-Pill „Archive“.
- **`IArchiveService`** (`Services/Archive`, `AddArchives()`): `ListAsync` (Einträge mit Pfad, Größe, komprimiert, Datum,
  Medien-/Archiv-Markierung, Verschlüsselung) und `ExtractAsync` mit Zielordner (Standard: Unterordner mit dem Archivnamen
  neben dem Archiv, `x.tar.gz` → `x`), vorhandene Dateien überspringen/überschreiben/umbenennen, nur Medien oder alles,
  verschachtelte Archive im Runden-System bis Tiefe 3 (V2-Vorbild, Zwischen-Archive werden danach entfernt),
  Zip-Slip-Schutz, Abbruch, Fortschritt (Dateien/Bytes), atomares Schreiben über `.pv3part`. Danach Incremental-Scan der
  Bibliothek und optional Archiv in den Papierkorb (`IFileOperationService.RecycleAsync`, nur ohne Fehler).
  Passwortgeschützte Archive: `ArchiveEncryptedException` mit klarer Meldung (kein Passwort-Dialog); beschädigte:
  `ArchiveFormatException`. Lesen über **SharpCompress 0.50.6** (neu, nur im Services-Projekt), ZIP-Fallback über
  System.IO.Compression.
- **Entpacken-Dialog** (`Wpf/Gallery/Archives`, Muster `AppDialogWindow`): Ziel, Optionen, Fortschritt mit Abbrechen,
  Ergebnis mit Fehlerliste; mehrere Archive nacheinander, Scan einmal nach dem letzten. Aufruf über die Aktionsleiste
  („Entpacken…“ für ausgewählte Archive), das neue Kachel-Kontextmenü („Inhalt anzeigen“, „Entpacken…“, „Im Explorer
  anzeigen“) und die Lightbox.
- **Lightbox-Inhaltsansicht** (`Lightbox/ArchiveContentView`): Enter/Doppelklick auf ein Archiv zeigt statt eines Bildes die
  Liste der Einträge (Größe, komprimiert, Datum, Symbol je Typ) mit „Entpacken…“ (über `ExtractArchiveRequestedMessage` an
  die Galerie) und „Im Explorer anzeigen“; Info-Panel „Typ: Archiv“ mit Datei-/Medienzählern.
- Rauchtest-Schritt „Archiv: ZIP → Kachel → Inhaltsansicht → Entpacken → Katalog“ (ZIP mit Unterordner, Textdatei und
  verschachteltem ZIP in „Unsortiert“; Screenshots 11–13).

### Behoben (während der Umsetzung gefunden)
- SharpCompress `ExtractAllEntries()` funktioniert nur für solide RAR und 7z; ZIP lief deshalb unbemerkt immer über den
  Fallback, TAR und nicht-solide RAR schlugen fehl. Jetzt Eintrag für Eintrag, `ExtractAllEntries` nur für solide Archive/7z.
  Der ZIP-Fallback greift nur noch, solange kein Eintrag verarbeitet wurde (keine doppelten Dateien).
- SharpCompress 1.0.0 ist auf NuGet zurückgezogen (unlisted) und lieferte PAX-Header in TAR als Dateien mit gekürzten Namen;
  umgestellt auf die neueste gelistete Version 0.50.6 (`OpenArchive`/`OpenReader`), TAR-Verwaltungseinträge werden
  zusätzlich übersprungen.

### Tests
- `tests/PhotoViewerV3.Tests`: 281 (+40, `Archive/`): Register-Klassifikation, Scanner nimmt Archive auf, ThumbnailService
  reiht sie nicht ein, `ListAsync`/`ExtractAsync` mit erzeugten ZIPs (Unterordner, Zip-Slip, verschachtelt bis Tiefe 3 und
  Tiefen-Grenze, Überspringen/Überschreiben/Umbenennen, nur Medien, Abbruch ohne Teildateien, verschlüsseltes und
  kaputtes ZIP), GZ, TAR, TAR.GZ, TAR mit 140 Zeichen langem PAX-Namen, Papierkorb + Incremental-Scan nach dem Entpacken.
  RAR (normal, solide, passwortgeschützt, verschlüsselte Köpfe) und 7z über eingecheckte Mini-Archive
  `Archive/TestData/` (erzeugt mit WinRAR bzw. Windows-bsdtar).
- `tests/PhotoViewerV3.Wpf.Tests`: 56 (+5, `Archive/`): Kachel-Daten, Inhaltszeile, Dialog-ViewModel (mehrere Archive,
  eigener Ordner, Fehlerliste), echtes Dialogfenster.
- Rauchtest-Szenario: 17/17 OK (nach dem Merge mit der Ordnerüberwachung 0.2.7), 0 WPF-Binding-Fehler.

### Offen
- „Archive: automatisch entpacken beim Scan“ (Einstellung, Standard aus) — der Service ist dafür vorbereitet.
- „Überschreiben“ ersetzt vorhandene Dateien direkt (kein Papierkorb, kein FileOperation-Eintrag).
- Mehrere Archive im Dialog: scheitert das letzte Archiv komplett, entfällt der abschließende Scan (Dateien erscheinen
  erst nach „Aktualisieren“).

## [0.2.7] — 2026-10-19

### Hinzugefügt
- **Ordnerüberwachung mit automatischem Diff-Scan** (`Services/Watch`: `ILibraryWatchService`/`LibraryWatchService`,
  `WatchPathFilter`, `WatchSettingsStore`, `AddWatch()`; ADR-0019 nannte den FileSystemWatcher bereits als möglichen
  Auslöser für „Aktualisieren“): ein `FileSystemWatcher` je Wurzel der aktiven Bibliothek (rekursiv, Created/Deleted/
  Renamed/Changed, 64 KB Puffer; die Wurzelliste ist in `GetRootPaths` gekapselt — für Bibliotheken mit mehreren
  Wurzeln nur dort erweitern). Relevant sind nur Medienformate aus dem Formatregister; `.tmp`/`.part`/`~`-Dateien,
  `Thumbs.db`, `.thumbnails`, `@eaDir`, Papierkorb und der eigene Thumbnail-Cache werden ignoriert, Ordner zählen bei
  Anlegen/Löschen/Umbenennen. Entprellung: Ereignisse werden gezählt, jedes startet die **Ruhezeit** (Standard 3 s)
  neu; erst danach läuft ein Incremental-Scan über `IScanService` — nie parallel zu einem laufenden Scan (eigener oder
  manueller, erkannt an den Scan-Nachrichten), gesammelte Ereignisse werden nach dessen Ende erneut geprüft. Endet der
  automatische Scan mit `MassMissingBlocked` oder `LibraryOffline`, wird nicht automatisch wiederholt (Zustand
  `Blocked` mit Hinweis), bis ein manueller Scan erfolgreich war, die Bibliothek wechselt oder die Einstellung
  umgeschaltet wird. Nicht erreichbare Wurzeln werden alle 30 s erneut versucht; Watcher-Fehler (Pufferüberlauf) →
  Neustart + Scan. Wechsel per `ActiveLibraryChangedMessage`; Start in `App.OnStartup`, Stop über Dispose des
  Singletons beim Beenden. Zustände gehen als `WatchStatusChangedMessage` an den Messenger.
- **Einstellungen**: Abschnitt „Ordnerüberwachung“ mit Schalter „Bibliotheksordner überwachen“ (Standard: an) und
  Ruhezeit in Sekunden (1–60, NumberBox); Settings-Tabelle `watch.enabled`/`watch.quietSeconds`, sofort wirksam.
- **Statusleiste**: Hinweis „Änderungen erkannt — Aktualisierung in n s“ (Countdown), „Automatische Aktualisierung
  läuft…“, „Automatisch aktualisiert: n neu · …“ (12 s) bzw. „Automatische Aktualisierung angehalten: …“ (Warnsymbol).
- README: Abschnitt „Ordnerüberwachung“.

### Tests
- `tests/PhotoViewerV3.Tests`: +30 (`Watch/LibraryWatchServiceTests` mit echtem FileSystemWatcher auf dem Temp-Ordner der
  `TempLibraryFixture` und Scan-Fake: Entprellung mehrerer Ereignisse → ein Scan, Anlegen/Löschen → je ein Scan, kein
  Doppelstart während des eigenen oder eines manuellen Scans, Temp-/Thumbnail-/Nicht-Medien-Dateien ignoriert, Stop,
  Schalter und Ruhezeit (Persistenz, Clamp), Mass-Missing-/Offline-Sperre und Aufhebung, nicht erreichbare Wurzel mit
  Wiederholung, Bibliothekswechsel; `Watch/WatchPathFilterTests`; Settings-Roundtrip).
- Rauchtest: neuer Schritt „Ordnerüberwachung: Datei kopieren → automatischer Diff-Scan → Galerie“ (Ruhezeit 1 s;
  Kopie → Countdown in der Statusleiste → Incremental Added 1 → Kachel sichtbar; Löschen → MarkedMissing 1 →
  „fehlt“-Badge). Die Überwachung ist in den übrigen Schritten ausgeschaltet, damit kein automatischer Scan parallel
  zu den manuellen Scans und Datei-Operationen läuft. 16/16 OK.

### Offen
- Datei-Operationen der App selbst (Verschieben, Papierkorb, Sortierer) lösen über den Watcher nach der Ruhezeit einen
  Incremental-Scan aus, der in der Regel „keine Änderungen“ findet — bei sehr großen Bibliotheken auf HDD ein
  vollständiger Baumlauf. Nächster Schritt wäre, eigene Operationen (OperationBatch) im Watcher auszublenden.
- Restrisiko Doppel-Scan: ein manueller Scan, der genau im Fenster zwischen Ablauf der Ruhezeit und seiner ersten
  `ScanProgressMessage` startet, wird nicht erkannt (SQLite-Single-Writer bleibt, der Katalog wäre doppelt
  verglichen).

## [0.2.5] — 2026-10-19

### Tests
- WPF-Testfenster (`AppDialogWindowTests`, `GalleryGridHost`) liegen bei −5000/−5000 und werden nicht aktiviert — sie
  erschienen sonst während der Testläufe auf dem Desktop (Dialog „Ungewöhnlich viele Dateien fehlen … 142.381“).

## [0.2.4] — 2026-10-19

### Geändert
- **Galerie-Grid UI-virtualisiert** (`Gallery/Controls/VirtualizingTilePanel`, Folgearbeit aus 0.2.1): eigenes
  `VirtualizingPanel` mit `IScrollInfo` — feste Kachelgröße 180×160, Spaltenzahl aus der Viewport-Breite,
  pixelbasiertes Scrollen (Mausrad 48 px je Rastung wie bisher, Bild↑/↓ = Viewport, Zeile 16 px), Container nur für die
  sichtbaren Zeilen plus eine Pufferzeile oben/unten über den `ItemContainerGenerator` (Recycling), `BringIndexIntoView`/
  `MakeVisible` für `ScrollIntoView` und Tastaturnavigation, der fokussierte Container bleibt realisiert. Kein
  horizontales Scrollen. `GalleryView.xaml`: `CanContentScroll=True`, `IsVirtualizing=True`, `VirtualizationMode=Recycling`.
- `GalleryListBox` gleicht die Auswahl zwischen Items (ViewModel, maßgeblich) und ListBox auch für Kacheln ohne Container ab:
  Umschalt-Bereich/Strg+A der ListBox erreichen alle Items (`OnSelectionChanged`); vom ViewModel aufgehobene Auswahl wird
  vor der Container-Erzeugung in die ListBox übernommen, damit `Selector.AdjustNewContainers` sie beim Zurückscrollen nicht
  wiederbelebt. Enter-Fallback in der View nimmt das erste ausgewählte Item statt `SelectedItem`.
- Thumbnails sichtbare Kacheln zuerst: das Panel meldet den sichtbaren Index-Bereich (`VisibleRangeChanged`, gebubbelt),
  `GalleryViewModel.ReportVisibleRange`; die Seitenanforderung wartet den Layoutdurchlauf ab und reiht nach Abstand zum
  sichtbaren Bereich ein (Queue des ThumbnailService bleibt FIFO).
- Diagnose-Logzeile „Kachel-Grid sichtbar: n Kacheln, m Container realisiert, x ms nach Seitenänderung“ (`GalleryView`).

### Messung (Debug-Build, i7-8700K)
- Harness `tests/PhotoViewerV3.Wpf.Tests` (ListBox 900×600, 1000 Kacheln, kachelähnliche Vorlage): WrapPanel 0.2.2 →
  Layout 3 267 ms, 1000 Container, 1001 UI-Automation-Elemente; VirtualizingTilePanel → Layout 50 ms, 20 Container,
  21 UI-Automation-Elemente.
- Echte App, Bibliothek mit 1242 Dateien, Seitengröße 1000: Grid nach Seitenänderung sichtbar nach 14–186 ms (20 Container,
  5 Spalten; erste Seite nach App-Start 420–470 ms inkl. erstmaliger Vorlagen), Seitenwechsel ≈ 200 ms (dominiert von den
  ~2000 Einzeländerungen an `CurrentPage` in `ApplyRows`, nicht vom Panel). UI-Automation-Elemente im Hauptfenster 89
  (0.2.1: 569 bei 500 Kacheln), Antwortzeit des UI-Threads mit lauschendem Automation-Client max 9–17 ms.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 50 (+14: `Gallery/VirtualizingTilePanelTests` — realisierte Container ≤ 60, Scrollen,
  ScrollIntoView, Reset, Spaltenumbruch, Mausrad/Seite, sichtbarer Bereich, Insert/Remove/Move/Replace, Auswahl in beide
  Richtungen inkl. Recycling, UI-Automation-Baum < 100, Messung WrapPanel gegen Panel, Thumbnail-Reihenfolge).
  Neue Fixture `Fixtures/GalleryGridHost` (echtes Fenster mit `GalleryListBox`).
- Rauchtest-Szenario: 15/15 OK, 0 WPF-Binding-Fehler.

### Offen
- Seitenwechsel bei 1000 Kacheln ≈ 200 ms durch Einzel-Remove/-Insert in `ApplyRows` (ObservableCollection); Bündelung wäre
  der nächste Schritt, falls spürbar.

## [0.2.3] — 2026-10-19

### Behoben
- **Lightbox zeigte von großen Fotos nur die linke obere Ecke in Fit-Größe:** Das Bildelement lag direkt im
  Bühnen-Grid; WPF schnitt ein Element, das größer als die Bühne ist, erst auf den Layout-Platz zu und
  verkleinerte es dann per RenderTransform. Das Bild liegt jetzt auf einem Canvas (keine Layout-Begrenzung),
  die Bühne schneidet selbst ab. Regressionstest mit 4000×2250-Bild und Treffertest am rechten Rand
  (`tests/PhotoViewerV3.Wpf.Tests/Lightbox/ZoomPanControlTests.cs`). In den Rauchtests waren alle Bilder
  kleiner als das Fenster, deshalb blieb es unentdeckt.
- Galerie-Kacheln melden im UI-Automation-Baum den Dateinamen statt des Typnamens.

## [0.2.2] — 2026-10-19

### Geändert
- `CLAUDE.md`: Regeln „keine Wpf.Ui MessageBox“ und „Galerie-Kacheln als Blatt-Peers“ aus den Befunden von 0.2.1.

## [0.2.1] — 2026-10-19

### Behoben
- **App hing beim Anlegen einer Bibliothek** (Namensabfrage ohne Knöpfe, CPU voll, Speicher wuchs): Die Wpf.Ui
  MessageBox 4.0.2 berechnet ihre Größe in `OnLoaded` selbst und gerät mit umbrechendem Text plus Eingabefeld in
  eine Layout-Schleife. Alle Dialoge (Shell, Galerie, Lightbox) laufen jetzt über ein eigenes `AppDialogWindow`
  (feste Breite, Höhe aus dem Inhalt, Primär/Sekundär/Schließen, Eingabefeld, Detailzeilen, Enter/Esc).
  Regressionstests öffnen den Dialog real (`tests/PhotoViewerV3.Wpf.Tests/Shell/AppDialogWindowTests.cs`).
- **UI-Thread blockierte dauerhaft, sobald ein UI-Automation-Client lauschte** (Visual Studio Live Visual Tree,
  Automationsskripte, Bildschirmleser): WPF aktualisiert dann nach jedem Layout den gesamten Peer-Baum
  (`AutomationPeer.UpdateSubtree`), bei 500 nicht virtualisierten Kacheln mit je rund zehn Elementen und einem
  Layout je eintreffendem Vorschaubild ohne Ende. Gegenmaßnahmen: Kacheln sind im Automation-Baum Blätter
  (`GalleryListBox`/`GalleryListBoxItem`, 569 statt mehrerer tausend Elemente), Vorschaubilder werden gebündelt mit
  Background-Priorität in die Galerie übernommen, Thumbnail-Decoder laufen mit höchstens sechs Threads in
  niedriger Priorität. Messung mit lauschendem Client: Antwortzeit des UI-Threads maximal 10 ms, Vorschaubilder
  einer Bibliothek mit 1558 Fotos nach rund 5 s fertig.
- EF-Core-SQL-Befehle werden nicht mehr auf Information-Niveau protokolliert (vorher tausende Zeilen je Sitzung).

### Offen
- Beim Start aus Visual Studio (Debugger) schienen keine Logzeilen in `%LocalAppData%\PhotoViewerV3\logs` zu
  entstehen — geklärt (0.2.6): die App loggte korrekt; der Prüfprozess lief in einem App-Container, dessen Sicht auf
  `%LocalAppData%` virtualisiert ist, und sah deshalb nur sein eigenes Verzeichnis.
- Galerie ohne UI-Virtualisierung: Erstaufbau von 500 Kacheln kostet im Debug-Build einige Sekunden; eine
  virtualisierende Kachelanordnung bleibt Folgearbeit.

## [0.2.0] — 2026-10-19

Release-Stand nach drei Wellen (Services → Lightbox/Galerie/Bibliothek/Shell → Sortierer → Aufräumen und Doku).
Konsolidiert die Vorabversionen `0.2.0-services` … `0.2.0-sorter`; siehe auch [README.md](README.md) und die neuen
ADRs [0016](docs/adr/0016-formatregister.md)–[0019](docs/adr/0019-incremental-scan.md).

### Hinzugefügt
- **Bibliothek** (Seite „Bibliothek“, `Wpf/Library`, `Services/Library`): Bibliothek per Ordnerdialog anlegen, Fresh-Scan,
  „Aktualisieren“ = Incremental-Scan mit stabilen FileIds (neu / geändert / fehlend / wieder vorhanden; Favoriten und
  Bewertungen bleiben), Missing-Markierung statt Löschen, Mass-Missing-Guard (> 10 000 oder > 20 % → Dialog
  [Erneut prüfen] [Als fehlend markieren] [Abbrechen]), Offline-Dialog ([Erneut prüfen] [Andere Bibliothek wählen]),
  „Neu aufbauen“ (ClearAndRescan nach Bestätigung), aktivieren, umbenennen, entfernen, Fortschrittspanel mit Abbruch
  und Zusammenfassung.
- **Galerie** (`Wpf/Gallery`): echte Vorschaubilder via WIC mit EXIF-Orientierung 1–8 und Alpha-Flattening, FFmpeg für
  Videos und als Fallback für WebP/HEIC/AVIF/RAW ohne Windows-Codec; sechs Sortierungen (Datum, Name, Größe je auf-
  und absteigend), Seitengröße 200/500/1000, FTS-Suche mit Query-Builder (keine Syntaxfehler bei `- : ( ) " *`),
  Filter Alle/Bilder/Videos/Favoriten, Ordnerfilter (direkt/rekursiv) mit Chip, Container-Kacheln für Ordner ohne
  direkte Dateien, Favorit-Herz und 5-Sterne-Bewertung je Kachel, Mehrfachauswahl, schwebende Aktionsleiste
  (Favorit, Bewertung, Verschieben nach…, Papierkorb, Auswahl aufheben) mit Rückgängig, Tastatur (Strg+←/→,
  Strg+Pos1/Ende, F5, Entf, Esc, Strg+A, Enter), Leerzustände, „fehlt“-Badge.
- **Lightbox** (`Wpf/Lightbox`): eigenes FluentWindow mit Auto-Hide-Toolbar; Bilder mit Zoom/Pan (Mausrad am Cursor,
  Ziehen, Doppelklick Fit ↔ 100 %), EXIF-Drehung, Decode im Hintergrund mit Prefetch ±1, Info-Panel; Videos via
  LibVLC mit Transportleiste, Lautstärke/Stumm, Codec/Dauer/Auflösung; Tastatur (←/→, Pos1/Ende, Bild↑/↓ = ±10,
  F/F11, I, Leertaste, M, +/−, 1, 0, Strg+D, Strg+0…5, Entf, Esc); Favorit/Bewertung/Papierkorb, „Im Explorer
  anzeigen“, „Mit Standardprogramm öffnen“.
- **Ordnerbaum-Seite** (`Wpf/Folders`): Baum der aktiven Bibliothek mit Dateizählern, Auswahl → Galerie rechts
  (mit/ohne Unterordner), „Gesamte Bibliothek“; lädt nach Scans und Datei-Operationen gebündelt neu und stellt
  Aufklapp-Zustand und Auswahl wieder her.
- **Einstellungen** (`Wpf/Settings`): Speicherorte (Datenbank, Protokolle, Thumbnail-Cache mit Größe und „Cache
  leeren“), FFmpeg-Status mit winget-Befehl, unterstützte Formate aus dem Formatregister, Farbschema
  Hell/Dunkel/System (Settings-Tabelle `ui.theme`), Version und Build-Datum.
- **Statusleiste** (ADR-0013, vier Indikatoren): Scan-Fortschritt und -Ergebnis (Incremental mit Diff-Zählern,
  „Bestätigung nötig“ beim Mass-Missing-Guard, „Bibliothek offline“), Thumbnail-Warteschlange, Hintergrund-Aktivität,
  Recovery-Hinweis für unvollständige/fehlgeschlagene OperationBatches.
- **Sortieren** (`Wpf/Sorter`, `Services/Sorter`, aus V2 portiert): File Sorter mit Pattern-Engine (Regex, Priorität,
  Capture-Gruppe 0/n, Kleinschreibung, `_unsorted`), Multi-Level (Sub-Level-Patterns auf dem vollen Dateinamen),
  Profile in der Settings-Tabelle (`sorter.patterns`, `sorter.sublevels`, V2-Defaults, Validierung), Import/Export im
  V2-JSON-Format, Gruppentabelle mit editierbaren Zielordnern und Zusammenführen, Vorschau, Ausführung über
  protokollierte Datei-Operationen, „Rückgängig (letzter Lauf)“; Flatten & Move nach V2-Regel (deduplizierte
  Pfad-Ebenen, `_root`, Separator, Dedup-Dateinamen `_1`/`_2` als protokolliertes Rename, optional direkt in die
  Zielwurzel, nur wirklich leere Quellordner löschen) mit Undo.
- **Services**: `FileOperationService` (Papierkorb via `SHFileOperation` ohne Shell-UI und mit Schutz vor stillem
  Hard-Delete auf Netz-/UNC-/Wechseldatenträgern, Move/Rename/Undo, Protokoll in `OperationBatches`/`FileOperations`,
  Folders-Kette, FTS- und Thumbnail-Bereinigung, `FilesChangedMessage`), `CatalogUpdateService` (Favorit/Bewertung
  set-basiert in 500er-Chunks), `LibraryService` (anlegen/aktivieren/umbenennen/leeren/entfernen, Root-Check),
  `IncrementalScanRunner` mit `RootAvailabilityProbe` und `MassMissingGuard`, `MediaFormatRegistry` (ADR-0016),
  `ThumbnailService`-Härtung (Zustand je Dateiversion, `.tmp`-Bereinigung, Retry-Cooldown bei äußeren Ursachen,
  `Invalidate`/`InvalidateAll`), FFmpeg-Wrapper-Fixes (ArgumentList-Quoting, stdout wird geleert, abbrechbares
  WaitForExit, `ffmpeg_timeout`, zweiter Lauf ohne `-ss` bei Clips unter 1 s), `FtsQueryBuilder`,
  Nachrichten-Records in `Services/Messaging` (ADR-0017).
- **Werkzeuge**: `CLAUDE.md` mit Skill- und Code-Profil; Rauchtest-Szenario (`Shell/Smoke`, 15 Schritte mit
  DB-/Dateisystem-Prüfungen und Screenshots) per Umgebungsvariablen `PHOTOVIEWERV3_SMOKE_ROOT/_SHOTS/_EXIT/_CLEANUP`;
  isoliertes AppData per `PHOTOVIEWERV3_APPDATA`; unbehandelte Ausnahmen und WPF-Binding-Fehler (Debug/Rauchtest) im
  Serilog-Log; neues Testprojekt `tests/PhotoViewerV3.Wpf.Tests`.
- **Doku**: `README.md`, ADR-0016 bis ADR-0019, `PLAN.md` auf Stand 0.2.0, Spike-Dokus 02/03 mit Hinweis auf den
  produktiven Code.

### Geändert
- Spike-Code aufgeräumt: `Wpf/Spike02` → `Wpf/Gallery` (Namespace `PhotoViewerV3.Gallery.*`, Registrierung in
  `AddGallery()`), `Wpf/Spike03` entfernt (produktiver Nachfolger `Wpf/Lightbox`).
- `IDialogService` um `ChooseAsync` (Drei-Knopf-Dialog, `DialogChoice`) erweitert; `ILibraryDialogService`,
  `LibraryDialogService` und `AddLibraryModule` entfallen.
- Galerie-Suche nutzt `FtsQueryBuilder.FromUserInput` aus `Data/RawSql` (eine Implementierung im Repo); Suchtext ohne
  suchbaren Inhalt (z. B. `---`) wird mit Hinweis in der Statuszeile ignoriert (Feld geleert) statt als leere Phrase
  gesucht.
- `GalleryViewModel` invalidiert den Thumbnail-Zustand bei `FilesChangedMessage` (Removed/Added: betroffene Ids) und
  `ScanCompletedMessage` (alles — neue Ids nach Neuaufbau, geänderte/wieder vorhandene Dateien auf jeder Page);
  „Cache leeren“ in den Einstellungen wirkt ohne Neustart (`InvalidateAll`, fehlgeschlagene Vorschaubilder werden
  erneut versucht), Dialogtext entsprechend.
- `ScanAbortedMessage.LibraryOffline`; Statusleiste unterscheidet „Scan abgebrochen“, „Bibliothek offline“ und Fehler.
- Rauchtest: der zweite Scan ist „Aktualisieren“ (Incremental ohne Änderung, mit neuer und fehlender Datei, wieder
  vorhanden), der Neuaufbau läuft über `LibraryViewModel.RunSmokeRebuildAsync`; weiterhin 15/15.
- NuGet: transitive Pins `SQLitePCLRaw.*` 2.1.14 und `System.Security.Cryptography.Xml`/`.Pkcs` 10.0.13 — Build ohne
  NU1902/NU1903.
- `ScanService`: fortlaufende Ids über alle Bibliotheken, vollständige Ordnerkette, gedrosselte
  Fortschrittsnachrichten, Rollback bei Abbruch; `ScanResult` mit `Mode`, `Outcome` und Diff-Zählern,
  `IScanService`-Overload mit `ScanOptions`.
- Shell: NavigationView mit `TargetPageType` + DI-PageProvider, `INavigationService`, Theme beim Start;
  `LightboxViewModel` per `ActivatorUtilities` (kein Container-Leak); Galerie-Thumbnail-Callback liest nur einen
  unveränderlichen Id-Schnappschuss; App-Start mit Fang, `OnExit` synchron.
- `CLAUDE.md`: Regel „Seiten-Namespaces dürfen keine Entitätsnamen verdecken“.

### Behoben
- FTS-Index wurde nie befüllt (`INSERT … NOT IN` las aus der External-Content-Tabelle) → `'rebuild'`; Zählung über
  `FilesFts_docsize`.
- Video-Thumbnails scheiterten am Muxer (die `.tmp`-Endung bestimmt kein Format) → `-f image2 -c:v mjpeg`.
- DI wählte für `IMediaFormatRegistry` den Konstruktor mit zwei leeren Listen → Factory-Registrierung; der Scanner fand
  in der App keine Dateien.
- Scanner-Ids kollidierten bei einer zweiten Bibliothek (Start bei 1) → `MAX(Id)`/`sqlite_sequence`.
- Ordnerkette war unvollständig (Zwischenordner fehlten) → Scanner legt die volle Kette an; verwaiste Ordnerzeilen aus
  Altkatalogen hängt `FileOperationService` an nachgetragene Elternordner.
- Namespace-Kollision: `PhotoViewerV3.Library` verdeckte die Entität `Library` → `PhotoViewerV3.Libraries`.
- WIC: Hochkant-Bilder (längere Seite = Zielgröße), EXIF-Orientierung, Alpha auf dunklem Hintergrund, kein
  Hochskalieren; WPF-UI-Icon außerhalb der BMP (U+F02B2) nicht mehr verwendet.
- `FileOperationService`: Katalog-Buchung nach dem Dateisystem-Schritt nicht mehr abbrechbar, Item-Status `Applying`
  vor `File.Move`, Undo findet die Zeile über den aktuellen Pfad (sicher bei Rescan/Id-Wiederverwendung), reservierte
  Gerätenamen abgelehnt, FTS-`delete` mit Rebuild-Fallback; `CatalogUpdateService` sendet Nachrichten auch nach
  Teilfehlern.

### Tests
- `tests/PhotoViewerV3.Tests`: 241 (Scanner inkl. Incremental und Guard, Datei-Operationen und Härtung, Katalog,
  Thumbnails, FFmpeg-Fallback, FTS-Builder gegen echtes FTS5, Sorter/Flatten, Profile).
- `tests/PhotoViewerV3.Wpf.Tests`: 33 (WIC-Renderer, `GalleryQuery`/FTS, Converter, Binding-Listener).
- Rauchtest-Szenario: 15/15 OK, 0 WPF-Binding-Fehler.

## [0.1.6] — 2026-10-18

### Hinzugefügt
- `IMediaFormatRegistry` / `MediaFormatRegistry` (Services/Media): gängige Foto- und Videoformate statt fester jpg/png/mp4-Liste; Scanner klassifiziert darüber.
- Schnittstellen-Verträge für die nächsten Slices: `ILibraryService`, `IFileOperationService`, `ICatalogUpdateService`, Nachrichten-Records in `Services/Messaging` (WeakReferenceMessenger), `ILightboxService` (Wpf).
- `ScanMode.ClearAndRescan` und `ScanMode.Incremental` (Implementierung folgt).
- `IThumbnailService.PendingCount` für die Statusleiste.
- `CLAUDE.md` mit Skill- und Code-Profil, dieses Changelog.

## [0.1.5] — 2026-10-18

### Hinzugefügt
- Thumbnail-Pipeline: `ThumbnailService` mit getrennten Worker-Pools (WIC für Bilder, FFmpeg für Videos), DB-Status-Lifecycle, atomic `.tmp`+Move; echte Vorschaubilder im Gallery-Grid.

## [0.1.4] — 2026-06-30
- Gallery arbeitet auf dem produktiven `LibraryContext` statt der Spike-DB.

## [0.1.3] — 2026-05-11
- Doku: Real-Welt-Scan-Validation (770 Dateien, 17 GB).

## [0.1.2] — 2026-05-11
- Fix: FTS-Count via SELECT; CLI-Test `spike-v01-scan`.

## [0.1.1] — 2026-05-11
- App-Start nutzt `AddPhotoViewerServices` + `DatabaseInitializer`.

## [0.1.0] — 2026-05-11
- Scanner-Slice: `FolderEnumerator` + `BulkScanWriter` + `FtsIndexer`.

## [0.0.8] — 2026-05-11
- Entities, `LibraryContext`, Migration `InitialV01` (V0.1-Schema).

## [0.0.2] – [0.0.7] — 2026-05-11
- Sechs Spikes grün: SQLite-Bulk/FTS5 (500k), Virtual Grid, LibVLCSharp-Lightbox, WIC + File-Locks, FFmpeg-Cancel, Storage-Detection.

## [0.0.1] — 2026-05-10
- Initial: Plan, 15 ADRs, Mockups, Tag-1-Skeleton.

## Offen
- SelectionStore über Seitengrenzen hinweg (ADR-0010: Auswahl nach FileId über Pages; heute nur innerhalb der aktuellen Page).
- Index für die Namenssortierung (`Files(LibraryId, Name COLLATE NOCASE)`) und COLLATE-NOCASE-Indizes für den Incremental-Abgleich (Ordnerpfad, Name) — bei 500k messen (Spike-Harness).
- Recovery-Dialog für unfertige OperationBatches beim Start (ADR-0012: Fortsetzen / Reparieren) — heute nur Hinweis in der Statusleiste und Undo.
- Thumbnail-Queue mit vier Prioritäten und Background-Batching für 500k (ADR-0007; heute zwei Worker-Pools mit Dedupe) sowie Storage-Profile (ADR-0008) produktiv anbinden.
- Tags/Alben (V0.4), Plan-Ordner (V0.3), Gesichtserkennung (V0.5, ADR-0015), Animated Hover (ADR-0014), Archive Auto-Extract, pHash-Duplikate.
- Portable Auslieferung per `dotnet publish` (PLAN Kap. 1) und Migration-Backup-Dialog (PLAN Kap. 14).
- Umbenannte/verschobene Dateien erkennt der Incremental-Scan als „neu + fehlend“ (keine Hash-Identität, ADR-0019).

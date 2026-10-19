# Changelog

Alle nennenswerten Änderungen an PhotoViewer V3. Format: `[Version] — Datum`, oberster Eintrag = aktuelle Version
(Versionsquelle für das Commit-Profil in `CLAUDE.md`).

## [0.2.14] — 2026-10-19

### Behoben (Ordnerüberwachung härten, Befunde aus dem Code-Review)
- **Nie zwei Scans derselben Bibliothek gleichzeitig** (Befund 1, bestätigt): `ScanService` hatte keine Sperre;
  „Aktualisieren“/„Neu aufbauen“, Entpacken und der automatische Scan konnten parallel laufen (die Überwachung erkannte
  fremde Scans erst an deren erster `ScanProgressMessage`, die Bibliotheksseite gar nichts). Neu `Services/Scanning/ScanGate`
  (`IScanGate`, ein `SemaphoreSlim` je Bibliothek, Singleton): `ScanService` belegt die Bibliothek vor dem ersten
  Dateisystem-Zugriff, der spätere Scan wartet (abbrechbar; auch ein beim Warten abgebrochener Aufruf sendet seine
  `ScanAbortedMessage`). Die Überwachung hört auf `IScanGate.Acquired`: ein fremder Scan gilt ab Belegung als laufend,
  und die bis dahin gesammelten Ereignisse sind durch ihn abgedeckt (zusammengelegt statt zweimal gescannt); endet er
  nicht erfolgreich, gelten sie wieder.
- **Bibliothekswechsel während eines automatischen Scans** (Befund 2, bestätigt): kam der Abbruch zu spät, wurde das
  Ergebnis der vorigen Bibliothek der neuen zugeordnet — Sperre „angehalten“ bzw. „Automatisch aktualisiert: …“ mit
  falscher Bibliothek. Jetzt Epoche je Bibliothekswechsel; Ergebnis und Sperre eines veralteten Scans werden verworfen,
  die neue Bibliothek zeigt nicht „läuft…“ für den fremden Scan.
- **Ordner mit Punkt im Namen** (Befund 3, bestätigt): `WatchPathFilter` hielt `2024.06 Urlaub` für eine Datei mit
  Endung „.06 Urlaub“ und verwarf Anlegen/Hineinverschieben/Umbenennen/Löschen. Jetzt zählt jeder existierende Ordner;
  gelöschte Pfade gelten als Ordner, wenn ihre „Endung“ keine Dateiendung sein kann (Leerzeichen, nur Ziffern, > 8
  Zeichen). Restlücke dokumentiert (gelöschter Ordner `Fotos.alt`).
- **Speicherreihenfolge der Einstellungen** (Befund 4, bestätigt): `SetQuietPeriodAsync`/`SetEnabledAsync` speicherten
  ohne Reihenfolge; die NumberBox „Ruhezeit“ (UpdateSourceTrigger=PropertyChanged, Aufruf ohne await) konnte einen
  älteren Wert zuletzt schreiben. Jetzt nacheinander (`SemaphoreSlim`), und jeder Speichervorgang schreibt den aktuellen
  Stand statt seines Aufrufwerts.
- **Flut von Statusnachrichten** (Befund 5, bestätigt): jedes relevante Dateiereignis schickte eine
  `WatchStatusChangedMessage` (beim Kopieren Tausender Dateien Tausende Dispatcher-Aufträge mit Normal-Priorität).
  Jetzt gehen reine Countdown-Verschiebungen höchstens alle 500 ms hinaus (die letzte nachlaufend), Zustandswechsel
  sofort; gesendet wird immer der jüngste Zustand, nie ein älterer nach einem jüngeren. Die Statusleiste bündelt
  zusätzlich (höchstens ein ausstehender Auftrag, Background-Priorität).

### Geprüft, nicht umgesetzt
- Eigene Datei-Operationen (Verschieben, „Ins Archiv…“, Papierkorb) lösen hinterher weiterhin einen (entprellten)
  automatischen Scan aus, der „keine Änderungen“ findet. Sauber unterdrücken ließe sich das nur in
  `FileOperationService` (Pfade der laufenden `OperationBatch` melden bzw. die Bibliothek für den Batch belegen); ein
  pauschales Ignorieren während eines Batches verlöre echte externe Änderungen und Teilfehler (Kopie am Ziel, Quelle
  nicht gelöscht). Entpacken profitiert bereits vom Zusammenlegen (es scannt am Ende selbst).

### Tests
- `tests/PhotoViewerV3.Tests`: 366 (+22): `Watch/ScanGateTests` (3, echter ScanService: zweiter Scan wartet, Abbruch
  beim Warten), `Watch/LibraryWatchServiceTests.Hardening` (8: fremder Scan vor seiner ersten Fortschrittsmeldung,
  Zusammenlegen, Abbruch gibt Ereignisse zurück, Bibliothekswechsel ×2, Ordner „2024.06 Urlaub“ hinein/hinaus,
  Speicherreihenfolge, Nachrichtenrate), `Watch/WatchPathFilterTests` (+11). Jeder Befund-Test scheitert mit dem alten
  Verhalten (gegengeprüft durch gezieltes Zurückbauen). `tests/PhotoViewerV3.Wpf.Tests`: 121.
- Rauchtest 19/19 OK, 0 WPF-Binding-Fehler.

### Offen
- Scan und Datei-Operation derselben Bibliothek sind nicht gegeneinander gesperrt (Single-Writer gilt nur je Dienst): ein
  Scan, der den Katalog liest, während ein Batch Dateien verschiebt, kann eine verschobene Datei als „fehlt“ + „neu“
  sehen. Kandidat: `FileOperationService` belegt für die Dauer eines Batches dasselbe `IScanGate`.
- Ein wartender manueller Scan zeigt auf der Bibliotheksseite bis zum Start „Ordner werden durchsucht…“ ohne Hinweis aufs
  Warten.

## [0.2.13] — 2026-10-19

### Geändert
- **Galerie-Kopfzeile** als eigenes Control `Gallery/Controls/GalleryToolbar` (vorher direkt in `GalleryView.xaml`),
  sauber von 1024 bis 2560 px Fensterbreite und in der schmalen rechten Hälfte der Ordner-Seite:
  - Typ-Filter als Segmentgruppe im Fluent-Stil (Alle | Bilder | Videos | Favoriten | Archive, mit Symbolen wie im
    Mockup), Status-Filter **Nicht archiviert / Archiviert** im neuen Dropdown **Status**
    (`GalleryStatusFilterDropDown`, aktiver Wert in Akzentfarbe). Bindungen und Commands unverändert (`Filter` über
    `EnumEquals`, Werte `GalleryFilter.*`).
  - Neues Layout-Panel `ToolbarFlowPanel`: bricht um statt überzulaufen; die Suche zählt für den Umbruch mit ihrer
    Mindestbreite (360 px) und wächst bis 640 px — passt sie nicht mehr neben die Filter, rutscht sie in eine eigene
    Zeile. Zweite Zeile: Sortierung, Seitengröße links; Kachel/Liste, Rückgängig, Aktualisieren rechtsbündig.
  - Suchfeld: Platzhalter „Name, Endung oder Tag…“ (die Suche findet seit 0.2.10 auch Tag-Namen), „Leeren“ als
    Symbolknopf ✕; der eingebaute Leeren-Knopf des Eingabefelds ist aus (er leerte nur den Text, ohne neu zu laden).
- Neue Basisklasse `Shell/Dialogs/AppFluentWindow` für Lightbox und `AppDialogWindow`.

### Behoben
- Erledigt (offener Punkt aus 0.2.11): Wurde ein Fenster mit WPF-UI-TitleBar nach `Loaded`, aber vor dem ersten
  Zeichnen geschlossen (Rauchtest: Lightbox öffnen und binnen ~100 ms schließen), warf
  `TitleBar.OnWindowContentRendered` „Hwnd mit dem Wert Null ist ungültig“ in den globalen Handler (FTL im Log).
  Ursache: WPF stellt `ContentRendered` mit Input-Priorität ein und bricht es beim Schließen nicht ab.
  `AppFluentWindow.OnContentRendered` verwirft das Ereignis eines geschlossenen Fensters — zentral für Lightbox und
  `AppDialogWindow`, ohne Verzögerung des Schließens und ohne Reflection.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 134 (+13) — `Shell/EarlyCloseWindowTests` (Kontrolle: reines FluentWindow mit
  TitleBar wirft weiterhin; AppFluentWindow, echte Lightbox und modaler AppDialogWindow schließen vor dem Zeichnen
  ohne Ausnahme), `Gallery/GalleryToolbarLayoutTests` (echtes `GalleryToolbar` bei 1024/1280/1920/2560 px, mit
  Ordner-Chip + aktivem Status, Ordner-Seite: nichts ragt über den Rand, Suche ≥ Mindestbreite, Platzhalter passt,
  Zeilenzahl; `ToolbarFlowPanel` direkt). `tests/PhotoViewerV3.Tests`: 344.
- Belege: `docs/spikes/07-galerie-kopfzeile.md` mit Bildern bei 1024, 1280 (auch mit Ordner-Chip) und 1920 px.
- Rauchtest: 19/19 OK, 0 WPF-Binding-Fehler, kein FTL/„Hwnd“ mehr im Log (Lightbox öffnen/schließen). Ein Lauf davor
  scheiterte einmalig an „Thumbnails 0/42“ (Thumbnail-Dienst startete erst nach der Prüfung, ohne Bezug zu dieser
  Änderung); Wiederholung grün.

### Offen
- Weitere FluentWindows mit TitleBar (`MainWindow`, `ArchiveTargetDialog`, `ExtractArchiveDialog`, `TagPickerDialog`)
  erben noch von `FluentWindow`; sie werden nur durch Benutzeraktion nach dem Zeichnen geschlossen. Umstellen = eine
  Zeile je XAML/Code-Behind auf `AppFluentWindow`.

## [0.2.12] — 2026-10-19

### Behoben
- **„Neu aufbauen“ übernimmt Favoriten, Bewertungen und Tags** (ADR-0023): `Services/Scanning/UserDataCarryOver`
  sichert vor dem Leeren alle Dateien mit Favorit, Bewertung oder Tag in TEMP-Tabellen einer eigenen Verbindung und
  schreibt sie nach dem Bulk-Insert (vor dem FTS-Aufbau, eine Schreibtransaktion) auf die neuen FileIds zurück —
  mengenbasiert per INSERT…SELECT / UPDATE…FROM. Schlüssel wie beim Incremental-Scan (ADR-0019): voller Ordnerpfad +
  Dateiname ohne Groß-/Kleinschreibung (Unicode), damit über alle Wurzeln eindeutig; keine Prüfung von
  Größe/Änderungszeit (eine nachbearbeitete Datei behält ihre Daten wie beim Aktualisieren). Dateien, die nach dem
  Neuscan nicht mehr am selben Pfad liegen, verlieren ihre Daten; die Tabelle `Tags` bleibt unberührt, Thumbnails und
  Suchindex werden wie bisher neu aufgebaut. Ein Fehler beim Zurückschreiben rollt den Scan wie jeder Persistenzfehler
  zurück. `ScanService` ruft die Klasse mit wenigen Zeilen im ClearAndRescan-Pfad auf und protokolliert beide Schritte.
- Bestätigungsdialog und Tooltip „Neu aufbauen“ (Bibliotheksseite) nennen jetzt, was erhalten bleibt.
- ADR-0019 (Punkt „Neu aufbauen“) und ADR-0021 (Konsequenz „Tags gehen verloren“) mit Statusvermerk auf ADR-0023.

### Messung
- Synthetischer Katalog, schlechtester Fall (jede Datei mit Bewertung, jede zweite Favorit, jede zehnte zwei Tags,
  jede hundertste in anderer Schreibweise zurück): **500 000 Dateien — Sichern 1,9 s, Zurückschreiben 2,6–3,9 s**;
  200 000 Dateien — 0,8 s / 1,0 s (`PV3_CARRYOVER_ROWS=500000 dotnet test … --filter Mengen_Messung`). Ein Filter auf
  `Files.LibraryId` in der Zuordnung lenkte den Planer auf `IX_Files_Library_Folder` (7,6 s bei 200k) — entfernt.

### Tests
- `tests/PhotoViewerV3.Tests`: +5 `Scanning/ClearAndRescanUserDataTests` (zwei Wurzeln + gleichnamige Dateien in
  verschiedenen Ordnern, entfernte Datei verliert ihre Daten und Tags bleiben, Umbenennung nur in
  Groß-/Kleinschreibung, FTS-/Thumbnail-Konsistenz, Mengen-Messung mit 20 000 Dateien).
- Rauchtest, Schritt „Aktualisieren + Neu aufbauen“: prüft, dass Favoriten und Bewertungen (Pfad + Werte) nach
  ClearAndRescan identisch sind (statt bisher „Favoriten verworfen“).

### Offen
- Abbruch während der Enumeration von „Neu aufbauen“ hinterlässt wie bisher einen leeren Katalog — jetzt samt
  verlorener Benutzerdaten (die Sicherung lebt nur in der Verbindung). Besser: erst nach der Enumeration sichern und
  leeren (Start-Ids vorher lesen); betrifft die Reihenfolge in `ScanService.RunFullScanAsync`.

## [0.2.11] — 2026-10-19

### Geändert
- Listenansicht, Spalte „Status“: zeigt jetzt „fehlt“, „Eingang“ oder „Archiv“ (Rolle der Wurzel, ADR-0020) statt nur
  „fehlt“; sortierbar. `GalleryItem.IsArchived`/`StatusText`, Platzhalter-Konverter entfernt.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 121 (+7 `ListView/GalleryItemStatusTests`); `tests/PhotoViewerV3.Tests`: 344.
- Rauchtest des integrierten Stands (0.2.7–0.2.10): 19/19 OK, 0 WPF-Binding-Fehler.

### Offen
- WPF-UI 4: Wird ein Fenster mit TitleBar geschlossen, bevor es einmal gezeichnet wurde (Rauchtest öffnet und schließt
  die Lightbox binnen ~100 ms), wirft `TitleBar.OnWindowContentRendered` „Hwnd mit dem Wert Null ist ungültig“. Der
  globale Handler fängt das ab, die App läuft weiter; Abhilfe wäre, das Schließen bis `ContentRendered` zu verzögern.
- Rauchtest-Startskript: robocopy kopierte im App-Container der Claude-Desktop-App teils 0 Dateien — dort Copy-Item nutzen.

## [0.2.10] — 2026-10-19

### Neu
- **Tags** (ADR-0021): frei konfigurierbare Schlagworte in eigenen Tabellen `Tags` (Name eindeutig ohne
  Groß-/Kleinschreibung, Farbe aus fester 12er-Palette, Reihenfolge) und `FileTags` (FileId + TagId, Index auf TagId,
  beide Fremdschlüssel ON DELETE CASCADE — Papierkorb, „Neu aufbauen“ und Tag löschen räumen die Zuweisungen in der DB
  mit auf). Migration `AddTags` (strikt, Bestandsdaten unverändert).
- `Services/Tags`: `ITagService` (anlegen, umbenennen, färben, sortieren, löschen; zuweisen/entfernen für Id-Listen in
  Chunks zu 500; Tags je Datei als Batch für eine Seite; Zähler je Tag; `RemoveForFilesAsync` als Hilfsmethode),
  `TagsChangedMessage` für Stammdaten, `FilesChangedMessage(Metadata)` nach Zuweisungen. Registrierung `AddTags()`.
- Galerie: Knopf **Tags** in der Aktionsleiste und Kontextmenü auf Kacheln/Zeilen (Häkchen = alle ausgewählten Dateien
  haben das Tag, „(teilweise)“ = ein Teil; Rechtsklick auf eine nicht ausgewählte Kachel wählt nur sie), **Strg+T**
  öffnet die Tag-Auswahl (`Gallery/Tags/TagPickerDialog`, dreistufige Häkchen, „Neues Tag…“), Tag-Punkte auf der Kachel
  (Hover = Namen), Dropdown **Tags** neben den Filter-Pills (ODER / UND / „nur Dateien ohne Tag“, mit Zählern). Die
  Suche findet zusätzlich Dateien, deren Tag-Name den Suchtext enthält (FTS-Treffer UNION Tag-Treffer, per
  `CROSS JOIN` an Files gebunden — die Treffer treiben die Abfrage wie bisher die FTS-Tabelle; keine FTS-Spalte).
- Lightbox: Tags der Datei in der Titelleiste, **Strg+T** bearbeitet sie (eigene Datei `LightboxViewModel.Tags.cs`).
- Seite **Tags** in der Navigation (`Settings/TagManagement`): Liste mit Zählern, Neu, Umbenennen, Farbe aus der
  Palette, Reihenfolge, Löschen mit Bestätigung und Zahl der betroffenen Dateien.
- **Listenansicht** (`Gallery/ListView`): Umschalter Kachel-Grid/Liste in der Kopfzeile (gespeichert als
  `ui.gallery.viewMode`), DataGrid über dieselbe Seite mit den Spalten Name (mit Vorschaubild), Ordner, Geändert,
  Größe, Typ, Bewertung, Favorit, Tags und Status (Platzhalter, zeigt bislang nur „fehlt“). Spalten über das
  Kopfzeilen-Kontextmenü ein-/ausblendbar und per Ziehen umsortierbar (`ui.gallery.listColumns`), Klick auf
  Name/Geändert/Größe sortiert die ganze Trefferliste über die Galerie-Sortierung, die übrigen Spalten nur die geladene
  Seite. Mehrfachauswahl, Doppelklick/Enter → Lightbox, Entf, Esc, Strg+A, Strg+T und Aktionsleiste wie im Grid;
  Zeilen-Virtualisierung mit Recycling; im UI-Automation-Baum sind nur realisierte Zeilen sichtbar, als Blätter
  (`GalleryDataGrid`/`GalleryDataGridRow`, eigene Peers statt `DataGridAutomationPeer`). Die Liste wird erst beim
  ersten Umschalten erzeugt — der Start im Kachel-Modus bleibt unverändert schnell.

### Integration
- Zusammengeführt mit 0.2.7–0.2.9. Migration `AddTags` neu erzeugt (`20261019105649_AddTags`, inhaltlich unverändert),
  damit sie nach `AddLibraryRoots` läuft; ModelSnapshot neu.
- Ein gemeinsames Kontextmenü für Kachel und Listenzeile (im Code-Behind gebaut): Archiv „Inhalt anzeigen“/„Entpacken…“,
  „Ins Archiv…“, „Im Explorer anzeigen“, danach das Tag-Menü. Behebt in 0.2.9 das doppelte ContextMenu-Setter-Paar,
  bei dem das Archiv-Menü „Ins Archiv…“ überdeckte.
- Rauchtest-Reihenfolge: … → Archiv (ZIP) → Eingang → Archiv → Tags + Listenansicht → Ordnerüberwachung.

### Tests
- `tests/PhotoViewerV3.Tests`: 256 (+15 `Tags/TagServiceTests` — Anlegen/Validierung, Duplikate ohne
  Groß-/Kleinschreibung, Nachrichten, Zuweisen > 500 in Chunks, Löschen, ON DELETE CASCADE beim Entfernen der Datei und
  beim Papierkorb, leere Listen, Empfänger-Ausnahmen, Palette).
- `tests/PhotoViewerV3.Wpf.Tests`: 97 (+46 — `ListView/GalleryListColumnsTests` Spalten-/Sortier-Logik und Setting,
  `ListView/GalleryDataGridAutomationTests` Blatt-Peers, `Tags/TagPickerLogicTests`, `Tags/GalleryPageSourceTagTests`
  Filter ODER/UND/ohne Tag und Tag-Suche gegen eine migrierte DB, `Tags/TagManagementViewModelTests`).
- Rauchtest-Szenario: neuer Schritt „Tags (Zuweisen, Filter, Suche, Verwaltung) + Listenansicht“ (Tag-Menü, Strg+T
  über einen Rauchtest-Ersatz des Dialogs, Filter, Suche, Listenansicht mit Setting/Spalte/Sortierung, Lightbox-Tags,
  Verwaltungsseite, Umbenennen, Löschen); 16/16 OK, 0 WPF-Binding-Fehler.

### Offen
- Spalte „Status“ der Listenansicht ist ein Platzhalter (TODO in `ListViewConverters.cs`/`GalleryListView.xaml`): den
  Eingangs-/Archiv-Status liefert das Bibliotheksmodul.
- Tag-Zuweisungen hängen an FileIds: „Neu aufbauen“ verliert sie wie Favoriten und Bewertungen.

## [0.2.9] — 2026-10-19

### Neu
- **Eingang und Archiv in einer Bibliothek** (ADR-0020): Tabelle `LibraryRoots` (Id, LibraryId, Path, Role, Name,
  SortOrder) mit genau einer Archiv-Wurzel und beliebig vielen Eingangs-Wurzeln, `Folders.RootId`. Migration
  `AddLibraryRoots` übernimmt jede `Libraries.RootPath` als Archiv-Wurzel und setzt `RootId` bestehender Ordner per
  Pfad-Präfix; `RootPath` bleibt als Anzeigefeld der Archiv-Wurzel. `LibraryInfo.Roots` (+ `ArchiveRoot`,
  `InboxRoots`, `HasOfflineRoot`), `ILibraryService.GetRootsAsync/AddRootAsync/RenameRootAsync/SetRootRoleAsync/
  RemoveRootAsync` (Entfernen nur aus dem Katalog), `LibraryRootsChangedMessage`.
- Scanner über alle Wurzeln: Fresh/ClearAndRescan nur, wenn alle Wurzeln erreichbar sind; Incremental je Wurzel mit
  eigenem Offline- und Mass-Missing-Guard (Offline-Wurzel wird übersprungen, Ergebnis `LibraryOffline` mit ihrem Pfad),
  Ordnerkette je Wurzel, `ScanResult.RootsScanned`.
- `FileOperationService`: „innerhalb der Bibliothek“ = unter irgendeiner Wurzel; Zielordner-Kette unter der passenden
  Wurzel, Undo entsprechend. Neu `CreateFolderAsync` (MkDir-Batch) und `RenameFolderAsync` (RenameFolder-Batch: Ordner-
  plus je Datei eine Operation, `Directory.Move`, Folders-Pfade samt Unterordnern, rückgängig machbar; Wurzeln nicht),
  `FoldersChangedMessage`.
- Bibliotheksseite: Wurzeln je Bibliothek mit Rolle und Erreichbarkeit, „Eingang hinzufügen…“ (mit Angebot zu
  aktualisieren), zur Archiv-Wurzel machen, Anzeigename ändern, Eingang entfernen (nur Katalog).
- Galerie: Filter-Pills „Nicht archiviert“ / „Archiviert“ (SQL über `Folders.RootId → LibraryRoots.Role`, Count-Query
  mitgezogen), Badge „Eingang“ auf der Kachel, Aktion **Ins Archiv…** (Aktionsleiste, Strg+M, Kontextmenü der Kachel,
  Lightbox Strg+M bzw. Overlay-Knopf über `IArchiveMoveService`). Dialog `Gallery/ArchiveTarget/ArchiveTargetDialog`: Ordnerbaum
  der Archiv-Wurzel (Katalog- und Dateisystem-Ordner), Suche, Neuer Ordner, Umbenennen, Vorschlag „Jahr\Jahr-Monat“ aus
  EXIF-Aufnahme- bzw. Dateidatum, zuletzt benutzter Zielordner vorgemerkt (Setting `archive.lastTarget.{libraryId}`).
  Die Ansicht bleibt nach dem Verschieben beim Filter.
- Ordnerbaum: Wurzeln als oberste Knoten mit Rolle (Archiv zuerst), Kontextmenü „Neuer Ordner…“ / „Umbenennen…“.
- Rauchtest: Schritt „Eingang → Archiv“ (zweite Wurzel, Incremental über 2 Wurzeln, Status-Filter + Badge, Ins Archiv mit
  Fake-Zieldialog, Rückgängig, Ordner anlegen/umbenennen, Eingang entfernen) — 16 OK.

### Integration
- Zusammengeführt mit Ordnerüberwachung (0.2.7) und Archiven (0.2.8): Der Dienst für „Ins Archiv…“ heißt
  `IArchiveMoveService`/`ArchiveMoveService` (Ordner `Wpf/Gallery/ArchiveTarget`), damit er nicht mit dem
  ZIP/RAR-Dienst `Services/Archive/IArchiveService` kollidiert; Filter-Pills Alle / Bilder / Videos / Favoriten /
  Archive / Nicht archiviert / Archiviert.
- Ordnerüberwachung beobachtet jetzt alle Wurzeln der Bibliothek (`LibraryWatchService.GetRootPaths` über
  `LibraryInfo.Roots`) und baut ihre Watcher bei `LibraryRootsChangedMessage` neu auf; Tests `WatchRootPathsTests`.
- Rauchtest-Reihenfolge: … → Archiv (ZIP) → Eingang → Archiv → Ordnerüberwachung (Überwachung bleibt bis zum
  letzten Schritt aus).

### Tests
- `PhotoViewerV3.Tests`: Migration auf einer Alt-DB, Wurzelverwaltung, Scanner über zwei Wurzeln (Fresh, Incremental,
  Offline-Wurzel), Move Eingang → Archiv mit Undo, Ordner anlegen/umbenennen (257 Tests).
- `PhotoViewerV3.Wpf.Tests`: Status-Filter im `GalleryPageSource` (auch mit Suche/Ordnerfilter), Archiv-Dialog-ViewModel
  (Baum, Vorauswahl, Suche, Vorschlag, Neuer Ordner, Umbenennen), Datumsvorschlag, echtes Dialogfenster (63 Tests).

### Offen
- Sortieren/Flatten arbeiten weiter nur unter der Archiv-Wurzel (`Libraries.RootPath`); Eingänge als Quelle folgen.
- Dateisystem-Ordner ohne Katalogzeile lassen sich im Archiv-Dialog anlegen/auswählen, aber nicht umbenennen.

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

# Changelog

Alle nennenswerten Änderungen an PhotoViewer V3. Format: `[Version] — Datum`, oberster Eintrag = aktuelle Version
(Versionsquelle für das Commit-Profil in `CLAUDE.md`).

## [0.2.30] — 2026-10-20

### Geändert (ADR-0031, offene Punkte aus 0.2.23)
- **Kategorie-Sortierung segmentweise**: statt einer Sortierung aller Dateien mit je einem Schlüssel-Lookup zählt die
  Galerie die Dateien je Wert und liest die Seite nur aus dem Wert-Segment, in das sie fällt (Reihenfolge des
  Datums-Index, `f.Id IN (Dateien des Werts)`, Reverse-Trick vom Segmentende, Spalten erst für die Zeilen der Seite).
  Kleine Wert-Segmente (≤ 4 000 Dateien, ohne Suche/Filter) lesen ihre Zuordnungen direkt. Zählung und Seite laufen in
  einer Lese-Transaktion (ein Snapshot); die Zählung bleibt beim Blättern gültig, bis eine andere Verbindung schreibt
  (`PRAGMA data_version`), die Gesamtzahl aus `GetTotalCountAsync` wird übernommen statt doppelt gezählt. Reihenfolge
  unverändert (ohne Wert zuletzt, Werte-Reihenfolge der Verwaltung, gleiche Werte nach Datum neu → alt, dann Id).
  500 000 Dateien: Seite 1 / Mitte / Seite 801 von 7,8–8,4 s auf 0,26–0,61 s beim ersten Laden nach einer Änderung und
  0,03–0,26 s beim Blättern (Spike #08, `docs/spikes/08-suche-sortierung.md`).
- **Neue Migration `AddCategorySortIndex`**: abdeckender Index `IX_FileCategoryValues_Category_Value_File (CategoryId,
  ValueId, FileId)` ersetzt `IX_FileCategoryValues_Category`; bestehende Daten bleiben unverändert.
- **Tag-Zweig der Suche** in derselben `IN`-Form wie die Kategorie-Werte: `FileTags.TagId IN (passende Tags)` über
  `IX_FileTags_Tag` statt Join über alle Zuordnungen (Plan vorher `SCAN ft`, nachher `SEARCH ft USING INDEX
  IX_FileTags_Tag (TagId=?)`). 500 000 Dateien mit 1,1 Mio. Tag-Zuweisungen: seltener Tag 196 → 42 ms (Zählen wie
  Seite); bei einem Tag mit 100 000 Treffern dominieren die Treffer selbst (≈ 0,15–0,35 s, vorher wie nachher).

### Neu
- **Suche findet Kategorie-Namen**: trifft der Suchtext den Namen einer Kategorie (Teilstring, ohne Groß-/Kleinschreibung),
  erscheinen alle Dateien mit irgendeinem Wert dieser Kategorie — gleiche Mechanik wie Tags/Werte (`CategoryId IN
  (passende Kategorien)` über den neuen Index, keine Index-Pflege, Umbenennen wirkt sofort).

### Tests
- `tests/PhotoViewerV3.Tests`: 460 (+1 `Categories/CategorySortIndexMigrationTests`) (DB auf Stand `AddCategories` mit
  Zuweisungen → migriert, neuer Index da, alter weg, alle Zuweisungen erhalten, `foreign_key_check` leer).
- `tests/PhotoViewerV3.Wpf.Tests`: 234 (+32): `Categories/GalleryCategorySortPagingTests` (neu: Blättern mit 8 Seitengrößen auf/ab,
  mit Filter und Suche gleich der Reihenfolge der alten Ein-Abfrage-Sortierung, zwei Bibliotheken, Zuweisung zwischen
  zwei Seiten macht die zwischengespeicherte Zählung ungültig — Gegenprobe ohne `data_version` rot, neue Datei nach
  dem Zählen erscheint ohne erneutes Zählen — Gegenprobe mit blind übernommener Gesamtzahl rot, leere Werte, Seite
  hinter dem Ende), `GalleryPageSourceCategoryTests` (Kategorie-Namen-Suche, Umbenennen, mit Filter/Sortierung, Plan
  über den neuen Index; Plan-Test der Sortierung auf die Segment-Form umgestellt), `GalleryPageSourceTagTests` (Plan
  `IX_FileTags_Tag` ohne `SCAN ft`, Umbenennen/Zuweisen), `Gallery/GallerySearchSortMeasurementTests` (Messung,
  Größe über `PV3_SEARCHSORT_FILES`, Standard 3 000). Wpf-Suite 3× hintereinander grün, Build ohne Warnungen.
- Rauchtest 22/22 OK, 0 WPF-Binding-Fehler, kein ERR/FTL im Log (Schritt „Kategorien“ deckt Suche und Sortierung ab;
  kein neuer Schritt).

### Offen
- Grundsortierungen Datum/Name/Größe lesen je übersprungener Zeile die ganze Files-Zeile; im synthetischen
  500k-Katalog mit quer zur Id verteiltem Datum kostet die mittlere Seite 2,4 s (Datum) bzw. 6,8 s (Name). Abhilfe wie
  hier: verzögerter Join (innen nur Id + Sortierspalten), für Name/Größe mit eigenem Index (Spike #08).
- Eine Suche, die sehr viele Dateien trifft (z. B. Kategorie-Name mit 250 000 Dateien), materialisiert alle Treffer
  (≈ 0,3–0,4 s bei 500k); ein kurzer Suchtext trifft als Teilstring jede Kategorie, deren Name ihn enthält.
- Der Platzhalter des Suchfelds („Name, Endung oder Tag…“) nennt Kategorien nicht (Gallery-View, nicht angefasst).

## [0.2.29] — 2026-10-20

### Behoben (ADR-0030, offener Punkt aus 0.2.24)
- **Keine verlorene Benutzeraktion während „Neu aufbauen“.** Die eine Schreibtransaktion am Ende (500 000 Dateien ≈ 30 s)
  ließ andere Schreiber scheitern — gemessen nach **34 s** (busy_timeout 5 s + interne Wiederholung von
  Microsoft.Data.Sqlite bis CommandTimeout 30 s), und weil Microsoft.Data.Sqlite auch in `*Async` synchron arbeitet, war
  das Fenster bei einer Bewertung so lange **eingefroren**. Jetzt wartet jeder Schreiber asynchron:
  - `Services/Database/SqliteBusyRetry` (zentral): Versuch auf dem Thread-Pool, nach SQLITE_BUSY/LOCKED asynchron
    warten und wiederholen bis `MaxWait` (5 min); danach kommt der Fehler wie bisher beim Aufrufer an.
  - `BusyRetryInterceptor` an jedem EF-Kontext der `LibraryContextFactory`: Autocommit-Schreibbefehle und
    Transaktionsbeginn laufen darüber — Einstellungen, Tag-/Kategorie-Stammdaten, Thumbnail-Status und
    Datei-Operationen ohne eigene Zeile.
  - Favorit/Bewertung, Tag- und Kategorie-Zuweisungen über `Services/Catalog/CatalogFileTargets`: Ordnerpfad + Name der
    Ziele vorher lesen, `BEGIN IMMEDIATE` mit Wiederholung, danach **fehlende FileIds auf die neuen Ids des Neuaufbaus
    umsetzen** — sonst landete die wartende Bewertung nach dem Commit auf einer Id, die es nicht mehr gibt (still
    verloren; im Test mit Wiederholung allein rot). `CatalogUpdateService` schreibt dafür Raw-SQL in einer Transaktion
    statt `ExecuteUpdateAsync` je Chunk; die Nachricht kommt nach dem Commit mit den tatsächlich geschriebenen Ids.
  - Statusleiste: „Änderung wartet auf den Katalog…“ (bzw. „n Änderungen warten…“, Uhr-Symbol, Tooltip) solange ein
    Schreiber wartet (`CatalogWriteWaitMessage`).
- **Bibliotheksseite erkennt ihr eigenes Warten an einer Kennung** (offener Punkt aus 0.2.22, ADR-0025 Nachtrag):
  `IScanGate.AcquireAsync(libraryId, callerId, ct)`, `ScanGateWait.CallerId`, `ScanOptions.GateCallerId`. Die Seite
  vergibt je Scan-Lauf eine Guid und zeigt nur dieses Warten; die Ordnerüberwachung scannt mit eigener Kennung. Vorher
  hielt die Seite das Warten eines gleichzeitig wartenden automatischen Scans für ihr eigenes.

### Geändert
- Nachrichten der Dienste nach Bewertung, Tags, Kategorien und EF-Schreibvorgängen kommen jetzt vom Thread-Pool (alle
  Empfänger dispatchen selbst); sie laufen also nach der Rückkehr des Aufrufs. Rauchtest-Schritt „Tags“ liest den
  Menüeintrag deshalb nach der Zuweisung neu (das Menü kann nach dem Anlegen zweier Tags noch einmal neu aufgebaut werden).
- `SqlitePragmaInterceptor(busyTimeoutMs)` und `LibraryContextFactory(busyRetry, messenger, defaultCommandTimeoutSeconds)`
  nehmen verkürzte Zeiten für Tests; produktiv unverändert (busy_timeout 5000, CommandTimeout 30 s).

### Tests
- `tests/PhotoViewerV3.Tests`: 470 (+11): `Database/CatalogWriteRetryTests` (9: Bewertung, Tag-Zuweisung,
  Kategorie-Wert und EF-Schreiber — Einzel-INSERT, Mehrfach-UPDATE in Transaktion, Einstellung — kommen nach einer 2,5 s
  gehaltenen Schreibsperre an, der Aufruf kehrt sofort zurück, Wartenachricht 1 → 0; Bewertung und Tag auf eine beim
  „Neuaufbau“ ersetzte FileId landen auf der neuen Id; Gegenprobe ohne Wiederholung scheitert mit SQLITE_BUSY; nach der
  Höchstdauer kommt der Fehler an; Abbruch schreibt nichts; `IsBusy`), busy_timeout 100 ms und CommandTimeout 1 s statt
  5 s/30 s. Gegen den alten Stand 7 von 9 rot (Gegenprobe und `IsBusy` grün); mit Interceptor, aber altem
  `CatalogUpdateService` bleibt der FileId-Test rot. `Watch/ScanGateCallerIdTests` (2: Kennungen gleichzeitiger
  Wartender in Nachrichten und `GetWaits`, ScanService reicht `ScanOptions.GateCallerId` durch).
- `tests/PhotoViewerV3.Wpf.Tests`: 204 (+2 `Shell/ScanGateCallerIdViewModelTests`: Watch-Scan wartet zuerst, die Seite
  zeigt nur ihr eigenes Warten und wartet weiter, als der Watch-Scan die Sperre bekommt — gegen das alte
  `LibraryViewModel` rot; Statusleisten-Hinweis 1/2/0 Wartende). Volle Suite 3× hintereinander grün. Die Hilfsklasse
  `GateScanService` in `ScanGateWaitViewModelTests` reicht die Kennung wie der echte ScanService durch.
- Rauchtest 23/23 OK, 0 WPF-Binding-Fehler; neuer Schritt „Katalog gesperrt“ (hält BEGIN IMMEDIATE, Bewertung über die
  Kachel kehrt sofort zurück, Statusleiste zeigt den Hinweis, Bewertung nach der Freigabe in der DB; Screenshot
  `19-statusleiste-katalog-wartet`; im Log „nach 5,7 s (2 Versuche) ausgeführt“).

### Offen
- Rohe Schreibpfade der Scans (`IncrementalCatalogWriter`, `LibraryCatalogCleanup`) und synchrone EF-Aufrufe nutzen die
  Wiederholung nicht (Hintergrund-Threads, warten wie bisher bis ≈ 35 s): ein automatischer Scan einer *anderen*
  Bibliothek kann während eines langen „Neu aufbauen“ weiterhin scheitern.
- Wird die App beendet, während eine Änderung wartet, ist sie verloren (Tooltip weist darauf hin).
- Eine Aktion auf eine alte FileId *nach* dem Commit, bevor die Galerie neu geladen hat, trifft weiter ins Leere (vorher
  ebenso; betrifft nur Klicks in diesem Augenblick).

## [0.2.28] — 2026-10-20

### Behoben
- **Wackelnde UI-Tests in der vollen Suite** (`tests/PhotoViewerV3.Wpf.Tests`): `AppDialogWindowTests` (Namensabfrage,
  gelegentlich auch Fehlerdialog) und `ArchiveUiTests.Dialog_Fenster_oeffnet_entpackt_und_schliesst` rissen ab und zu die
  10-s-Grenze (gemessen bis 12 s *im* Dialog), einzeln liefen sie stabil grün. Ursache: Alle UI-Testklassen teilen sich
  den einen `TestUiThread`, liefen als xUnit-Klassen aber parallel. `ShowDialog` und `Dispatcher.Invoke` (Pump) öffnen
  dort verschachtelte Message-Loops, in denen die Dispatcher-Aufträge fremder Testklassen (z. B. die Layout-Messungen der
  `VirtualizingTilePanelTests`) mitliefen — der Dialog kehrte erst danach zurück.
- Abhilfe: neue xUnit-Sammlung `Fixtures/UiTestCollection` (`DisableParallelization = true`); alle zehn Klassen, die den
  UI-Thread nutzen, stehen darin und laufen nacheinander. `TestUiThread` prüft beim Zugriff über den Aufrufstapel, dass
  die aufrufende Testklasse in der Sammlung steht, und wirft sonst sofort — eine vergessene Zuordnung fällt damit immer
  auf statt gelegentlich. `AppDialogWindowTests` misst wie der Archiv-Test nur noch die Zeit im Dialog (nicht den Start
  des UI-Threads); Zeitgrenzen und Timeouts bleiben unverändert.
- Zweites Fehlerbild der Namensabfrage (Eingabe leer statt „02 Fotos“): `ShowDialog` übergeht `ShowActivated = false`,
  der Testdialog wird auch außerhalb des Bildschirms Vordergrundfenster mit fokussiertem, markiertem Eingabefeld — eine
  Taste des Benutzers in seiner eigenen App (z. B. Rücktaste) landete im Test. `TestUiThread` verwirft deshalb echte
  Tastatur- und Maus-Eingaben (`InputManager.PreProcessInput`); kein Test nutzt sie, Fokuswechsel bleiben unberührt.

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 169 (+1 `AppDialogWindowTests.Eingaben_ueber_den_InputManager_erreichen_den_Testdialog_nicht`:
  Rücktaste und Texteingabe über den InputManager ändern die Vorbelegung nicht; Gegenprobe ohne Filter rot). Volle Suite
  10× hintereinander grün (vorher 3 von 7 Läufen rot); Build ohne Warnungen.
- Beim Zusammenführen mit 0.2.21–0.2.27: `Categories/CategoryPickerTests` (aus 0.2.23) in die Sammlung `UiTestCollection`
  aufgenommen — der neue Wächter in `TestUiThread` verlangt das für jede Klasse, die den UI-Thread nutzt.
  `Shell/ScanGateWaitViewModelTests` (aus 0.2.22) nutzt statt ihrer eigenen Sammlung `ScanGateWaitUiCollection` jetzt
  ebenfalls `UiTestCollection` — zwei Sammlungen hätten wieder parallel auf dem einen UI-Thread laufen können.

## [0.2.27] — 2026-10-20

### Neu
- **Knopf „Alle auswählen“** in der Galerie: rechts in der Kopfzeile neben „Rückgängig“ (immer sichtbar) und in der
  Aktionsleiste neben „Auswahl aufheben“; wählt wie Strg+A alle Dateien der aktuellen Seite (`SelectAllCommand`).
  Damit die Ansichtszeile bei 1024 px Fensterbreite einzeilig bleibt, sind Sortier-Auswahl (150 px) und
  Vorschaugrößen-Regler (72 px, ohne Symbol davor) etwas schmaler.

### Tests
- Kopfzeilen-Layouttest (9 Breiten/Varianten) weiter grün mit dem neuen Knopf; Wpf-Tests 168, Rauchtest 21/21.

### Offen
- `AppDialogWindowTests` und `ArchiveUiTests` (echte Fenster) scheitern in der vollen Wpf-Suite gelegentlich (einzeln
  stabil grün, schon bei 0.2.20 beobachtet) — Fokus/Timing, nicht an diese Änderung gebunden.

## [0.2.26] — 2026-10-20

### Neu (ADR-0029)
- **Portable Version**: `pwsh -File build\publish.ps1` baut per `dotnet publish` ein self-contained Paket für
  `win-x64` (kein installiertes .NET nötig) als Ordner `artifacts\PhotoViewerV3-<Version>-win-x64\` plus ZIP — kein
  Single-File (LibVLC braucht seinen Plugin-Ordner), kein Trimming (WPF), nur `libvlc\win-x64`, nur deutsche
  Satelliten-Ressourcen. Das Skript prüft Produktversion = CHANGELOG sowie SQLite- und LibVLC-Bestandteile.
  Schalter `-OutputRoot`, `-NoZip`, `-FrameworkDependent`. `artifacts/` und `publish/` stehen in `.gitignore`.
- **Rauchtest gegen eine beliebige EXE**: `build\run-smoke.ps1 -Exe <PhotoViewerV3.exe> -TestLib <Testbibliothek>`
  kopiert die Testbibliothek in einen frischen Arbeitsordner (Standard `%TEMP%\PhotoViewerV3-smoke-<Tag>`), startet
  mit eigenem AppData und gibt Ergebnis, Log-Auszug und Screenshots aus (ExitCode 0 = alle Schritte OK).

### Geändert
- **App-Version aus dem CHANGELOG**: `PhotoViewerV3.Wpf.csproj` liest die Version beim Build aus dem obersten
  Eintrag `## [x.y.z]` (bisher fest `0.2.0`, Einstellungen → „Über“ zeigte deshalb seit 0.2.1 die falsche Version).
  Der Rauchtest-Schritt „Einstellungen“ erwartet jetzt die Assembly-Version statt fest „0.2.0“.

### Geprüft
- Pfade hängen nicht vom Repo-Layout ab: Daten unter `%LocalAppData%\PhotoViewerV3` bzw. `PHOTOVIEWERV3_APPDATA` und
  `database-location.json`; Formatregister sind Code; SQLite (`e_sqlite3.dll`) und LibVLC liegen im Paket; FFmpeg
  wird wie bisher im PATH bzw. unter den winget-Links gesucht.
- Paket 0.2.26: 780 Dateien, 256,6 MB entpackt, ZIP 114 MB. Rauchtest mit der **veröffentlichten** EXE: 21/21 OK,
  0 WPF-Binding-Fehler, Version „0.2.26“, LibVLC 3.0.21 und FFmpeg gefunden.

### Offen
- FFmpeg ist nicht im Paket (Lizenz, Größe) und wird nicht neben der EXE gesucht; kein Code-Signing, kein Installer.
- `BuildHost-net472/-netcore` (~4 MB aus `Microsoft.EntityFrameworkCore.Design`) liegen unnötig im Paket.

## [0.2.25] — 2026-10-20

### Neu (ADR-0032)
- **Archive automatisch entpacken** (Einstellungen → **Archive**, Standard **aus**, global): findet ein Incremental-Scan
  („Aktualisieren“ oder Ordnerüberwachung) ein **neues** Archiv unter einer **Eingangs-Wurzel**, entpackt der neue
  `IArchiveAutoExtractService` (Services/Archive) es **nach dem Scan** vollständig in den Ordner mit dem Archivnamen daneben
  (verschachtelte Archive über das bestehende Runden-System bis Tiefe 3) und startet danach einen Incremental-Scan.
  Optional (zweiter Schalter, ebenfalls aus) wandert das Archiv nach fehlerfreiem Entpacken über
  `IFileOperationService.RecycleAsync` protokolliert in den Papierkorb.
- Unberührt bleiben Archive unter der Archiv-Wurzel, Archive aus Fresh-Scan/„Neu aufbauen“, Archive in einem aus einem
  Archiv entstandenen Ordner (neue `ArchiveExtractedMessage` des `ArchiveService`, manuell wie automatisch — keine
  Schleife) und Archive, deren Zielordner schon existiert. Passwortgeschützte, beschädigte und leere Archive bleiben liegen.
- **Sperren**: entpackt wird je Archiv mit `IScanGate.AcquireForFileOperationAsync` (wartet auf das Ende des Scans),
  Papierkorb und Folge-Scan holen die Sperre danach selbst (nicht wiedereintrittsfähig, ADR-0025). Kein Eingriff in den
  ScanService.
- **Ordnerüberwachung**: neues `IOwnChangeScope.AddTree` (Ordner und alles darunter gilt als eigen); die Zielordner sind bis
  nach dem Folge-Scan angemeldet — kein zweiter automatischer Scan.
- **Statusleiste**: Fortschritt („Archiv 1/3 wird automatisch entpackt: …“), Ergebnis („Automatisch entpackt: n Archive ·
  m Dateien · k im Papierkorb“), gemeldete Archive als Warnung mit Name und Grund im Tooltip.

### Tests
- `tests/PhotoViewerV3.Tests`: 434 (+10): `Archive/ArchiveAutoExtractServiceTests` (9: Standard aus, aus = kein Entpacken,
  an = Entpacken neben dem Archiv + Folge-Scan + eigene Ordnerbäume, Papierkorb protokolliert, nur Eingang, verschlüsselt/
  beschädigt/Zielordner vorhanden bleiben liegen und werden gemeldet, wartet auf gehaltene Sperre und kein Deadlock mit
  gemeinsamem ScanGate, verschachtelte Reste ohne Schleife, Archive aus manuellem Entpacken unberührt),
  `Watch/OwnChangeRegistryTests` (+1: `AddTree`).
- `tests/PhotoViewerV3.Wpf.Tests`: 173 (+5): `Settings/SettingsAutoExtractTests` (Laden, Schalter speichern, Speicherfehler
  setzt zurück, ohne Dienst deaktiviert, Statusleisten-Text).
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler (Einstellung im Rauchtest aus; kein eigener Schritt).
- Hinweis: bei stark ausgelasteter Maschine (mehrere parallele Läufe) schlugen bestehende Fenster-Tests
  (`EarlyCloseWindowTests`, `AppDialogWindowTests`, `GalleryToolbarLayoutTests`, `ArchiveUiTests`-Fenster) gelegentlich mit
  Dispatcher-Timeout fehl; einzeln und im erneuten Gesamtlauf grün.

### Offen
- Einstellung nur global (nicht je Bibliothek); Archive, die vor dem Einschalten schon im Katalog standen, und Archive
  eines Fresh-Scans bleiben manuell. Kein eigener Rauchtest-Schritt für das automatische Entpacken.

## [0.2.24] — 2026-10-20

### Behoben (ADR-0028, offener Punkt aus 0.2.14)
- **„Neu aufbauen“ verliert bei Abbruch oder Absturz keine Benutzerdaten mehr.** Bisher wurde der Katalog vor der
  Enumeration geleert und committet; brach der Lauf danach ab (Abbrechen, Ausnahme, App-Absturz), waren Katalog,
  Favoriten, Bewertungen, Tags und Kategorie-Werte weg. Jetzt bleibt der bisherige Katalog während der Enumeration
  unangetastet und benutzbar; danach laufen Sichern der Benutzerdaten, Löschen der alten Zeilen, Bulk-Insert,
  Zurückschreiben und Suchindex in **einer** Schreibtransaktion (`Services/Scanning/FullScanCatalogWriter`). Abbruch
  oder Fehler an jeder Stelle rollen zurück, ein Absturz hinterlässt eine nie committete Transaktion, die SQLite beim
  nächsten Öffnen verwirft — keine Wiederaufnahme-Logik, keine Schema-Änderung. Der Thumbnail-Cache-Ordner wird erst
  nach dem Commit gelöscht (`ILibraryService.DeleteThumbnailCacheAsync`).
- Kategorie-Werte (0.2.20) wurden bereits übernommen; sie sind jetzt in allen Fehlerfällen mit abgedeckt. Bewertungen
  oder Tags, die während der Enumeration noch vergeben werden, gehen ebenfalls mit über.
- Fresh-Scan nutzt denselben Schreibpfad; der eigene Aufräum-Rollback (`RollbackPartialScan`) entfällt.
- Start-Ids werden unter der Schreibsperre erneut geprüft: Hatte ein Scan einer anderen Bibliothek während der
  Enumeration dieselben Ids vergeben, verwarf `INSERT OR IGNORE` bisher still Dateien und Ordner (im Test: 1 statt 3
  Dateien) — jetzt werden die Zeilen samt ParentId/FolderId nach hinten verschoben.
- `UserDataCarryOver` arbeitet in der Transaktion des Aufrufers statt mit eigener Verbindung; `BulkScanWriter`
  (`InsertFolders`/`InsertFiles`) und `FtsIndexer.PopulateAll` nehmen optional eine offene Transaktion entgegen.
- Bestätigungsdialog und Tooltip „Neu aufbauen“ nennen Kategorien und dass der bisherige Katalog bis zum Abschluss
  bestehen bleibt.
- ADR-0028 (neu), ADR-0023 mit Statusvermerk „Ablauf abgelöst durch ADR-0028“, PLAN Kap. 4 und 12 nachgezogen.

### Tests
- `tests/PhotoViewerV3.Tests`: 434 (+10 `Scanning/ClearAndRescanFailureTests`: Abbruch in jeder Phase — Enumeration,
  direkt nach dem Löschen der alten Zeilen, beim Einfügen, vor dem Suchindex —, Ausnahme in drei Phasen samt
  erfolgreichem Folgelauf, simulierter Absturz vor dem Commit (DB-Dateien in diesem Moment kopiert, geöffnet, alter
  Katalog samt Benutzerdaten unverändert, Folgelauf übernimmt sie), erfolgreicher Neuaufbau mit Kategorien und einer
  Bewertung aus der Enumeration, Scan einer zweiten Bibliothek während der Enumeration vergibt die Start-Ids; jeweils
  FileIds, Suchindex, Thumbnail-Zeilen, Tags und Kategorie-Werte verglichen).
  `Mengen_Messung_synthetischer_Katalog` misst jetzt den ganzen Transaktionsblock: 500 000 Dateien (jede mit Bewertung,
  jede zehnte mit zwei Tags) — Sichern 5,4 s, Zurückschreiben 3,3 s, Löschen + Einfügen + Zurückschreiben + Suchindex
  in einer Transaktion 27,8 s (Rechner parallel mit weiteren Builds belastet).
- `tests/PhotoViewerV3.Wpf.Tests`: 168 (unverändert; Fake von `ILibraryService` um `DeleteThumbnailCacheAsync` ergänzt).
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler (Schritt „Aktualisieren + Neu aufbauen“ prüft Favoriten/Bewertungen nach ClearAndRescan).

### Offen
- Die Schreibtransaktion am Ende von „Neu aufbauen“ ist bei sehr großen Bibliotheken lang (500k: ~30 s); andere
  Schreiber (Bewertung setzen, Tags, Thumbnail-Status) scheitern in dieser Zeit nach `busy_timeout` 5 s mit „database
  is locked“ — die einzelne Aktion schlägt fehl, der Katalog nicht. Denkbar: Bedienung während der Phase „Katalog wird
  geschrieben…“ sperren oder Schreiber mit Wiederholung.

## [0.2.23] — 2026-10-20

### Neu (ADR-0027, Nachtrag 0.2.23)
- **Strg+K — Kategorien zuweisen per Tastatur** in Galerie und Lightbox (auch Rechtsklick → „Kategorien zuweisen…“):
  Dialog mit je Kategorie einer Auswahlliste (Werte, „Kein Wert“, bei unterschiedlichen Werten der Auswahl
  „(gemischt — nicht ändern)“); Tab/↑/↓/Enter/Esc, nur geänderte Kategorien werden geschrieben. Ausgangswerte kommen aus
  der DB, zählen also auch ausgewählte Dateien außerhalb der Seite. Strg+K war in Galerie und Lightbox frei.
- **Lightbox zeigt Kategorie-Werte**: gesetzte Werte in der Titelleiste („XX: N · Projekt: Nord“), im Info-Panel (I)
  alle Kategorien mit Wert bzw. „—“ und Knopf „Zuweisen… (Strg+K)“; folgt Blättern, Zuweisungen und Änderungen auf der
  Seite „Kategorien“. Tastenhilfe nennt Strg+T und Strg+K.
- **Suche findet Kategorie-Werte** (Teilstring ohne Groß-/Kleinschreibung wie Tag-Namen) — per Join
  (`FileCategoryValues.ValueId IN (… CategoryValues.Name LIKE …)`) statt FTS: keine Index-Pflege, Umbenennen/Löschen
  wirkt sofort; der Weg läuft über `IX_FileCategoryValues_Value`. 500 000 Dateien: 33 ms (seltener Wert) bis 320 ms
  (50 000 Treffer).
- **Kategorie-Spalten sortieren die ganze Trefferliste serverseitig** (SQL mit Paging inkl. Reverse-Trick): Werte in der
  Reihenfolge der Verwaltung, erneuter Klick kehrt um, „ohne Wert“ immer am Ende, gleiche Werte nach Datum neu → alt.
  Die Sortier-Auswahl zeigt „XX (Kategorie) ↑/↓“, Sortierpfeil an der Spalte. 500 000 Dateien: Seite 1 ≈ 0,5 s, Mitte
  ≈ 1,25 s (wie „Name A → Z“).

### Tests
- `tests/PhotoViewerV3.Tests`: 425 (+1 `Categories/CategoryServiceTests`: Werte einer Auswahl über die Chunk-Grenze lesen
  und je Kategorie setzen/entfernen).
- `tests/PhotoViewerV3.Wpf.Tests`: 189 (+21): `Categories/GalleryPageSourceCategoryTests` (+13: Suche nach Werten, mit
  Dateiname und Filter, Umbenennen/Löschen ohne Index-Pflege, Abfrageplan ohne `SCAN f`/`SCAN fcv`; Sortierung auf/ab mit
  „ohne Wert“ am Ende, Werte-Reihenfolge der Verwaltung, Seiten inkl. Reverse-Trick, mit Filter/Suche, Primärschlüssel-
  Lookup im Plan), `Categories/CategoryPickerTests` (+8: Ausgangszustand gemeinsam/kein Wert/gemischt, nur Änderungen im
  Ergebnis, echtes Dialogfenster mit Fokus/Enter/Esc, Kopfzeilen-Logik, Strg+K belegt ohne Doppelbelegung in Galerie und
  Lightbox).
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler; Schritt „Kategorien“ erweitert um Strg+K (Galerie, gemischte Auswahl,
  Tastenbindung), Suche nach Wert, Sortierung auf/ab samt Sortierpfeil und Rückkehr zur Standard-Sortierung, Lightbox
  (Titelleiste, Info-Panel, Zuweisen/Entfernen).
- Bekannt, nicht durch diese Änderung: unter paralleler Testausführung und Last scheitern in `PhotoViewerV3.Wpf.Tests`
  sporadisch 2–3 UI-Tests mit Zeitüberschreitung (`AppDialogWindowTests`, `EarlyCloseWindowTests`, `ArchiveUiTests`;
  gemeinsamer UI-Thread). Gegengeprüft: Stand 0.2.20 zeigt dasselbe; seriell
  (`-- xUnit.ParallelizeTestCollections=false`) alle 189 grün.

### Offen
- Tiefe Seiten der Kategorie-Sortierung kosten bei 500 000 Dateien gut eine Sekunde (Top-N-Sortierung wie Name/Größe).
- Der Tag-Zweig der Suche liest alle `FileTags`-Zeilen (Plan `SCAN ft`); dieselbe `IN`-Form wie bei Kategorie-Werten
  wäre bei vielen Tag-Zuweisungen schneller.
- Die Suche findet Kategorie-Werte, nicht Kategorie-Namen.

## [0.2.22] — 2026-10-20

### Neu (offene Punkte aus 0.2.14 und 0.2.17, ADR-0025 Nachtrag)
- **Wartezustände der Scan-Sperre sichtbar**: `ScanGate` meldet über den Messenger, wenn ein Scan oder Datei-Batch auf
  eine belegte Bibliothek warten muss — `ScanGateWaitStartedMessage` / `ScanGateWaitEndedMessage`
  (`Services/Messaging/ScanGateMessages.cs`, `ScanGateWait` mit Wartendem und Belegung Scan/Datei-Batch). Wer die
  Sperre sofort bekommt, meldet nichts. Neu in `IScanGate`: `GetWaits()` (Momentaufnahme) und `CancelWait(id)` — der
  Wartende bekommt eine `ScanGateWaitCanceledException` (ein `OperationCanceledException`), bevor der Batch etwas
  protokolliert oder ändert; die Abbruch-Fortsetzungen laufen nicht auf dem UI-Thread.
- **Statusleiste**: solange eine Datei-Operation (Verschieben, Papierkorb, Ins Archiv, Umbenennen, Ordner
  anlegen/umbenennen, Rückgängig, Sortierer-Lauf) auf einen laufenden Scan derselben Bibliothek wartet, steht links
  „Wartet auf laufenden Scan…“ (Uhr-Symbol, Tooltip) mit **Abbrechen**; danach kurz „Wartende Datei-Operation
  abgebrochen“. Anzeige-Modell `Shell/ScanGateWaitIndicator` (Filter je Verwender, UI-Thread-Dispatch).
- **Bibliotheksseite**: ein manueller Scan, der auf einen anderen Scan oder einen Datei-Batch wartet, zeigt im
  Scan-Panel „Wartet auf laufenden Scan…“ bzw. „Wartet auf laufende Datei-Operation…“ statt „Ordner werden
  durchsucht…“, bis er die Sperre hat (`LibraryViewModel.IsWaitingForGate`). **Abbrechen** während des Wartens meldet
  „Der Scan wurde abgebrochen, bevor er begann; der Katalog ist unverändert.“
- `StatusBarViewModel` und `LibraryViewModel` nehmen optional einen `IMessenger` (Tests); `StatusBarViewModel` bekommt
  `IScanGate` per DI. Die Galerie bleibt unverändert (sie verwirft den Abbruch wie bisher still).

### Tests
- `tests/PhotoViewerV3.Tests`: 432 (+8 `Watch/ScanGateWaitTests`: keine Meldung ohne Warten, Beginn/Ende mit Wartendem
  und Belegung, Scan wartet auf Datei-Batch, `CancelWait` mit eigener Ausnahme ohne Belegung, Abbruch über das Token des
  Aufrufers zählt nicht als Benutzer-Abbruch, unbekannte Id, mehrere Bibliotheken, `MoveAsync` beim Warten abgebrochen —
  nichts verschoben, kein Batch protokolliert, danach normal).
- `tests/PhotoViewerV3.Wpf.Tests`: 175 (+7 `Shell/ScanGateWaitViewModelTests`: Indikator zeigt/abbricht, filtert,
  übernimmt ein schon laufendes Warten; Statusleiste mit Bestätigung; Bibliotheksseite wartet auf Datei-Batch, Abbruch
  während des Wartens, kein Hinweis ohne Konkurrenz) mit `Fixtures/NullService` (DispatchProxy-Leerdienste).
- Rauchtest: neuer Schritt „Wartezustände“ (belegt die Sperre selbst: Statusleiste + Abbrechen, Scan-Panel wartet auf
  Datei-Batch, Screenshots `12-statusleiste-wartet`, `12-bibliothek-wartet`). Rauchtest 22/22 OK, 0 WPF-Binding-Fehler
  (ein erster Lauf scheiterte einmal an „Thumbnails neu erzeugt: 43/42“ im Schritt „Neu aufbauen“ — Zeitverhalten der
  Vorschaubilder, nicht berührt; Wiederholung grün).
- Die neue Wpf-Testklasse läuft in einer eigenen, nicht parallelen Collection (teilt den Test-UI-Thread mit den
  Fenster-Tests). Unter hoher Rechnerlast (sechs Agenten parallel) schlagen die zeitkritischen Fenster-Tests
  (`EarlyCloseWindowTests`, `AppDialogWindowTests` „< 10 s“, `GalleryToolbarLayoutTests`) im parallelen Lauf
  sporadisch fehl — auch ohne die neuen Tests; seriell (`-- xUnit.ParallelizeTestCollections=false`) 175/175 grün.

### Offen
- Die Bibliotheksseite ordnet ein Warten ihrem Scan über Bibliothek und Zeitpunkt zu (erster wartender Scan vor dem
  ersten eigenen Fortschritt): wartet gleichzeitig ein automatischer Scan der Ordnerüberwachung, kann sie den falschen
  erwischen. Abhilfe wäre eine Kennung des Aufrufers in `IScanGate.AcquireAsync`.
- Die Ruhephase beim Umzug der Katalog-DB belegt die Sperre wie ein Datei-Batch; wartet sie, erscheint der Hinweis
  ebenfalls in der Statusleiste (der Umzug läuft ohnehin modal mit eigenem Zeitlimit).
- Eine Datei-Operation, die hinter einer anderen auf den internen Schreib-Lock des `FileOperationService` wartet, zeigt
  keinen Hinweis (nur das Warten auf die Scan-Sperre wird gemeldet).

## [0.2.21] — 2026-10-20

### Behoben (offene Punkte aus 0.2.18, ADR-0026 ergänzt)
- **Kein Schreiben mehr in die alte DB während und nach dem Umzug**: Kontexte der `LibraryContextFactory` tragen einen
  Gate-Interceptor, der bei gesetzter Sperre `SaveChanges`, neue Transaktionen und Non-Query-Befehle mit
  `DatabaseSuspendedException` abweist — auch bei Kontexten, die vor der Sperre erzeugt wurden (Favorit im selben
  Augenblick); Lesen bleibt für sie erlaubt. Zusätzlich hält der Umzug die `BEGIN IMMEDIATE`-Schreibsperre der alten Datei bis zur Rücknahme bzw. nach
  Erfolg bis zum Prozessende; damit scheitern auch rohe Verbindungen, die schon vor der Sperre offen waren, mit
  `SQLITE_BUSY`. Leser (Galerie, Backup-API) arbeiten im WAL-Modus weiter. Der Checkpoint vor der Kopie ist deshalb
  `PASSIVE` (die Backup-API kopiert den WAL-Inhalt ohnehin mit). `DatabaseRelocationService` ist jetzt `IDisposable`
  (gibt die gehaltene Sperre beim Beenden frei).
- **Vorschaubilder nach einer Rücknahme sofort**: während der Pause angeforderte Vorschaubilder werden je FileId
  zurückgestellt (neueste Anforderung gilt, Archive ausgenommen) und von `IThumbnailService.Resume` sofort nachgereicht,
  nicht erst beim nächsten Seitenwechsel.
- **Thumbnail-Ordner nur einmal durchlaufen** (`Relocation/ThumbnailInventory`): die Prüfung erstellt die Dateiliste,
  der Umzug übernimmt sie (ohne vorherige Prüfung: ein eigener Durchlauf) und kopiert danach statt erneut aufzuzählen —
  bisher drei Durchläufe (Prüfung, erneute Prüfung im Umzug, Kopie). Die Prüfung bleibt so streng wie bisher: nach der
  Pause der Thumbnail-Worker werden seither erzeugte Vorschaubilder aus der DB ergänzt (`GeneratedAtUtc` bzw.
  `LastAttemptAtUtc` ab Beginn des Durchlaufs − 2 s) und der Platz mit der endgültigen Liste und frisch gelesenem freiem
  Platz erneut geprüft. „Übernehmen“ durchläuft den Ordner gar nicht. `RelocationTargetCheck.ThumbnailFiles` zählt jetzt
  nur die zu kopierenden Dateien (ohne `.tmp`), wie dokumentiert; der Platzbedarf rechnet `.tmp` weiter mit.
- Annahmen: Vorschaubild-Dateien entstehen nur über den Thumbnail-Dienst (Datei vor DB-Zeile); eine fremde Datei, die
  nach der Prüfung ohne DB-Zeile in den Ordner gelegt wird, zieht nicht mit. Seit der Prüfung entfernte Dateien gelten
  beim Kopieren nicht als „übersprungen“.

### Tests
- `tests/PhotoViewerV3.Tests`: 430 (+6): `Database/DatabaseRelocationServiceTests` (+5: älterer Kontext — `SaveChanges`,
  Non-Query über offene Verbindung und rohe Verbindung — schreibt weder während noch nach dem Umzug in die alte DB;
  nach der Rücknahme speichert derselbe Kontext wieder normal; Kopie nach der Liste der Prüfung mit ergänztem neuem
  Vorschaubild und ohne nachträglich abgelegte fremde Datei; Platz wird nach dem Abgleich erneut geprüft und bricht mit
  Rücknahme ab; Umzug ohne vorherige Prüfung), `Thumbnails/ThumbnailServicePauseTests` (+1: zurückgestellte
  Anforderungen kommen nach `Resume` ohne neue Anforderung, je Datei einmal). Ohne die drei Korrekturen scheitern
  5 der 6 neuen Tests (gegengeprüft; der sechste deckt den Umzug ohne vorherige Prüfung ab). Bestehende Tests: „Neustart“ gibt vorher die gehaltene
  Sperre frei (`Service.Dispose` = Prozessende), `ThumbnailFiles` erwartet 2 statt 3 (ohne `.tmp`).
- `tests/PhotoViewerV3.Wpf.Tests`: 168 (unverändert), grün. Unter Last durch parallele Läufe scheiterte im Standardlauf
  wechselnd je 1 Fenster-Test (`Shell/EarlyCloseWindowTests`, `Shell/AppDialogWindowTests`, UI-Timeout) — ebenso auf
  dem unveränderten Stand 0.2.20 (dort 3); mit `xUnit.ParallelizeTestCollections=false` 168/168.
- Rauchtest 21/21 OK, 0 WPF-Binding-Fehler (kein eigener Umzugs-Schritt; Umzug ist durch die Unit-Tests abgedeckt).

## [0.2.20] — 2026-10-20

### Neu (ADR-0027)
- **Kategorien mit festen Werten**: neue Seite **Kategorien** (Navigation unter „Tags“) — Kategorien anlegen (optional
  gleich mit Werten „N, NN“), umbenennen, sortieren, löschen; Werte hinzufügen (mehrere durch Komma), umbenennen,
  sortieren, löschen; Zähler je Wert. Jede Datei hat je Kategorie höchstens einen Wert.
- **Zuweisen** per Rechtsklick auf Kachel oder Zeile: je Kategorie ein Untermenü mit den Werten (Häkchen / „teilweise“
  bezogen auf die Auswahl) und „Kein Wert“.
- **Filter** „Kategorien“ in der Galerie-Kopfzeile (sichtbar, sobald es Kategorien gibt): je Kategorie Werte und
  „ohne Wert“ — innerhalb einer Kategorie ODER, zwischen Kategorien UND; wirkt mit Suche, Tag- und Status-Filter zusammen.
- **Listenspalten**: je Kategorie eine Spalte (Sortierung innerhalb der Seite); ein-/ausblenden und Reihenfolge werden
  gespeichert (`ui.gallery.listColumns`: Token `Kategorie:{Id}` bzw. `!Kategorie:{Id}`), neue Kategorien erscheinen
  automatisch am Ende.
- **Schema** (Migration `AddCategories`): `Categories`, `CategoryValues`, `FileCategoryValues` (PK FileId+CategoryId,
  alle Fremdschlüssel ON DELETE CASCADE); `ICategoryService` (Services/Categories). „Neu aufbauen“ übernimmt die Werte
  wie Tags (`UserDataCarryOver`, ADR-0023).

### Tests
- `tests/PhotoViewerV3.Tests`: 424 (+7 `Categories/CategoryServiceTests`: Anlegen mit Werten, Duplikate/leere Namen,
  ein Wert je Kategorie setzen/ersetzen/entfernen, fremder Wert abgelehnt, Kaskaden beim Löschen von Wert/Kategorie/Datei,
  Umbenennen/Reihenfolge, „Neu aufbauen“ übernimmt die Werte).
- `tests/PhotoViewerV3.Wpf.Tests`: 168 (+13): `Categories/GalleryPageSourceCategoryTests` (6: Wert, ODER, „ohne Wert“,
  UND zwischen Kategorien, mit Suche — gegen echte, migrierte DB), `Categories/CategoryColumnSettingsTests` (7: Spalten-Token,
  Aus-/Einblenden, Reihenfolge, Indexer der Werte, Sortierung über den Indexer-Pfad, Werteingabe zerlegen).
- Rauchtest 21/21 OK (neuer Schritt „Kategorien“: Anlegen, Zuweisen über das Menü-Kommando inkl. Bedienbarkeit, Filter,
  Listenspalte, Verwaltung, Umbenennen, Löschen), 0 WPF-Binding-Fehler.

### Offen
- Kategorie-Werte lassen sich nur über das Rechtsklick-Menü zuweisen (kein Tastenkürzel, keine Anzeige in der Lightbox);
  die Suche findet Kategorie-Werte nicht; Kategorie-Spalten sortieren nur die geladene Seite.

## [0.2.19] — 2026-10-20

### Neu
- **Vorschaugröße in der Galerie einstellbar**: Regler in der Kopfzeile neben dem Umschalter Kachel/Liste (nur in der
  Kachelansicht), außerdem Strg+Mausrad und Strg+Plus/Minus im Kachel-Grid. Kachelbreite 120–320 px in 20er-Stufen
  (Standard 180; 320 = Auflösung der Vorschaubilder), die Höhe wächst mit dem Bildbereich, die Namenszeile bleibt gleich.
  Die Wahl steht in der Settings-Tabelle (`ui.gallery.tileSize`). Beim Umschalten bleibt die oberste sichtbare Kachel
  oben (`VirtualizingTilePanel` verankert den Scroll-Offset am ersten sichtbaren Item).

### Tests
- `tests/PhotoViewerV3.Wpf.Tests`: 155 (+7): `Gallery/VirtualizingTilePanelTests` (Kachelgröße ändern hält die oberste
  Kachel, 2 Fälle; Begrenzen/Einrasten der Vorschaugröße, 5 Fälle); Kopfzeilen-Layouttest weiter grün (Regler passt bei
  1024 px in die Ansichtszeile).

## [0.2.18] — 2026-10-20

### Neu (ADR-0026)
- **Speicherort des Katalogs wählbar**: Einstellungen → Speicherorte → Datenbank → **Ändern…** zieht `library.db`
  samt Vorschaubildern (`thumbs\`, Herberts Entscheidung: zieht mit) in einen anderen Ordner; **Standardort** holt sie
  zurück. Der Ort steht in `database-location.json` in der App-Wurzel (`DatabaseLocationStore`), `AppDataPaths.Resolve`
  liest sie beim Start; Logs bleiben in der App-Wurzel. Beschädigte Datei oder gewählter Ordner ohne `library.db`
  (Laufwerk abgezogen) → Start bricht mit Meldung ab statt still einen leeren Katalog anzulegen.
- **Sicherer Umzug** (`Services/Database/Relocation/DatabaseRelocationService`): Ziel prüfen (lokal, kein UNC/
  Netzlaufwerk, nicht in einer Bibliothekswurzel oder im Thumbnail-/Log-Ordner, Schreibprobe, Platz inkl. Reserve,
  OneDrive nur mit Warnung) → Ordnerüberwachung stoppen, alle Bibliotheken über `IScanGate` belegen, Thumbnail-Worker
  pausieren (`IThumbnailService.PauseAsync/Resume`), höchstens 30 s warten → Context-Factory sperren
  (`IDatabaseAccessGate`, neue Verbindungen werfen `DatabaseSuspendedException`), `ClearAllPools`, `BEGIN IMMEDIATE` →
  `wal_checkpoint(TRUNCATE)` + SQLite-Backup-API → `integrity_check` und Zeilenzahl je Tabelle → `Thumbnails.CachePath`
  auf den neuen Ordner umschreiben → Vorschaubilder kopieren → neuen Ort atomar speichern. Erst dann gilt er.
- **Vorhandener Katalog im Ziel**: „Übernehmen“ (gültiger Katalog; nichts kopiert, der bisherige bleibt erhalten) oder
  „Ersetzen“ (vorhandene Dateien werden in `….ersetzt-{Zeit}` umbenannt und beim nächsten Start gelöscht).
- **Fehler/Abbruch**: Rücknahme-Stapel räumt das Ziel auf, benennt Beiseitegelegtes zurück, hebt Sperren auf; die App
  arbeitet mit dem alten Ort weiter, `IDialogService` nennt den Grund. Während des Umzugs ist das Fenster gesperrt.
- **Neustart statt Umschalten zur Laufzeit** (`Shell/AppRestartService`): nach Erfolg startet die App neu; der neue
  Prozess löscht alte DB-Dateien und den alten `thumbs\` im Hintergrund (bis zu 10 Versuche, nie etwas vom aktiven Ort).

### Tests
- `tests/PhotoViewerV3.Tests`: 417 (+23): `Database/DatabaseRelocationServiceTests` (14: Umzug mit Prüfung und
  umgeschriebenen Thumbnail-Pfaden, „Neustart“ mit neuem Ort und Aufräumen, zurück zum Standardort, Abbruch beim
  Kopieren und Fehler beim Speichern → vollständige Rücknahme, Scan über die Wartezeit, Warten auf einen Datei-Batch,
  vorhandener Katalog: erkennen/übernehmen/ersetzen/Rücknahme nach dem Beiseitelegen, kaputte Datei, ungeeignete Ziele,
  OneDrive), `Database/DatabaseLocationStoreTests` (8: Persistenz, beschädigte Datei, fehlendes Laufwerk, Aufräumen nie
  am aktiven Ort, gesperrte Datei bleibt vorgemerkt), `Thumbnails/ThumbnailServicePauseTests` (1). Ohne Rücknahme
  scheitern 3 der Relocation-Tests (gegengeprüft).
- `tests/PhotoViewerV3.Wpf.Tests`: 148 (+8 `Settings/SettingsRelocationTests`: Neustart nach Erfolg, Fehlerdialog mit
  weiter gültigem Ort, ungeeignetes Ziel, Übernehmen/Ersetzen/Abbrechen, fremde Datei, OneDrive-Warnung).

### Offen
- Ein EF-Kontext, der vor der Sperre erzeugt wurde und erst danach speichert (Favorit im selben Augenblick), schreibt
  noch in die alte DB; die `BEGIN IMMEDIATE`-Barriere fängt nur laufende Transaktionen ab.
- Während der Pause verworfene Vorschaubild-Anforderungen kommen nach einer Rücknahme erst mit dem nächsten Seitenwechsel.
- Der Thumbnail-Ordner wird für Prüfung und Umzug mehrfach durchlaufen (bei 500k Dateien spürbar).

## [0.2.17] — 2026-10-19

### Behoben (offene Punkte aus 0.2.14, ADR-0025)
- **Scan und Datei-Operation derselben Bibliothek schließen sich aus**: jeder Batch des `FileOperationService`
  (Verschieben, Papierkorb, Umbenennen, Ordner anlegen/umbenennen, Rückgängig) belegt über
  `IScanGate.AcquireForFileOperationAsync` die beteiligten Bibliotheken (Dateien und Move-Ziel; bei Undo die Wurzeln der
  protokollierten Pfade), bevor er den Katalog liest, und gibt sie vor der Rückkehr frei. Ein Scan wartet auf das
  Batch-Ende, ein Batch auf das Scan-Ende (abbrechbar); kein Scan sieht mehr eine verschobene Datei als „fehlt“ + „neu“.
  Die Datei-Variante löst `IScanGate.Acquired` nicht aus. Entpacken (Papierkorb, dann Scan), Sortierer und
  „Ins Archiv…“ halten die Sperre beim Scan-Aufruf nie — kein Deadlock.
- **Kein leerer automatischer Scan nach eigenen Operationen**: neu `Services/Watch/OwnChangeRegistry`
  (`IOwnChangeRegistry`, Singleton). Vor jedem Dateisystem-Schritt trägt der Batch Quelle, Ziel und neu angelegte
  Ordner ein — nur Pfade, deren Katalog er mitbucht (ein Ziel außerhalb der Bibliothek der Datei bleibt fremd). Die
  Ordnerüberwachung verwirft Ereignisse dieser Pfade (bei „Renamed“ nur, wenn beide Pfade eigen sind) bis 10 s nach
  Batch-Ende. Scheitert Dateisystem- oder Katalog-Schritt, wird der Pfad zurückgenommen und zählt nachträglich als
  Ereignis; externe Änderungen an anderen Pfaden zählen weiter, auch mitten im Batch. Der Sortierer trägt gelöschte
  leere Quellordner ebenfalls ein.

### Tests
- `tests/PhotoViewerV3.Tests`: 394 (+16): `Operations/FileOperationScanGateTests` (7: Move wartet auf laufenden Scan,
  Scan wartet auf Papierkorb-Batch und sieht sein Ergebnis, Abbruch beim Warten, Ordner-Operationen/Undo/Umbenennen
  warten, Eingang → Archiv neben parallelen Scans, Entpacken mit Papierkorb + Scan, Sortierer-Lauf + Undo + Scans —
  jeweils ohne Deadlock und mit konsistentem Katalog), `Watch/LibraryWatchServiceTests.OwnChanges` (4: kein Scan nach
  eigenem Verschieben in neue Ordner, nach Papierkorb/Umbenennen/Ordner-Operationen/Verschieben aus der Bibliothek/
  Undo; fremde Datei danach bzw. während des Batches wird erkannt; zurückgenommener Pfad zählt nur unter einer Wurzel),
  `Watch/OwnChangeRegistryTests` (5). Die beiden „kein Scan“-Tests scheitern ohne die Unterdrückung (gegengeprüft).
- `tests/PhotoViewerV3.Wpf.Tests`: 140 (+1 `ArchiveTarget/ArchiveMoveServiceGateTests`: „Ins Archiv…“ wartet auf den
  Scan und gibt die Bibliothek frei).
- Rauchtest 20/20 OK, 0 WPF-Binding-Fehler; die Ordnerüberwachung scannte im ganzen Lauf nur einmal (Schritt
  „Datei kopieren“), nicht mehr nach Verschieben, Papierkorb, „Ins Archiv…“ und Sortierer.

### Offen
- Ein Batch, der auf einen langen Scan wartet, zeigt in der Galerie keinen Hinweis aufs Warten (nur der laufende Scan
  in der Statusleiste; im Log „Datei-Operation wartet auf den laufenden Scan“).

## [0.2.16] — 2026-10-19

### Neu
- **Sortierer und Flatten & Move über alle Wurzeln** (ADR-0020, ADR-0024): Quelle ist jede Wurzel der Bibliothek
  (Eingang oder Archiv) bzw. ein Ordner darunter, Ziel standardmäßig die Archiv-Wurzel (bzw. ein Ordner darunter) oder
  „Quellordner“. Gemeinsame Auswahl `Sorter/Views/SorterLocationView` + `SorterRootSelection` für beide Bereiche:
  ComboBox je Quelle/Ziel mit Rolle „Eingang“/„Archiv“, Name und Pfad, Offline-Wurzeln ausgegraut und nicht wählbar,
  Vorbelegung erster erreichbarer Eingang → Archiv, Hinweis „unter Archiv „…““ bzw. „gesperrt“. Lädt bei
  `ActiveLibraryChangedMessage`/`LibraryRootsChangedMessage` neu, behält gültige Auswahlen und verwirft eine Analyse,
  deren Wurzeln sich geändert haben. Der Bestätigungsdialog nennt den Wechsel „von Eingang … nach Archiv …“.
- Services: `SorterCatalog.RequireLocationAsync` findet die Wurzel (über `LibraryRoots`, längster Pfad) statt
  `Libraries.RootPath`; Analyse und Plan tragen `SourceRoot`, `Roots`, `TargetLibraryRoot`, `CrossesRoots`.
  `IFileSorterService.SetTargetRoot` prüft „Ordner existiert bereits“ gegen die Zielwurzel statt gegen den Quellordner.
  Die Katalog-Nachführung läuft unverändert über `IFileOperationService` (Zielordner-Kette mit `Folders.RootId` der
  Zielwurzel, FileIds stabil, FTS gepflegt, OperationBatch/FileOperation, Rückgängig).

### Geändert
- **Ziele außerhalb aller Wurzeln sind gesperrt** (ADR-0024), ebenso Ziele unter einer Offline-Wurzel: alle
  Plan-Einträge „ungültiges Ziel“ mit Begründung (`SorterPlan.TargetError`), Fehlerleiste statt Warnung, „Sortieren“/
  „Ausführen“ verschiebt nichts. Bisher nur Warnung — die Dateien fielen aus dem Katalog, Rückgängig vergab neue FileIds.
- Beim Aufräumen leerer Quellordner werden alle Wurzeln geschützt (Eingang wie Archiv), nicht nur die Archiv-Wurzel.
- Knopf „Bibliotheksordner“ entfällt (ersetzt durch die Wurzel-Auswahl); `SorterLibraryLocator` liefert die Wurzeln.

### Tests
- `tests/PhotoViewerV3.Tests`: 351 (+7 `Sorter/SorterRootsTests` — Analyse im Eingang, Zielwurzel-Prüfung für
  „Ordner existiert“, Sortieren Eingang → Archiv mit RootId/Zwischenordnern/Status-Filter/FTS/Protokoll/Undo, Flatten
  im Eingang + Undo, Ziel außerhalb gesperrt, Offline-Wurzel gesperrt, `SorterTargetCheck`).
- `tests/PhotoViewerV3.Wpf.Tests`: 126 (+5 `Sorter/SorterRootSelectionTests` — Vorbelegung, ohne Eingang, ohne
  Bibliothek, Synchronisation Auswahl/Pfad mit Sperre, Neuladen über Nachrichten).
- Rauchtest: neuer Schritt „Sortierer: Eingang → Archiv“ vor „Ordnerüberwachung“ (eigener Eingang
  `<Bibliothek>-Sortierer-Eingang`, Vorbelegung, gesperrtes Ziel, Sortieren nach `Archiv\Smoke-Sortiert` mit DB-/FTS-/
  Filter-Prüfung, Rückgängig, Flatten im Eingang + Rückgängig, Eingang entfernen → Auswahl lädt neu); 20/20 OK,
  0 WPF-Binding-Fehler.

## [0.2.15] — 2026-10-19

### Geändert
- `MainWindow`, `ArchiveTargetDialog`, `ExtractArchiveDialog` und `TagPickerDialog` erben von `Shell/Dialogs/AppFluentWindow`
  (offener Punkt aus 0.2.13) — alle Fenster der App sind gegen das frühe Schließen vor dem ersten Zeichnen geschützt.
  CLAUDE.md: Regel für neue Fenster.
- Rauchtest, Schritt „Thumbnails“: wartet zuerst auf die geladene Galerieseite; unter Last war die Seite nach dem
  Fresh-Scan noch leer und der Schritt meldete „0/42“ (Befund aus den Läufen der Agenten zu 0.2.12/0.2.13).

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

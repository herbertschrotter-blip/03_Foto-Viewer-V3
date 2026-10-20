# ADR-0026: Speicherort von Katalog-DB und Thumbnail-Cache ist wählbar; Umzug per geprüfter Kopie und Neustart

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** [ADR-0003](0003-catalog-modell.md) (zentrale DB), [ADR-0009](0009-cache-strategie.md) (Thumbnails lokal), [ADR-0025](0025-scan-sperre-fuer-datei-operationen.md) (Scan-Sperre)

## Kontext

Katalog (`library.db` + `-wal`/`-shm`) und Thumbnail-Cache lagen fest unter `%LocalAppData%\PhotoViewerV3`. Bei
500k Dateien wachsen beide auf viele GB; der Benutzer will sie auf ein anderes lokales Laufwerk legen. Der Pfad kann
nicht in der DB selbst stehen, weil er gebraucht wird, bevor die DB geöffnet wird. Viele Singletons kennen den Pfad
(`IAppDataPaths`, `LibraryContextFactory`-Connection-String, `GalleryPageSource` mit dauerhaft offener Verbindung,
`ThumbnailService`); unter Windows lässt sich eine geöffnete Datei nicht löschen. Während des Kopierens darf niemand
mehr in die alte DB schreiben, sonst gehen Änderungen verloren.

## Entscheidung

1. **Persistenz:** `database-location.json` in der App-Wurzel (`%LocalAppData%\PhotoViewerV3` bzw.
   `PHOTOVIEWERV3_APPDATA`) mit `DatabaseFolder` (null = Standard) und `PendingDeletes`. `AppDataPaths.Resolve` liest
   sie beim Start; Logs und die Datei selbst bleiben immer in der App-Wurzel. Eine beschädigte Datei oder ein
   gewählter Ordner ohne `library.db` (Laufwerk nicht verbunden) bricht den Start mit Meldung ab — nie still am
   Standardort einen leeren Katalog anlegen.
2. **Thumbnails ziehen mit** (Herberts Entscheidung): `thumbs\` liegt immer neben `library.db`.
3. **Umzug** (`Services/Database/Relocation/DatabaseRelocationService`):
   Ziel prüfen (vollständiger Pfad, nicht der aktuelle Ordner, kein Netzlaufwerk/UNC, nicht im Thumbnail-/Log-Ordner,
   nicht in einer Bibliothekswurzel, beschreibbar per Schreibprobe, Platz ≥ (DB + WAL + Thumbnails) × 1,1 + 50 MB,
   OneDrive nur mit Warnung) → ruhigstellen (Ordnerüberwachung stoppen, alle Bibliotheken über
   `IScanGate.AcquireForFileOperationAsync` belegen, Thumbnail-Worker pausieren, höchstens 30 s warten; dann
   `IDatabaseAccessGate.Suspend` an der Context-Factory, `ClearAllPools`, `BEGIN IMMEDIATE` als Schreiber-Barriere) →
   `wal_checkpoint(TRUNCATE)`, Zeilen je Tabelle zählen, **SQLite-Backup-API** in `library.db.part` → umbenennen →
   `integrity_check` = „ok“ und gleiche Zeilenzahl je Tabelle → Thumbnails kopieren (`.tmp` ausgelassen, einzelne
   nicht lesbare übersprungen, „Platte voll“ bricht ab) → neuen Ort speichern (atomar). Erst dieser letzte Schritt
   schaltet um.
4. **Vorhandener Katalog im Ziel:** „Übernehmen“ (nur gültiger Katalog; nichts wird kopiert, der bisherige bleibt
   erhalten) oder „Ersetzen“ (vorhandene `library.db`/`-wal`/`-shm`/`thumbs` werden in `….ersetzt-{Zeitstempel}`
   umbenannt und beim nächsten Start gelöscht). Verwaiste `-wal`/`-shm` ohne DB werden immer beiseitegelegt.
5. **Fehler/Abbruch:** jeder angelegte Zielpfad steht vorher auf einem Rücknahme-Stapel; danach wird das Ziel
   aufgeräumt, Beiseitegelegtes zurückbenannt, Sperre/Pause/Überwachung aufgehoben. Die App arbeitet weiter mit dem
   alten Ort; die Einstellungsseite meldet den Grund über `IDialogService`.
6. **Neustart statt Umschalten zur Laufzeit:** nach Erfolg bleibt alles gesperrt, die App startet sich neu
   (`IAppRestartService`). Die alten Dateien (DB, `-wal`, `-shm`, alter `thumbs\`) löscht der nächste Start im
   Hintergrund (`DatabaseLocationStore.RunPendingDeletesAsync`, mit Wiederholung, nie etwas vom aktiven Ort).

### Ergänzung 0.2.21 (2026-10-20): Schreiber vor der Sperre, eine Dateiliste, zurückgestellte Vorschaubilder

7. **Sperre greift beim Zugriff, nicht nur beim Erzeugen:** jeder Kontext der `LibraryContextFactory` trägt einen
   Gate-Interceptor, der bei gesetzter Sperre `SaveChanges`, neue Transaktionen und Non-Query-Befehle
   (`ExecuteSqlRaw`, `ExecuteUpdate/Delete`) mit `DatabaseSuspendedException` abweist — auch für Kontexte, die vor der
   Sperre erzeugt wurden (Favorit im selben Augenblick). Lesen bleibt für solche Kontexte erlaubt; neue Kontexte
   verweigert `Create` wie bisher.
8. **Schreibsperre wird gehalten:** die `BEGIN IMMEDIATE`-Barriere wird nicht mehr sofort zurückgerollt, sondern bleibt
   auf einer eigenen Verbindung offen — bis zur Rücknahme bzw. nach Erfolg bis zum Prozessende (der Dienst hält die
   Referenz; `Dispose` gibt sie frei). Damit erreicht auch eine rohe Verbindung, die schon vor der Sperre offen war,
   die alte Datei nicht mehr (`SQLITE_BUSY` nach busy_timeout). Leser und die Backup-API arbeiten im WAL-Modus weiter.
   Der Checkpoint vor der Kopie ist deshalb `PASSIVE` statt `TRUNCATE` (TRUNCATE würde auf die eigene Sperre warten);
   die Backup-API kopiert den WAL-Inhalt ohnehin mit.
9. **Thumbnail-Ordner einmal durchlaufen:** `CheckTargetAsync` erstellt die Dateiliste (`ThumbnailInventory`) und
   merkt sie sich; `RelocateAsync` übernimmt sie (ohne vorherige Prüfung: ein eigener Durchlauf), gleicht sie nach der
   Pause der Thumbnail-Worker mit der DB ab (Zeilen mit `GeneratedAtUtc`/`LastAttemptAtUtc` ab Beginn des Durchlaufs
   − 2 s, deren Datei existiert) und prüft den Platz mit der endgültigen Liste und frisch gelesenem freiem Platz erneut.
   Kopiert wird nach der Liste; seither entfernte Dateien gelten nicht als Fehler. „Übernehmen“ durchläuft den Ordner
   gar nicht. Annahme: Vorschaubild-Dateien entstehen nur über den Thumbnail-Dienst (der die Zeile nach der Datei
   schreibt); eine fremde Datei, die nach der Prüfung ohne Zeile in den Ordner gelegt wird, zieht nicht mit.
10. **Vorschaubilder nach Rücknahme:** während der Pause angeforderte Vorschaubilder werden je FileId zurückgestellt
    (die neueste Anforderung gilt) und von `Resume` sofort nachgereicht — nicht erst mit dem nächsten Seitenwechsel.

## Begründung

- Neustart ist die einfachere, robuste Variante: jeder Dienst startet sauber mit dem neuen Pfad; keine Invalidierung
  von Caches, offenen Verbindungen (`GalleryPageSource`) oder gemerkten Pfaden in einem Dutzend Singletons, kein
  halb umgestellter Zustand. Ein Umzug ist selten; ein paar Sekunden Neustart sind akzeptabel.
- Löschen erst im neuen Prozess: die alte Instanz hält beim Neustart noch Handles auf die alte DB.
- Backup-API statt roher Dateikopie: konsistenter Schnappschuss inklusive WAL-Inhalt; `-wal`/`-shm` werden nie
  einzeln kopiert. `VACUUM INTO` wäre gleichwertig, kompaktiert aber zusätzlich — für einen Umzug unnötig.
- Die Factory-Sperre deckt Schreiber ab, die keine Scan-Sperre kennen (Favorit, Bewertung, Tags, Einstellungen):
  sie bekommen eine verständliche Ausnahme statt still in eine DB zu schreiben, deren Kopie schon läuft.

## Konsequenzen

### Positiv
- Katalog und Cache lassen sich auf ein größeres/schnelleres lokales Laufwerk legen, auch zurück an den Standardort.
- Kein Datenverlust bei Fehlern: umgeschaltet wird erst nach geprüfter Kopie; das Original bleibt bis zum Neustart.

### Negativ
- Umzug erfordert einen Neustart; während des Kopierens (bei 500k Vorschaubildern Minuten) ist das Hauptfenster
  gesperrt.
- Bis zum nächsten Start belegen alte und neue Dateien doppelt Platz.

### Neutral
- Netzlaufwerke bleiben ausgeschlossen (WAL braucht lokales Dateisystem, PLAN Kap. 11) — ADR-0009 „Cache lokal“ gilt
  weiter, nur nicht mehr zwingend unter `%LocalAppData%`.
- Thumbnail-Dateien bleiben FileId-basiert; da die DB 1:1 kopiert wird, passen sie am neuen Ort ohne Neuberechnung.

## Alternativen

- **Factory zur Laufzeit umstellen:** verworfen — zu viele Stellen mit gemerkten Pfaden/Verbindungen, schwer testbar,
  alte Datei bis zum Prozessende trotzdem gesperrt.
- **Pfad in der Registry oder `appsettings.json` neben der EXE:** verworfen — Registry ist schwer zu prüfen/zu
  reparieren, neben der EXE ist der Ort bei portabler Auslieferung nicht immer beschreibbar.
- **Thumbnail-Cache bleibt lokal / eigener Speicherort:** von Herbert zugunsten „zieht mit“ entschieden.
- **Alte DB als Backup behalten:** von Herbert zugunsten „löschen“ entschieden (Ausnahme „Übernehmen“: dort wird
  nichts kopiert, also bleibt der bisherige Katalog erhalten).

## Referenzen

- `src/PhotoViewerV3.Services/Infrastructure/AppDataPaths.cs`, `DatabaseLocationStore.cs`
- `src/PhotoViewerV3.Services/Database/Relocation/*` (ab 0.2.21 mit `ThumbnailInventory.cs`), `LibraryContextFactory.cs`
  (`IDatabaseAccessGate`, Gate-Interceptor), `Thumbnails/ThumbnailService.cs` (zurückgestellte Anforderungen)
- `src/PhotoViewerV3.Wpf/Settings/SettingsViewModel.cs`, `Shell/AppRestartService.cs`, `App.xaml.cs`
- Tests: `tests/PhotoViewerV3.Tests/Database/DatabaseRelocationServiceTests.cs`, `DatabaseLocationStoreTests.cs`,
  `tests/PhotoViewerV3.Wpf.Tests/Settings/SettingsRelocationTests.cs`

# ADR-0028: „Neu aufbauen“ ersetzt den Katalog in einer Transaktion — kein Datenverlust bei Abbruch oder Absturz

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Claude (Auftrag Herbert Schrotter; Variante ohne Rückfrage gewählt, siehe Begründung)
- **Bezogen auf:** ADR-0023 (löst dort Ablauf und Konsequenz „Abbruch zwischen Leeren und Zurückschreiben verliert die
  Daten“ ab), ADR-0027 (Kategorien), ADR-0004 (Raw-SQL für Bulk), ADR-0011 (FTS-Pflege), ADR-0005 (keine Migration nötig)

## Kontext

Bis 0.2.20 lief „Neu aufbauen“ (`ScanMode.ClearAndRescan`) so: Benutzerdaten in TEMP-Tabellen einer eigenen Verbindung
sichern → Katalog der Bibliothek leeren und **committen** (`LibraryService.ClearAsync`) → alle Wurzeln enumerieren
(bei großen oder Netz-Bibliotheken Minuten) → Bulk-Insert → Benutzerdaten zurückschreiben → Suchindex. Brach der Lauf
nach dem Leeren ab (Abbrechen-Knopf, Ausnahme, App-Absturz, Strom weg), war der Katalog leer und Favoriten,
Bewertungen, Tags und Kategorie-Werte (ADR-0027 — sie werden seit 0.2.20 mitgesichert) verloren: die Sicherung lebte
nur im Speicher dieser Verbindung, der Rollback eines Persistenzfehlers löschte nur die neuen Zeilen. Genau „Neu
aufbauen“ ist aber der Notnagel bei einem verkorksten Katalog und darf die Arbeit des Benutzers nie wegwerfen.

## Entscheidung

Der Katalog wird erst **nach** der Enumeration angefasst, und zwar in **einer** Schreibtransaktion (BEGIN IMMEDIATE)
auf einer Verbindung (`Services/Scanning/FullScanCatalogWriter`): Benutzerdaten in TEMP-Tabellen sichern
(`UserDataCarryOver.Capture`) → alte Thumbnails-, Files- und Folders-Zeilen löschen (Tag- und Kategorie-Zuweisungen per
Kaskade) → Folders + Files einfügen → Benutzerdaten auf die neuen FileIds zurückschreiben → Suchindex per `'rebuild'` →
Commit. Erst danach wird der Thumbnail-Cache-Ordner der Bibliothek gelöscht. Fresh nutzt denselben Schreibpfad ohne
Sichern/Löschen. Keine Schema-Änderung.

Weil zwischen dem Lesen der Start-Ids (vor der Enumeration) und dem Schreiben jetzt die ganze Enumeration liegt, prüft
der Schreibpfad die nächsten freien Ids unter der Schreibsperre erneut und verschiebt die Zeilen (samt ParentId/FolderId)
nach hinten, falls ein Scan oder Datei-Batch einer anderen Bibliothek sie inzwischen vergeben hat — sonst verwürfe
`INSERT OR IGNORE` die kollidierenden Zeilen still (das Fenster gab es vorher auch, nur kürzer).

## Begründung

Gewählt wurde die zweite der beiden Möglichkeiten aus dem Auftrag — „alte Zeilen erst nach Erfolg ersetzen, in einer
Transaktion“ — statt „persistente Sicherung mit Wiederaufnahme“:

- **Atomar durch SQLite selbst:** Bis zum Commit sehen alle anderen Verbindungen (WAL) den alten Katalog; ein
  Rollback (Abbruch, Ausnahme) oder ein Absturz (SQLite verwirft beim nächsten Öffnen die WAL-Frames ohne
  Commit-Markierung) hinterlässt ihn Byte für Byte unverändert. Es gibt keinen Zwischenzustand.
- **Keine Wiederaufnahme-Logik, die selbst fehlschlagen kann:** Eine persistente Sicherung braucht eine neue Tabelle
  (Migration), einen Zustand „Sicherung offen“, eine Erkennung beim Start bzw. nächsten Scan, Regeln für den Fall, dass
  der Benutzer danach „Aktualisieren“ statt „Neu aufbauen“ startet oder die Bibliothek löscht, und Tests für jede
  dieser Kanten. Und auch mit Sicherung wäre der Katalog nach einem Absturz bis zur Wiederaufnahme leer — die Galerie
  zeigte nichts, die Ordnerüberwachung würde ins Leere gleichen.
- **Mehr Benutzerdaten erhalten:** Weil gesichert wird, unmittelbar bevor die alten Zeilen fallen, zählen auch
  Bewertungen oder Tags, die der Benutzer während der Enumeration noch vergibt — der alte Katalog bleibt bis dahin
  bedienbar (vorher war er in dieser Zeit leer).
- **Kosten gleichartig wie bisher:** Der Bulk-Insert lief schon bisher in einer Transaktion über alle Files; neu im
  selben Block sind Löschen (ohne zeilenweises FTS-`'delete'`, der `'rebuild'` am Ende ersetzt den ganzen Index) und
  Zurückschreiben. Gemessen (synthetisch, schlechtester Fall: jede Datei mit Bewertung, jede zehnte mit zwei Tags):
  siehe CHANGELOG 0.2.24 (`ClearAndRescanUserDataTests.Mengen_Messung_synthetischer_Katalog`,
  `LUMORA_CARRYOVER_ROWS=500000`).
- **Suchindex:** Die CLAUDE.md-Regel „vor DELETE `'delete'` mit den alten Werten“ dient der Konsistenz zwischen
  `Files` und `FilesFts`. Hier wird der Index in derselben Transaktion komplett per `'rebuild'` aus `Files` neu
  aufgebaut (wie der Fresh-Scan es schon immer tat); beim Commit ist er konsistent, bei einem Rollback unverändert.
  Gezieltes `'delete'` der alten plus Einfügen der neuen Zeilen wären zwei Indexdurchläufe über die Bibliothek statt
  einem — für den Normalfall (eine Bibliothek) teurer. Der Helfer `LibraryCatalogCleanup.DeleteLibraryRowsBeforeFtsRebuild`
  ist deshalb nur für diesen Pfad gedacht und so benannt.

## Konsequenzen

### Positiv
- Abbruch, Ausnahme oder Absturz an jeder Stelle von „Neu aufbauen“ verlieren weder Katalog noch Favoriten,
  Bewertungen, Tags oder Kategorie-Werte; FileIds, Suchindex und Thumbnail-Zeilen bleiben die alten.
- Während der Enumeration bleibt die Galerie mit dem alten Katalog benutzbar.
- Fresh-Scan: ein Fehler mitten in der Persistenz braucht keinen eigenen Aufräum-Rollback mehr (`RollbackPartialScan`
  entfällt).

### Negativ
- Die Schreibtransaktion am Ende ist länger als die bisherigen Einzeltransaktionen (Löschen + Einfügen + Zurückschreiben
  + Suchindex am Stück). Andere Schreiber (Bewertung setzen, Tags, Thumbnail-Status) warten in dieser Zeit bis zu
  `busy_timeout` = 5 s und scheitern bei sehr großen Bibliotheken dann mit „database is locked“ — vorher traf das nur
  den Bulk-Insert. Ein Fehlschlag dort betrifft eine einzelne Aktion des Benutzers, nicht den Katalog.
- Die WAL-Datei wächst während der Transaktion um alte und neue Seiten (bei 500k Dateien einige hundert MB) und wird
  erst beim nächsten Checkpoint zurückgesetzt.
- Ein Absturz genau zwischen Commit und Löschen des Thumbnail-Cache-Ordners lässt alte Cache-Dateien liegen — toter
  Platz, kein Fehler (FileIds werden nie wiederverwendet), der nächste „Neu aufbauen“ räumt ihn.
- Weil die Galerie den alten Katalog während der Enumeration weiter zeigt, rendert der Thumbnail-Dienst dort weiter
  Vorschaubilder alter FileIds. Eine Thumbnails-Zeile, deren Schreiben erst nach dem Commit durchkommt, weist der
  Fremdschlüssel ab (der Dienst protokolliert den Fehler); eine danach geschriebene Cache-Datei bleibt als toter Platz.
- Der `'rebuild'` indexiert alle Bibliotheken neu; mit mehreren großen Bibliotheken verlängert das die Sperre.

### Neutral
- Die Phase „alte Katalogdaten werden entfernt…“ erscheint jetzt nach der Enumeration, unmittelbar vor „Katalog wird
  geschrieben…“.
- `UserDataCarryOver` öffnet keine eigene Verbindung mehr, sondern arbeitet in der Transaktion des Aufrufers;
  `BulkScanWriter` und `FtsIndexer` nehmen dafür optional eine Transaktion entgegen.
- `ILibraryService.DeleteThumbnailCacheAsync` macht das Löschen des Cache-Ordners ohne Katalogzeilen zugänglich.

## Alternativen

- **Persistente Sicherungstabelle, Wiederaufnahme beim nächsten Scan/Start** → verworfen weil: Schema-Migration,
  zusätzlicher Wiederherstellungszustand mit eigenen Kanten (siehe Begründung), Katalog bis zur Wiederaufnahme leer;
  SQLite liefert die gewünschte Atomarität bereits.
- **Tabellen-Tausch (neue Zeilen in Schatten-Tabellen, dann umbenennen)** → verworfen weil: `Files`/`Folders` teilen
  sich alle Bibliotheken; ein Tausch je Bibliothek ginge nur über Kopieren der fremden Zeilen, die FTS-External-Content-
  Tabelle und die Fremdschlüssel (FileTags, FileCategoryValues, Thumbnails) hängen am Tabellennamen.
- **Nur die Reihenfolge ändern (erst enumerieren, dann wie bisher leeren und einfügen, mehrere Transaktionen)** →
  verworfen weil: verkürzt das Fenster, schließt es aber nicht (Absturz zwischen Leeren-Commit und Zurückschreiben).

## Referenzen

- [src/Lumora.Services/Scanning/FullScanCatalogWriter.cs](../../src/Lumora.Services/Scanning/FullScanCatalogWriter.cs)
- [src/Lumora.Services/Scanning/UserDataCarryOver.cs](../../src/Lumora.Services/Scanning/UserDataCarryOver.cs)
- [src/Lumora.Services/Scanning/ScanService.cs](../../src/Lumora.Services/Scanning/ScanService.cs) (`RunFullScanAsync`)
- [tests/Lumora.Tests/Scanning/ClearAndRescanFailureTests.cs](../../tests/Lumora.Tests/Scanning/ClearAndRescanFailureTests.cs)
  (Abbruch je Phase, Ausnahme je Phase, simulierter Absturz mit Wiederaufnahme, Kategorien)

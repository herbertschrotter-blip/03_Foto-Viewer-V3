# ADR-0023: „Neu aufbauen“ übernimmt Favoriten, Bewertungen und Tags über den Pfad

- **Status:** Accepted (Ablauf „vor dem Leeren in einer eigenen Verbindung sichern, nach dem Bulk-Insert
  zurückschreiben“ und Konsequenz „Abbruch zwischen Leeren und Zurückschreiben verliert die Daten“ abgelöst durch
  ADR-0028, 0.2.24; Schlüssel und Mengenlogik gelten weiter)
- **Ergänzt durch:** [ADR-0028](0028-neu-aufbauen-in-einer-transaktion.md) — Sichern, Ersetzen und Zurückschreiben in
  einer Transaktion nach der Enumeration; Kategorie-Werte (ADR-0027) gehören zu den übernommenen Benutzerdaten
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0019 (Incremental-Scan, löst dort den Punkt „ClearAndRescan … Favoriten und Bewertungen gehen
  verloren“ ab), ADR-0020 (mehrere Wurzeln), ADR-0021 (Tags), ADR-0004 (Raw-SQL für Bulk)

## Kontext

„Neu aufbauen“ (`ScanMode.ClearAndRescan`) löscht die Katalogzeilen einer Bibliothek und scannt frisch; dabei entstehen
neue FileIds. Bisher gingen damit alle Benutzerdaten verloren, die an der FileId hängen: `Files.IsFavorite`,
`Files.Rating` und die Zuweisungen in `FileTags` (ON DELETE CASCADE). Weitere benutzerbezogene Spalten gibt es im
Schema nicht (Width/Height/DurationMs/Codec sind abgeleitete Metadaten, Thumbnails ein Cache, `FileOperations` ein
Protokoll mit eigenen Pfaden). „Neu aufbauen“ ist der Notnagel bei einem verkorksten Katalog — gerade dann soll er die
Arbeit des Benutzers nicht wegwerfen. Bibliotheken bis 500k Dateien, Schreibdisziplin laut CLAUDE.md (Single-Writer,
lange Läufe in einer Transaktion).

## Entscheidung

`UserDataCarryOver` (Services/Scanning) sichert vor dem Leeren alle Dateien der Bibliothek mit Favorit, Bewertung oder
mindestens einem Tag in TEMP-Tabellen einer eigenen Verbindung und schreibt sie nach dem Bulk-Insert des Neuscans —
vor dem FTS-Aufbau, in einer Schreibtransaktion — auf die neuen FileIds zurück. Schlüssel ist die Identität des
Incremental-Scans: voller Ordnerpfad + Dateiname, ohne Groß-/Kleinschreibung (Unicode, wie `OrdinalIgnoreCase`, über
eine registrierte SQLite-Funktion). Keine Plausibilitätsprüfung über Größe/Änderungszeit.

## Begründung

- **Gleiche Identität wie „Aktualisieren“:** Dort behält eine an Ort und Stelle geänderte Datei (andere Größe/Zeit)
  ihre FileId und damit Favorit/Bewertung/Tags. Würde „Neu aufbauen“ zusätzlich Größe+Zeit verlangen, verlöre eine
  nachbearbeitete Datei dort ihre Daten, beim Aktualisieren aber nicht — zwei Regeln für dieselbe Frage.
- **Voller Ordnerpfad statt Wurzel-Id + relativer Pfad:** Wurzeln bleiben beim Neuaufbau unverändert
  (`LibraryRoots` wird nicht geleert), der volle Pfad ist über alle Wurzeln eindeutig und steht direkt in
  `Folders.Path`; gleichnamige Dateien in verschiedenen Ordnern oder Wurzeln bleiben getrennt.
- **Mengenbasiert statt Schleife:** INSERT…SELECT und UPDATE…FROM über TEMP-Tabellen mit Primärschlüsseln; die
  Zuordnung wird von den gesicherten Zeilen getrieben (Ordner per Schlüssel, Datei per `IX_Files_Folder_Name` mit
  exaktem Namen, nur der Rest per Groß-/Kleinschreibung-Vergleich im selben Ordner). Gemessen (synthetisch,
  schlechtester Fall: jede Datei hat eine Bewertung, jede zehnte zwei Tags): 500 000 Dateien — Sichern 1,9 s,
  Zurückschreiben 2,6–3,9 s; 200 000 Dateien — 0,8 s / 1,0 s.
- **Keine Schema-Migration:** TEMP-Tabellen leben nur in der Verbindung (`temp_store=MEMORY`, Pooling aus).

## Konsequenzen

### Positiv
- Favoriten, Bewertungen und Tags überleben „Neu aufbauen“ für alle Dateien, die noch am selben Ort liegen — auch
  über mehrere Wurzeln und bei einer Umbenennung nur in Groß-/Kleinschreibung.
- Thumbnails und Suchindex verhalten sich wie bisher (Thumbnails neu, FTS komplett neu; die Rückschreibung ändert keine
  FTS-Spalte).

### Negativ
- Dateien, die nach dem Neuscan nicht mehr am selben Pfad liegen (gelöscht, verschoben, umbenannt, zuvor „fehlt“ und
  weiterhin fehlend), verlieren ihre Daten.
- Bricht der Lauf zwischen Leeren und Zurückschreiben ab (Abbruch während der Enumeration, Absturz), sind die Daten
  weg — wie vor 0.2.12, weil der Katalog in diesem Moment ohnehin leer ist. Schlägt das Zurückschreiben selbst fehl,
  rollt der Scan wie bei jedem Persistenzfehler komplett zurück.
- Im schlechtesten Fall (alle 500k Dateien mit Daten) halten die TEMP-Tabellen einen zweistelligen MB-Betrag im
  Speicher.

### Neutral
- Auf case-sensitiven Ordnern (Schlüsselkollision) gewinnt die vorhandene vor der „fehlt“-Zeile, sonst die ältere.
- Ein während des Scans gelöschtes Tag wird nicht zurückgeschrieben (sonst Fremdschlüsselverletzung).
- Die Tabelle `Tags` wird nie angefasst; `FileOperations` behalten ihre alten FileIds (Undo arbeitet über Pfade).

## Alternativen

- **Größe + Änderungszeit als Plausibilitätsprüfung** → verworfen weil: weicht von der Identität des Incremental-Scans
  ab (siehe Begründung).
- **Sicherung in C#-Listen und zeilenweises UPDATE** → verworfen weil: 500k Einzel-Updates, CLAUDE.md verlangt
  mengenbasierte Bulk-Pfade.
- **Persistente Sicherungstabelle (übersteht Absturz)** → verworfen weil: Schema-Migration für einen seltenen Notfall;
  besser wäre, den Katalog erst nach der Enumeration zu leeren (siehe CHANGELOG „Offen“).
- **FileIds beim Neuaufbau beibehalten** → verworfen weil: genau das leistet „Aktualisieren“; „Neu aufbauen“ soll den
  Katalog bewusst frisch aufbauen (neue Ids, neuer Thumbnail-Cache).

## Referenzen

- [src/PhotoViewerV3.Services/Scanning/UserDataCarryOver.cs](../../src/PhotoViewerV3.Services/Scanning/UserDataCarryOver.cs)
- [src/PhotoViewerV3.Services/Scanning/ScanService.cs](../../src/PhotoViewerV3.Services/Scanning/ScanService.cs) (`RunFullScanAsync`)
- [tests/PhotoViewerV3.Tests/Scanning/ClearAndRescanUserDataTests.cs](../../tests/PhotoViewerV3.Tests/Scanning/ClearAndRescanUserDataTests.cs)
  (Mengen-Messung mit `PV3_CARRYOVER_ROWS=500000`)

# ADR-0030: Schreiber warten asynchron auf einen gesperrten Katalog — keine verlorene Benutzeraktion

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Claude (Auftrag Herbert Schrotter, Variante ohne Rückfrage gewählt, siehe Begründung)
- **Bezogen auf:** ADR-0028 (eine Schreibtransaktion bei „Neu aufbauen“), ADR-0004 (EF Core CRUD + Raw-SQL), ADR-0023
  (Benutzerdaten über „Neu aufbauen“), ADR-0019 (Datei-Identität Ordnerpfad + Name)

## Kontext

Seit 0.2.24 (ADR-0028) ersetzt „Neu aufbauen“ den Katalog am Ende in **einer** Schreibtransaktion; bei 500 000 Dateien
dauert sie ≈ 30 s. Andere Schreiber — Bewertung/Favorit, Tags, Kategorie-Werte, Thumbnail-Status, Einstellungen — kamen
in dieser Zeit nicht an die Sperre. Gemessen (0.2.29, Sperre 40 s gehalten): eine Bewertung scheiterte nach **34 s** mit
„database is locked“ (busy_timeout 5 s + interne Wiederholung von Microsoft.Data.Sqlite bis CommandTimeout 30 s), nicht
nach 5 s wie in 0.2.24 vermutet. Schlimmer: Microsoft.Data.Sqlite arbeitet auch in seinen `*Async`-Methoden synchron —
die Galerie rief `SetRatingAsync` auf dem UI-Thread, das Fenster war bis zu 34 s eingefroren. Und selbst eine Wiederholung
allein reicht nicht: „Neu aufbauen“ vergibt **neue FileIds**; eine Bewertung, die auf das Commit wartet, träfe danach eine
Id, die es nicht mehr gibt, und ginge still verloren (im Test mit Wiederholung, aber ohne Umsetzung: rot).

## Entscheidung

1. **Zentrale Wiederholung** `Services/Database/SqliteBusyRetry`: jeder Versuch läuft auf dem Thread-Pool; nach
   SQLITE_BUSY/SQLITE_LOCKED wird asynchron gewartet und wiederholt, bis `MaxWait` (Standard **5 min**) abgelaufen ist —
   erst dann erreicht die Ausnahme den Aufrufer (Fehlermeldung wie bisher, nichts verschwindet still). Ein Versuch hält
   einen Pool-Thread höchstens ≈ busy_timeout (CommandTimeout des Versuchs 1 s). Wiederholt wird nur, was atomar
   gescheitert ist: Autocommit-Befehle und `BEGIN IMMEDIATE`.
2. **EF-Kontexte automatisch**: die `LibraryContextFactory` hängt einen `BusyRetryInterceptor` an jeden Kontext; er führt
   in den asynchronen Pfaden Autocommit-Schreibbefehle (SaveChanges mit einem Befehl, ExecuteUpdate/-Delete,
   ExecuteSql) und den Beginn von Transaktionen (SaveChanges mit mehreren Befehlen, `BeginTransactionAsync`) über die
   Wiederholung aus. Einstellungen, Tag-/Kategorie-Stammdaten, Thumbnail-Status und Datei-Operationen brauchen dafür
   keine eigene Zeile.
3. **Rohe Schreibpfade auf Benutzerdaten** (Favorit/Bewertung, Tag- und Kategorie-Zuweisungen) laufen über
   `Services/Catalog/CatalogFileTargets.WriteAsync`: Verbindung öffnen, **Ordnerpfad + Name der Ziel-Dateien vorher
   lesen** (Lesen ist im WAL nie gesperrt), `BEGIN IMMEDIATE` mit Wiederholung, unter der Sperre fehlende FileIds über
   Bibliothek + Ordnerpfad + Name auf die neuen Ids umsetzen, schreiben, Commit — alles auf dem Thread-Pool.
   `CatalogUpdateService` schreibt dafür jetzt Raw-SQL in einer Transaktion statt `ExecuteUpdateAsync` je Chunk.
4. **Sichtbar**: Wartende melden sich über `CatalogWriteWaitMessage`; die Statusleiste zeigt „Änderung wartet auf den
   Katalog…“ (Tooltip: wird danach automatisch gespeichert, App bis dahin nicht schließen).

## Begründung

- Wiederholung statt Sperren der Bedienung: Bewertung, Tags und Kategorien sind über Galerie, Lightbox, Menüs,
  Tastenkürzel und Dialoge verteilt — eine Sperre müsste überall greifen und hätte ein Zeitfenster (Klick kurz vor
  Beginn der Schreibphase). Die Wiederholung wirkt an einer Stelle und auch für andere lange Schreiber.
- Wiederholung statt eigener Warteschlange: die Wiederholung ist eine Warteschlange, die SQLite selbst verwaltet —
  Reihenfolge je Dienst bleibt durch dessen Single-Writer-Lock erhalten, Fehler kommen beim Aufrufer an, kein
  zusätzlicher Zustand, der beim Beenden verloren ginge.
- Dieselbe Semantik wie Microsoft.Data.Sqlite (es wiederholt intern ebenfalls Befehl bzw. BEGIN), nur asynchron und
  länger; Befehle innerhalb einer Transaktion halten die Sperre bereits und bleiben unberührt.
- Die Umsetzung der FileIds nutzt dieselbe Identität wie das Übernehmen der Benutzerdaten (ADR-0023/0019); ohne sie
  würde genau der Hauptfall — Bewertung in der Bibliothek, die gerade neu aufgebaut wird — still verloren gehen.

## Konsequenzen

### Positiv
- Keine verlorene Bewertung/Tag-/Kategorie-Zuweisung während „Neu aufbauen“, auch nicht durch neue FileIds.
- Der UI-Thread wartet nie mehr auf SQLite (auch nicht bei kurzer Konkurrenz, z. B. Thumbnail-Status).
- Hinweis in der Statusleiste statt eingefrorenem Fenster und Fehlermeldung nach 34 s.

### Negativ
- Jeder asynchrone EF-Schreibbefehl außerhalb einer Transaktion und jeder Transaktionsbeginn wechselt einmal auf den
  Thread-Pool (µs); Nachrichten der Dienste (FilesChanged, TagsChanged, …) kommen dadurch vom Pool-Thread — alle
  Empfänger dispatchen ohnehin selbst, sie laufen aber jetzt *nach* der Rückkehr des Aufrufs.
- Benutzerdaten-Schreiber lesen vorher Ordnerpfad + Name der Ziele (eine Abfrage je 500 Ids) und prüfen unter der
  Sperre, ob die Ids noch bestehen (eine weitere).
- Wartet eine Änderung, während die App beendet wird, ist sie verloren (Hinweis im Tooltip).

### Neutral
- `SqlitePragmaInterceptor` (busy_timeout) und `LibraryContextFactory` (DefaultTimeout, `SqliteBusyRetryOptions`)
  nehmen verkürzte Zeiten nur für Tests entgegen; produktiv bleiben busy_timeout 5000 und CommandTimeout 30 s.
- Synchrone EF-Pfade und rohe Schreibpfade der Scans (`IncrementalCatalogWriter`, `LibraryCatalogCleanup`) sind nicht
  umgestellt: sie laufen ohnehin auf Hintergrund-Threads und warten wie bisher bis ≈ 35 s.

## Alternativen

- **busy_timeout bzw. CommandTimeout hochsetzen:** eine Zeile, blockiert aber den aufrufenden Thread (UI) für die ganze
  Wartezeit und löst das FileId-Problem nicht → verworfen.
- **Bedienung während „Katalog wird geschrieben…“ sperren:** viele Bedienstellen, Zeitfenster, FileId-Problem bleibt →
  verworfen.
- **EF-ExecutionStrategy mit Wiederholung:** verträgt keine benutzergestarteten Transaktionen (FileOperationService)
  ohne Umbau aller Aufrufer und wiederholt nicht asynchron ohne Thread-Wechsel → verworfen.
- **Neuaufbau behält FileIds:** würde ADR-0028/0023 umbauen (Bulk-Insert mit Id-Zuordnung), großer Eingriff in den
  Scan → verworfen.

## Referenzen

- Code: `src/PhotoViewerV3.Services/Database/SqliteBusyRetry.cs`, `BusyRetryInterceptor.cs`, `LibraryContextFactory.cs`,
  `src/PhotoViewerV3.Services/Catalog/CatalogFileTargets.cs`, `src/PhotoViewerV3.Wpf/Shell/StatusBarViewModel.CatalogWriteWait.cs`
- Tests: `tests/PhotoViewerV3.Tests/Database/CatalogWriteRetryTests.cs`; Rauchtest-Schritt „Katalog gesperrt“
- CHANGELOG 0.2.24 (Offen), 0.2.29

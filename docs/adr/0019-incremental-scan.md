# ADR-0019: Incremental-Scan — Identität Ordner+Name, Missing statt Löschen, Mass-Missing-Guard

- **Status:** Accepted (Punkt „ClearAndRescan … Favoriten und Bewertungen gehen verloren“ abgelöst durch ADR-0023, 0.2.14)
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0003 (Catalog-Modell), ADR-0007 (Thumbnail-Scheduling), ADR-0009 (Cache-Strategie), ADR-0011 (FTS-Pflege), PLAN Kap. 12

## Kontext

Nach dem ersten Scan lebt eine Bibliothek weiter: neue Fotos kommen dazu, Dateien werden außerhalb der App
verschoben oder gelöscht, Videos nachbearbeitet. Der V0.1-Weg „Katalog leeren und neu scannen“ vergibt dabei neue
FileIds — und damit verlieren Thumbnail-Cache (FileId-basiert, ADR-0009), Favoriten, Bewertungen und die Referenzen im
Operationsprotokoll (ADR-0012) ihren Bezug. Zweites Problem (PLAN Kap. 12): Liegt die Bibliothek auf USB-Platte oder
NAS und ist beim Scan nicht angeschlossen, darf das nicht als „alle Dateien gelöscht“ im Katalog landen; auch ein
halb eingehängtes Laufwerk darf nicht zehntausende Dateien stillschweigend als fehlend markieren.

## Entscheidung

`ScanMode.Incremental` ist der Standard für „Aktualisieren“ einer bestehenden Bibliothek (`IncrementalScanRunner`):

- **Identität** einer Datei ist das Paar (Ordnerpfad, Dateiname), case-insensitiv; Ordner werden nach Pfad
  abgeglichen. Bekannte Datei mit anderer Größe oder Änderungszeit → Zeile aktualisiert (Id bleibt; das Thumbnail
  wird über `SourceModifiedAtUtc` ungültig und neu erzeugt). Unbekannte Datei → neue Zeile mit fortlaufender Id samt
  Ordnerkette. Nicht mehr vorhanden → `Missing = 1`: Zeile, Favorit, Bewertung und Thumbnail bleiben, die Galerie
  zeigt „fehlt“. Wieder vorhanden → `Missing = 0`.
- **Offline-Guard:** Vor dem Scan, alle 2000 Dateien, bei jedem IO-Fehler und vor jedem Persist-Batch prüft
  `RootAvailabilityProbe` den Root mit Timeout (lokal 1 s, UNC 3 s). Ist er nicht erreichbar, endet der Scan mit
  `ScanOutcome.LibraryOffline`: bereits geschriebene neue/geänderte Dateien bleiben (sie sind real), **nichts wird als
  fehlend markiert**.
- **Mass-Missing-Guard:** Fehlen mehr als 10 000 Dateien oder mehr als 20 % der vorher vorhandenen, endet der Scan
  mit `ScanOutcome.MassMissingBlocked` ohne Markierung; die Bibliotheksseite fragt ([Erneut prüfen] [Als fehlend
  markieren] [Abbrechen]) und wiederholt mit `ScanOptions.ConfirmMassMissing`.
- **Persistenz** in Batches (je Batch eigene Transaktion und Root-Recheck), FTS-Pflege gezielt je Insert/Update statt
  Rebuild; Missing-Markierungen zuletzt. Ergebnis als `ScanResult` mit `Mode`, `Outcome` und Diff-Zählern (`Added`,
  `Modified`, `MarkedMissing`, `Reappeared`, `FoldersAdded`, `MissingDetected`), dazu `FilesChangedMessage`s für die
  Module (ADR-0017).
- `ScanMode.ClearAndRescan` bleibt als „Neu aufbauen“ mit ausdrücklicher Warnung (Favoriten und Bewertungen gehen
  verloren); `Fresh` nur für leere Kataloge.

## Begründung

- Stabile FileIds sind die Voraussetzung dafür, dass der Thumbnail-Cache (bei 500k Dateien viele Stunden
  Rechenzeit), Favoriten, Bewertungen und das Operationsprotokoll über Scans hinweg gültig bleiben.
- (Ordner, Name) ist ohne Lesen der Dateien verfügbar und bei Fotos praktisch eindeutig; eine Hash-Identität würde
  jede Datei lesen — auf NAS und USB nicht vertretbar.
- „Missing statt löschen“ ist reversibel: Dateien kommen zurück (Laufwerk wieder da, Datei zurückverschoben), und die
  Metadaten sind noch da.
- Der Offline-Guard und der Schwellwert folgen PLAN Kap. 12 wörtlich — ein Katalog, der nach einem Scan ohne
  Laufwerk leer aussieht, zerstört das Vertrauen in die App.

## Konsequenzen

### Positiv
- „Aktualisieren“ ist der normale Weg nach Änderungen auf der Platte; Favoriten, Bewertungen und Vorschaubilder
  überleben.
- Offline-Laufwerke und Teil-Ausfälle führen zu Dialogen statt zu Datenverlust im Katalog.
- Das Szenario ist durch Tests (`IncrementalScanTests`, `RootAvailabilityProbeTests`, Guard-Theorie) und den
  Rauchtest (neue, fehlende, wieder vorhandene Datei) abgesichert.

### Negativ
- Umbenannte oder verschobene Dateien gelten als „neu + fehlend“ (keine Inhalts-Identität); Favorit und Bewertung
  wandern nicht mit. Eine spätere Hash- oder Größe+Zeit-Heuristik kann das ergänzen.
- Jeder Incremental-Scan läuft den kompletten Baum (kein FileSystemWatcher); bei 500k Dateien auf HDD dauert das
  Minuten.
- Missing-Zeilen bleiben, bis die Datei zurückkommt, in den Papierkorb verschoben oder der Katalog neu aufgebaut wird.

### Neutral
- Der Katalog-Snapshot (Ordnerpfade und Dateinamen) liegt während des Scans im Speicher — bei 500k Dateien ein
  zweistelliger MB-Betrag.
- Ids werden fortlaufend über `MAX(Id)`/`sqlite_sequence` vergeben; nach Papierkorb-Löschungen können Ids
  wiederverwendet werden, deshalb invalidieren Galerie und Thumbnail-Service ihren In-Memory-Zustand bei
  `FilesChangedMessage(Removed/Added)` und `ScanCompletedMessage`.

## Alternativen

- **Hash-basierte Identität (pHash/SHA)** → verworfen weil: liest jede Datei; als spätere Ergänzung für
  Verschiebungen denkbar (Phase 8 Duplikate).
- **FileSystemWatcher statt Re-Scan** → verworfen weil: unzuverlässig auf NAS/USB, Lücken bei geschlossener App,
  Puffer-Überlauf bei Massenänderungen; als Ergänzung (Auslöser für „Aktualisieren“) weiterhin möglich.
- **Fehlende Dateien löschen** → verworfen weil: irreversibler Metadaten- und Cache-Verlust, Offline-Laufwerke würden
  den Katalog leeren.
- **Nur ClearAndRescan (V0.1)** → verworfen weil: neue Ids bei jedem Scan machen Cache und Favoriten wertlos.

## Referenzen

- [src/PhotoViewerV3.Services/Scanning/IncrementalScanRunner.cs](../../src/PhotoViewerV3.Services/Scanning/IncrementalScanRunner.cs)
- [src/PhotoViewerV3.Services/Scanning/RootAvailabilityProbe.cs](../../src/PhotoViewerV3.Services/Scanning/RootAvailabilityProbe.cs)
- [src/PhotoViewerV3.Services/Scanning/MassMissingGuard.cs](../../src/PhotoViewerV3.Services/Scanning/MassMissingGuard.cs)
- [src/PhotoViewerV3.Services/Scanning/ScanModels.cs](../../src/PhotoViewerV3.Services/Scanning/ScanModels.cs) (`ScanMode`, `ScanOutcome`, `ScanResult`)
- [src/PhotoViewerV3.Wpf/Library/LibraryViewModel.cs](../../src/PhotoViewerV3.Wpf/Library/LibraryViewModel.cs) (Dialoge des Guards)
- [PLAN.md Kap. 12](../../PLAN.md), [Spike #06 Storage Detection](../spikes/06-storage-detection.md)

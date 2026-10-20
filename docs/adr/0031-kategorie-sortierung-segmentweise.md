# ADR-0031: Kategorie-Sortierung segmentweise je Wert über einen abdeckenden Index

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** [ADR-0027](0027-kategorien-mit-festen-werten.md) (Kategorien, Nachtrag 0.2.23: serverseitige
  Sortierung), [ADR-0010](0010-ui-datenmodell-paging.md) (Paging, Reverse-Trick), [ADR-0005](0005-migration-disziplin.md)
  (Migrationen), Messung [Spike #08](../spikes/08-suche-sortierung.md)

## Kontext

Seit 0.2.23 sortiert die Galerie die ganze Trefferliste nach dem Wert einer Kategorie in **einer** Abfrage: alle
Dateien der Bibliothek, je Datei ein Schlüssel-Lookup in `FileCategoryValues` (LEFT JOIN) und `CategoryValues`, danach
eine Sortierung aller Zeilen mit `LIMIT/OFFSET`. Gemessen wurde damals ≈ 0,5 s (Seite 1) bis ≈ 1,25 s (Mitte) an einem
Katalog, dessen Änderungsdaten mit der Id liefen. Im synthetischen Katalog von Spike #08 (500 000 Dateien,
Änderungsdatum quer zur Id wie bei gemischt eingelesenen Ordnern) kostete **jede** Seite 7,8–8,4 s: Der Planer läuft
über `IX_Files_Library_Modified`, holt jede Files-Zeile und jede Zuordnung in zufälliger Reihenfolge (Seitencache
2 MB), und sortiert danach breite Zeilen. Die Kosten liegen also nicht in der Tiefe der Seite, sondern im Lookup je
Datei — ein Top-N oder Keyset-Paging der einen Abfrage hätte daran nichts geändert.

## Entscheidung

Die Gesamtordnung (Wert-Reihenfolge der Verwaltung bzw. umgekehrt, gleiche Werte nach Datum neu → alt und Id, „ohne
Wert“ zuletzt) wird in **Segmente je Wert** zerlegt:

1. **Zählung je Wert** (`GROUP BY ValueId`) unter allen Filtern; „ohne Wert“ = Gesamtzahl − Summe. Ohne Suche treibt der
   neue abdeckende Index `IX_FileCategoryValues_Category_Value_File (CategoryId, ValueId, FileId)`; mit Suche treiben die
   Treffer.
2. Die Seite liest nur aus dem Segment (bzw. den zwei Segmenten), in das sie fällt: innen nur `Id` und Datum in der
   Reihenfolge von `IX_Files_Library_Modified` mit `f.Id IN (Dateien des Werts)` bzw. `NOT IN (Dateien mit Wert)` —
   beide aus dem neuen Index —, `LIMIT/OFFSET` relativ zum Segment und Reverse-Trick vom Segmentende; außen die Spalten
   nur für die Zeilen der Seite (verzögerter Join). Kleine Wert-Segmente (≤ 4 000 Dateien) ohne Suche und Filter lesen
   stattdessen ihre Zuordnungen aus dem Index und sortieren nur diese.
3. Zählung und Seitenabfragen laufen in **einer Lese-Transaktion** (WAL-Snapshot), damit Segmentgrößen und Zeilen
   zusammenpassen, auch wenn zwischendurch jemand committet.
4. Die Zählung wird je Abfrage (SQL + Parameter) zwischengespeichert, bis eine andere Verbindung etwas schreibt
   (`PRAGMA data_version`, innerhalb der Transaktion stabil; die Galerie-Verbindung schreibt nie). Blättern kostet dann
   nur noch das Segment. Die Gesamtzahl aus `GetTotalCountAsync` wird unter demselben Schlüssel übernommen.

Migration `AddCategorySortIndex`: `IX_FileCategoryValues_Category` (nur `CategoryId`) entfällt, der neue Index deckt
dieses Präfix (Fremdschlüssel, Kategorie-Filter, Suche nach Kategorie-Namen) mit ab. Daten bleiben unverändert.

## Begründung

- Gemessen (500 000 Dateien, Spike #08): Seite 1, Mitte und Seite 801 von 7,8–8,4 s auf 0,26–0,61 s beim ersten Laden
  nach einer Änderung und 0,03–0,26 s beim Blättern (Segment allein). Die Reihenfolge ist identisch mit 0.2.23 (Tests
  vergleichen Seite für Seite gegen die alte Abfrage).
- Ein Index über Tabellen hinweg (Wert-Reihenfolge aus `CategoryValues`, Datum aus `Files`) ist in SQLite nicht
  möglich; die Segmentierung nutzt, dass die erste Sortierspalte nur wenige verschiedene Werte hat.
- `data_version` ist exakt: Jede Zuweisung, jedes Umsortieren der Werte, jeder Scan committet über eine andere
  Verbindung und macht den Eintrag ungültig. Keine Abhängigkeit vom ViewModel oder Messenger.

## Konsequenzen

### Positiv
- Kategorie-Sortierung bei großen Bibliotheken bedienbar; tiefe Seiten nicht teurer als Seite 1.
- Kategorie-Filter und Suche nach Kategorie-Namen lesen nur noch den Index.

### Negativ
- Mehr SQL-Code (`GalleryPageSource.CategorySort.cs`): zwei Abfrage-Formen und eine Segment-Rechnung im Code.
- Der erste Seitenaufruf nach einer Änderung zählt (500k: einige 100 ms); das Segment „ohne Wert“ baut eine
  `NOT IN`-Menge aller Dateien mit Wert (500k mit 80 % Wert: ≈ 0,2–0,5 s).
- Zusätzlicher Index-Platz: drei Ganzzahlen je Zuweisung (ersetzt den bisherigen Ein-Spalten-Index).

### Neutral
- `LoadPageAsync` ignoriert für die Kategorie-Sortierung die übergebene Gesamtzahl und rechnet mit der eigenen Zählung
  im Snapshot; `GetTotalCountAsync` merkt sich bei Kategorie-Sortierung seine Zahl samt `data_version`.
- Eine Zuordnung ohne Wert-Zeile (nur bei ausgeschalteten Fremdschlüsseln denkbar) steht als eigenes Segment hinter
  den Werten und vor „ohne Wert“ (vorher mitten unter „ohne Wert“).
- Die Sortierung nach Name/Größe hat dasselbe Grundmuster (Lookup je Datei über `IX_Files_Library_Modified`) und ist
  bei quer verteilten Daten ebenfalls langsam — nicht Teil dieser Entscheidung (Spike #08, offen).

## Alternativen

- **Index allein** (z. B. `(FileId, CategoryId, ValueId)` abdeckend) bei der einen Abfrage → verworfen: spart nur den
  Tabellen-Lookup der Zuordnung, die 500 000 Lookups und die Sortierung bleiben (Teilmessung: die Lookups allein in
  Id-Reihenfolge ≈ 1–2 s, die Abfrage ohne breite Spalten 3,4–4,9 s).
- **Keyset-Paging/Top-N** wie bei Name/Größe → verworfen: die Kosten liegen im Lookup je Datei, nicht im OFFSET; Seite 1
  wäre genauso langsam.
- **Rangliste im Speicher** (alle Ids und Werte lesen, in C# sortieren, Seite herausschneiden) → verworfen: liest bei
  jeder Abfrage 500 000 Zeilen in den Prozess und bricht mit ADR-0010 (nie mehr als eine Seite pro Aufruf).
- **Materialisierte CTE mit Join** → verworfen: SQLite baut dafür keinen Index, der Join wird quadratisch.

## Referenzen

- Code: `src/PhotoViewerV3.Wpf/Gallery/Data/GalleryPageSource.CategorySort.cs`, Migration
  `src/PhotoViewerV3.Data/Migrations/*_AddCategorySortIndex.cs`
- Tests: `tests/PhotoViewerV3.Wpf.Tests/Categories/GalleryCategorySortPagingTests.cs`,
  `GalleryPageSourceCategoryTests.cs`, `tests/PhotoViewerV3.Tests/Categories/CategorySortIndexMigrationTests.cs`
- Messung: [docs/spikes/08-suche-sortierung.md](../spikes/08-suche-sortierung.md)

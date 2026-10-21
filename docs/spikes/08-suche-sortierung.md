# Spike #08 — Suche und Kategorie-Sortierung bei 500 000 Dateien

- **Status:** 🟢 Grün
- **Datum:** 2026-10-20 (v0.2.30)
- **Bezogen auf:** `src/Lumora.Wpf/Gallery/Data/GalleryPageSource.cs`, `GalleryPageSource.CategorySort.cs`,
  Migration `AddCategorySortIndex`, [ADR-0031](../adr/0031-kategorie-sortierung-segmentweise.md),
  Messtest `tests/Lumora.Wpf.Tests/Gallery/GallerySearchSortMeasurementTests.cs`

## Problem (offene Punkte aus 0.2.23)

1. Der Tag-Zweig der Suche las alle `FileTags`-Zeilen (Join gegen `Tags`, Plan `SCAN ft`).
2. Tiefe Seiten der Kategorie-Sortierung kosteten „gut eine Sekunde“ — im Messkatalog unten sogar jede Seite ~8 s.
3. Die Suche fand Kategorie-Werte, aber keine Kategorie-Namen.

## Messaufbau

- Messtest `GallerySearchSortMeasurementTests` gegen eine echte, per `DatabaseInitializer` migrierte DB und die echte
  `GalleryPageSource` (Raw-SQLite-Verbindung mit den App-PRAGMAs, Seitencache 2 MB, kein `ANALYZE` — wie beim Benutzer).
  Größe über `LUMORA_SEARCHSORT_FILES` (Standard 3 000 im normalen Testlauf), `LUMORA_SEARCHSORT_DIR` behält die DB.
- Katalog: 500 000 Dateien, 200 je Ordner, Änderungsdatum **quer zur Id** verteilt (Permutation; ungünstigster Fall
  für Lookups in Datums-Reihenfolge). Tags: 40 Hintergrund-Tags, je Datei zwei, dazu „Urlaub“ (jede 100.) und
  „Familie“ (jede 5.) — 1,1 Mio. Zuweisungen. Kategorien: „Qualität“ (Sortier-Kategorie, 4 belegte Werte à 100 000,
  100 000 ohne Wert), „Projekt“ (12 Werte, 50 %), „Kunde“ (3 Werte, 2 %).
- Seitengröße 500, Median aus 3 Läufen nach einem Aufwärmlauf, warmer OS-Cache. „Zählen“ = `GetTotalCountAsync`.
  Nachher zusätzlich: „nach Änderung“ = vor jedem Lauf ein echter Commit einer anderen Verbindung (entspricht dem Neuladen
  nach Zuweisung/Scan, Segment-Zählung wird neu gemacht), „wiederholt“ = Blättern ohne Änderung (Zählung aus dem Zwischenspeicher).
- Rechner: Herberts PC, während der Messung liefen parallel Builds/Tests anderer Agenten — Werte schwanken um ±50 %.

## Ergebnis

| Fall | Treffer | vorher Zählen | vorher Seite | nachher Zählen | nachher Seite nach Änderung | nachher Seite wiederholt |
|---|---:|---:|---:|---:|---:|---:|
| Tag-Suche „urlaub“ (selten), Seite 1 | 5 000 | 196 ms | 197 ms | 42 ms | 43 ms | 42 ms |
| Tag-Suche „familie“ (häufig), Seite 1 | 100 000 | 219 ms | 243 ms | 133 ms | 141 ms | 139 ms |
| Kategorie-Sortierung ↑, Seite 1 | 500 000 | 31 ms | 7 782 ms | 29 ms | 261 ms | 27 ms |
| Kategorie-Sortierung ↑, Seite 501 (Mitte) | 500 000 | 26 ms | 8 439 ms | 29 ms | 337 ms | 109 ms |
| Kategorie-Sortierung ↑, Seite 801 (tief) | 500 000 | 26 ms | 7 953 ms | 29 ms | 430 ms | 187 ms |
| Kategorie-Sortierung ↓, Seite 801 (tief) | 500 000 | 24 ms | 7 793 ms | 39 ms | 611 ms | 259 ms |
| Kategorie-Namen-Suche „projekt“ (50 %), Seite 1 | 250 000 | (0 Treffer) 157 ms | 156 ms | 329 ms | 340 ms | 383 ms |
| Kategorie-Namen-Suche „kunde“ (2 %), Seite 1 | 10 000 | (0 Treffer) 155 ms | 154 ms | 46 ms | 56 ms | 58 ms |
| Vergleich (unverändert): Datum neu → alt, Seite 501 | 500 000 | — | — | 30 ms | 2 445 ms | 2 546 ms |
| Vergleich (unverändert): Name A → Z, Seite 501 | 500 000 | — | — | 38 ms | 6 763 ms | 7 093 ms |

Endstand (Lese-Transaktion je Seite, gemerkte Gesamtzahl aus `GetTotalCountAsync`). Ein früherer Lauf ohne diese beiden
Punkte und unter stärkerer Last lag beim ersten Laden bei 0,56–0,96 s und beim Blättern bei 0,07–0,38 s, die
Tag-Suche „urlaub“ bei 73 ms — die Größenordnung bestätigt sich.

### Lesart

- **Tag-Suche**: der seltene Tag profitiert deutlich (196 → ~40–70 ms); beim häufigen Tag dominieren die 100 000
  Treffer (Materialisieren + Files-Lookups), dort liegt der Unterschied im Rauschen.
- **Kategorie-Sortierung**: Faktor 13–30 beim ersten Laden nach einer Änderung, 30–290 beim Blättern. Die Kosten lagen
  nicht im OFFSET, sondern im Lookup je Datei der einen Abfrage (siehe Pläne); tiefe Seiten sind jetzt nur noch so
  teuer wie ihr Segment. Am teuersten ist das Segment „ohne Wert“ (Seite 801 liegt darin: `NOT IN` über alle 400 000
  Dateien mit Wert).
- **Kategorie-Namen-Suche**: vorher keine Treffer (nur Kosten des alten Tag-Scans), nachher 250 000 bzw. 10 000
  Treffer in 0,05–0,4 s.
- **Vergleich**: die Grundsortierungen Datum/Name lesen bei quer verteiltem Datum je übersprungener Zeile die ganze
  Files-Zeile (Reverse-Trick halbiert nur) — 2,4 bzw. 6,8 s in der Mitte. Nicht Teil dieser Änderung (offen, siehe unten).

## Abfragepläne

**Tag-Zweig der Suche** (`EXPLAIN QUERY PLAN`, Zählabfrage mit „urlaub“):

| vorher | nachher |
|---|---|
| `SCAN ft` | `SEARCH ft USING INDEX IX_FileTags_Tag (TagId=?)` |
| `SEARCH t USING INTEGER PRIMARY KEY (rowid=?)` | `LIST SUBQUERY 2` · `SCAN t USING COVERING INDEX UX_Tags_Name` · `CREATE BLOOM FILTER` |
| — (Kategorie-Namen nicht gesucht) | `SEARCH fcn USING COVERING INDEX IX_FileCategoryValues_Category_Value_File (CategoryId=?)` · `LIST SUBQUERY 6` · `SCAN k USING COVERING INDEX UX_Categories_Name` |

Die übrigen Zweige (FTS `SCAN FilesFts VIRTUAL TABLE INDEX 0:M2`, Werte `SEARCH fcv USING INDEX
IX_FileCategoryValues_Value (ValueId=?)`, `SCAN hit` + `SEARCH f USING INTEGER PRIMARY KEY`) sind unverändert.

**Kategorie-Sortierung** vorher (eine Abfrage):

```
SEARCH f USING INDEX IX_Files_Library_Modified (LibraryId=?)
SEARCH fo USING INTEGER PRIMARY KEY (rowid=?)
SEARCH lr USING INTEGER PRIMARY KEY (rowid=?) LEFT-JOIN
SEARCH scv USING INDEX sqlite_autoindex_FileCategoryValues_1 (FileId=? AND CategoryId=?) LEFT-JOIN
SEARCH sv USING INTEGER PRIMARY KEY (rowid=?) LEFT-JOIN
USE TEMP B-TREE FOR ORDER BY
```

nachher (Segment-Zählung, Wert-Segment; das Segment „ohne Wert“ wie das Wert-Segment mit `(CategoryId=?)`; kleine
Wert-Segmente ≤ 4 000 Dateien ohne Suche/Filter lesen ihre Zuordnungen `SEARCH c … (CategoryId=? AND ValueId=?)` +
`SEARCH f USING INTEGER PRIMARY KEY` und sortieren nur diese, `USE TEMP B-TREE FOR ORDER BY`):

```
CO-ROUTINE g
  SEARCH scv USING COVERING INDEX IX_FileCategoryValues_Category_Value_File (CategoryId=?)
  SEARCH f USING INTEGER PRIMARY KEY (rowid=?)
SCAN g
SEARCH sv USING INTEGER PRIMARY KEY (rowid=?) LEFT-JOIN

CO-ROUTINE p
  SEARCH f USING COVERING INDEX IX_Files_Library_Modified (LibraryId=?)
  LIST SUBQUERY 1
  SEARCH c USING COVERING INDEX IX_FileCategoryValues_Category_Value_File (CategoryId=? AND ValueId=?)
  CREATE BLOOM FILTER
  USE TEMP B-TREE FOR LAST TERM OF ORDER BY
SCAN p
SEARCH f USING INTEGER PRIMARY KEY (rowid=?)
SEARCH fo USING INTEGER PRIMARY KEY (rowid=?)
SEARCH lr USING INTEGER PRIMARY KEY (rowid=?) LEFT-JOIN
```

## Zwischenmessungen (Python `sqlite3` 3.50.4, gleiche DB, Seitencache 2 MB, `temp_store=MEMORY`)

| Variante (Seite in der Mitte, Offset 250 000) | Zeit |
|---|---:|
| alte Abfrage | 6,8–7,0 s |
| nur Ids/Rang statt breiter Spalten, Lookups in Id-Reihenfolge (`NOT INDEXED`) | 4,9 s |
| nur Ids/Rang, Datums-Index abdeckend | 3,7–4,1 s |
| nur die 500 000 Zuordnungs-Lookups (ohne Sortierung) | 1,9–2,0 s |
| Sortierung 500 000 schmaler Zeilen aus abdeckendem Index (ohne Lookups) | 0,3 s |
| Segment-Zählung ohne neuen Index / mit neuem Index | 3,7 s / 0,26–0,47 s |
| Wert-Segment (80 000–100 000) Offset 0 / Mitte, ohne / mit neuem Index | 0,09–0,30 s / 0,02–0,11 s |

Die Lookups je Datei sind der Kostentreiber; ein Index allein, Top-N oder Keyset-Paging der einen Abfrage hätten sie
nicht beseitigt. Eine `MATERIALIZED`-CTE mit Join lief ohne automatischen Index quadratisch (nach 5 min abgebrochen).

## Offen

- Grundsortierungen Datum/Name/Größe: gleiche Ursache (Files-Zeile je übersprungener Zeile in Datums-Reihenfolge).
  Ein verzögerter Join (innen nur Id + Sortierspalten über einen abdeckenden Index, außen die Spalten der Seite) wie bei
  den Kategorie-Segmenten würde Datum ↓/↑ vermutlich auf das Niveau der Segmente bringen; Name/Größe bräuchten dazu
  eigene Indizes `(LibraryId, Name COLLATE NOCASE)` bzw. `(LibraryId, Size)`.
- Sehr große Suchtreffer (Kategorie-Name mit 250 000 Dateien) materialisieren alle Treffer (~0,3–0,4 s). Die Suche
  nach Kategorie-Namen ist wie bei Tags/Werten ein Teilstring-Vergleich: ein kurzer Suchtext (z. B. „e“) holt alle
  Dateien jeder Kategorie, deren Name ihn enthält.

# ADR-0027: Frei konfigurierbare Kategorien mit festen Werten: ein Wert je Datei, Filter und Listenspalte

- **Status:** Accepted (Nachtrag 0.2.23: Strg+K, Lightbox, Suche per Join, serverseitige Kategorie-Sortierung — die
  Konsequenzen „sortieren nur die geladene Seite“, „nur Rechtsklick-Menü“ und „Suche findet Werte nicht“ sind aufgehoben;
  Ausführung der Kategorie-Sortierung als eine Abfrage über alle Dateien und Index `IX_FileCategoryValues_Category`
  abgelöst durch [ADR-0031](0031-kategorie-sortierung-segmentweise.md), 0.2.30 — Reihenfolge unverändert; die Suche
  findet seit 0.2.30 auch Kategorie-Namen); teilweise ersetzt durch [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) —
  Kategorien sind nicht mehr global, sondern gehören einer Bibliotheks-Gruppe (0.2.51)
- **Datum:** 2026-10-20
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** [ADR-0021](0021-tags-eigene-tabellen.md) (Tags), [ADR-0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md) (Neu aufbauen), [ADR-0010](0010-ui-datenmodell-paging.md) (Paging)

## Kontext

Herbert will neben Tags eigene, in den Einstellungen frei anlegbare „Kategorien“ mit festen Werten — Beispiel „XX“ mit
den Punkten „N“ und „NN“ —, nach denen die Galerie filtert und die in der Listenansicht als Spalten erscheinen, mehrere
davon. Tags sind dafür ungeeignet: sie sind eine flache Menge ohne Gruppierung und erlauben beliebig viele je Datei; eine
Spalte „XX“ braucht genau einen Wert je Datei. Offen war, wie eine Datei ihren Wert bekommt (manuell oder automatisch
per Dateinamen-Muster) — Herbert hat „manuell zuweisen, ein Wert je Kategorie“ gewählt.

## Entscheidung

Drei eigene Tabellen (Migration `AddCategories`), global wie Tags:

- `Categories (Id, Name UNIQUE NOCASE, SortOrder, CreatedAtUtc)`
- `CategoryValues (Id, CategoryId → Categories CASCADE, Name NOCASE, SortOrder, UNIQUE (CategoryId, Name))`
- `FileCategoryValues (FileId → Files, CategoryId → Categories, ValueId → CategoryValues, alle CASCADE, PK (FileId, CategoryId))`

Der Primärschlüssel (FileId, CategoryId) erzwingt „ein Wert je Kategorie“; Setzen ist ein UPSERT, „Kein Wert“ ein
DELETE. `ICategoryService` (Services/Categories) verwaltet Stammdaten und Zuweisungen (Raw-SQL in Chunks, eigener
Schreib-Lock, prüft „Wert gehört zur Kategorie“) und meldet `CategoriesChangedMessage` bzw. `FilesChangedMessage`
(Metadata). Oberfläche: Seite **Kategorien** (Navigation unter „Tags“), Rechtsklick-Untermenü je Kategorie zum
Zuweisen, Dropdown **Kategorien** in der Galerie-Kopfzeile (je Kategorie Werte ODER „ohne Wert“, Kategorien
untereinander UND), je Kategorie eine Spalte der Listenansicht. Spalten-Einstellung `ui.gallery.listColumns` kennt
dafür die Token `Kategorie:{Id}` (sichtbar, mit Reihenfolge) und `!Kategorie:{Id}` (ausgeblendet); Kategorie-Spalten
ohne Token sind sichtbar, damit neue Kategorien sofort erscheinen. „Neu aufbauen“ trägt die Werte über
`UserDataCarryOver` mit (ADR-0023).

## Begründung

- Eigene Tabellen statt Tags mit „Gruppe“: die Regel „genau ein Wert“ steckt im Schlüssel statt in jedem Schreibpfad;
  Filter- und Spalten-SQL bleiben einfache Joins über indizierte Spalten.
- Manuelle Zuweisung (Herberts Wahl) passt zu Begriffen, die nicht aus dem Dateinamen ableitbar sind; ein automatischer
  Musterabgleich bliebe als Ergänzung möglich (eigene Spalte „Muster“ je Wert), ohne das Schema zu brechen.
- Filter-Semantik ODER innerhalb / UND zwischen Kategorien entspricht der Erwartung bei Facettenfiltern.

## Konsequenzen

### Positiv
- Beliebig viele Kategorien, jede mit eigener Spalte und eigenem Filter, ohne Schemaänderung pro Kategorie.
- Löschen von Wert oder Kategorie räumt die Zuordnungen per CASCADE (und ausdrücklich im Dienst) auf.

### Negativ
- Kategorie-Spalten sortieren nur innerhalb der geladenen Seite (wie Tags/Status), nicht die ganze Trefferliste.
- Zuweisen nur über das Rechtsklick-Menü (keine Tastenkürzel, kein Feld in der Lightbox).

### Neutral
- Werte werden je Page in einem Batch nachgeladen (wie Tags); die Suche findet Kategorie-Werte nicht.

## Alternativen

- **Tags mit Gruppen:** verworfen — „ein Wert je Gruppe“ müsste jeder Schreibpfad selbst sichern; Tag-Filter (UND/ODER)
  und Spalten passen nicht zur Facetten-Logik.
- **Automatische Zuordnung per Dateinamen-Muster:** von Herbert zugunsten manueller Zuweisung entschieden.
- **Mehrere Werte je Kategorie:** von Herbert zugunsten „ein Wert“ entschieden (dafür gibt es Tags).

## Nachtrag 0.2.23 (2026-10-20): Tastatur, Lightbox, Suche, Sortierung

Offene Punkte aus 0.2.20, Umsetzung Claude (Agent, ohne Rückfragemöglichkeit — Annahmen unten).

### Entscheidungen

1. **Tastenkürzel Strg+K** („Kategorie“) in Galerie und Lightbox öffnet einen Dialog
   (`Gallery/Categories/CategoryPickerDialog`, hinter `ICategoryPickerService` wie die Tag-Auswahl Strg+T): je Kategorie
   eine Auswahlliste mit den Werten und „Kein Wert“, bei unterschiedlichen Werten der Auswahl zusätzlich
   „(gemischt — nicht ändern)“ als Ausgangszustand. Das Ergebnis enthält nur geänderte Kategorien; je Kategorie ein
   `SetValueAsync`. Strg+K war weder in der Galerie (Strg+←/→/Pos1/Ende/A/M/T, F5, Entf, Esc, Strg+Plus/Minus/Mausrad)
   noch in der Lightbox (Strg+D/M/T/0–5 u. a.) belegt; ein Test prüft, dass kein Kürzel doppelt vergeben ist. Die
   Ausgangswerte kommen aus der DB (`GetValuesForFilesAsync`), damit ausgewählte Dateien außerhalb der Page mitzählen.
2. **Lightbox:** gesetzte Werte kompakt in der Titelleiste („XX: N · Projekt: Nord“), im Info-Panel (I) alle Kategorien
   mit Wert bzw. „—“ und Knopf „Zuweisen… (Strg+K)“ (Muster der Tags: `LightboxViewModel.Categories.cs`, lädt bei
   Blättern, `FilesChangedMessage(Metadata)` und `CategoriesChangedMessage` neu).
3. **Suche per Join, nicht per FTS.** Die Treffermenge der Galeriesuche bekommt einen dritten `UNION`-Zweig:
   `SELECT fcv.FileId FROM FileCategoryValues fcv WHERE fcv.ValueId IN (SELECT v.Id FROM CategoryValues v WHERE v.Name
   LIKE %text%)` — Teilstring ohne Groß-/Kleinschreibung wie bei Tag-Namen (ADR-0021), gleiche Materialisierung und
   `CROSS JOIN` an `Files`. Die `IN`-Form erzwingt den Weg über die kleine Tabelle `CategoryValues` und
   `IX_FileCategoryValues_Value` (ein Test prüft den Abfrageplan: kein `SCAN fcv`, kein `SCAN f`).
4. **Sortierung serverseitig:** Klick auf eine Kategorie-Spalte setzt `GalleryQuery.CategorySort (CategoryId, Descending)`;
   die Page-SQL holt den Wert per `LEFT JOIN FileCategoryValues … AND CategoryId = $sortCat` (Primärschlüssel-Lookup) und
   `LEFT JOIN CategoryValues` und sortiert `(ohne Wert) zuletzt, Wert-SortOrder auf/ab, Datum neu → alt, Id` — mit dem
   bestehenden Paging inkl. Reverse-Trick (jede Spalte wird gedreht). Die Count-Abfrage bleibt ohne Join. In der
   Sortier-Auswahl erscheint die Kategorie-Sortierung als eigener Eintrag „XX (Kategorie) ↑/↓“, solange sie gilt.

### Begründung

- **Join statt FTS:** `FilesFts` ist External-Content über `Files` (rowid = Files.Id) ohne Trigger. Eine Spalte für
  Kategorie-Werte bräuchte eine zweite Content-Quelle und Pflege bei jeder Zuweisung, bei jedem Umbenennen eines Werts
  (alle betroffenen Dateien neu indizieren) und beim Löschen von Wert/Kategorie (Kaskade ohne FTS-Delete wäre
  inkonsistent). Werte sind wenige und kurz; der Join ist ohne Pflege immer konsistent (Umbenennen wirkt sofort, Test)
  und bleibt schnell, weil die Treffer die Abfrage treiben. Messung mit 500 000 Dateien (2/3 mit XX-Wert, 1/5 mit
  Projekt-Wert, 1/10 getaggt; Messrechner unter Last durch parallele Builds, Median aus drei Läufen):

  | Abfrage (Count + Page 500) | Zeit |
  |---|---|
  | Suche Dateiname (`IMG_00012`, 100 Treffer) | 17 ms |
  | Suche seltener Wert (334 Treffer) | 33 ms |
  | Suche häufiger Wert (`nord`, 50 000 Treffer) | 250–320 ms |
  | Suche ohne Treffer | 21 ms |
  | ohne Suche, Datum neu → alt (Index) | 33 ms |

- **Sortierung:** Ein Index kann einen Sortierschlüssel aus einem Join nicht abdecken; wie die bestehende Sortierung nach
  Name/Größe (ohne Index) ist es eine Top-N-Sortierung über die Treffer. Gleichstände nach Datum (Ganzzahl) statt Name:
  bei tiefen Seiten halb so teuer (Messung Seite 500/1000 der Liste: Name-Tiebreak 1,8 s, Datum-Tiebreak 0,9 s) und
  dieselbe Reihenfolge wie die Standard-Sortierung. Ergebnis bei 500 000 Dateien: Seite 1 ≈ 0,5 s, Mitte ≈ 1,25 s,
  letzte Seite (Reverse) ≈ 0,8 s, mit Kategorie-Filter (50 000 Treffer) ≈ 0,35 s — gleiche Größenordnung wie
  „Name A → Z“ (0,3 s / 1,25 s). Werte-Reihenfolge = Reihenfolge der Verwaltung (feste Werte sind wie eine Aufzählung,
  z. B. „hoch, mittel, niedrig“), nicht alphabetisch; „ohne Wert“ in beiden Richtungen am Ende, damit die belegten
  Dateien oben stehen.

### Annahmen (ohne Rückfrage)

- Kürzel **Strg+K** (frei, merkbar); der Dialog bearbeitet alle Kategorien auf einmal (wie Strg+T alle Tags).
- Die Suche findet **Werte**, nicht Kategorie-Namen (Suche „Projekt“ liefert nicht alle Dateien mit Projekt-Wert).
- Der Platzhalter des Suchfelds bleibt „Name, Endung oder Tag…“ (ein längerer Text passt bei 1280 px nicht vollständig,
  Layouttest); der Tooltip nennt die Kategorie-Werte.

### Konsequenzen

- Positiv: Zuweisen ohne Maus; Werte in der Lightbox sichtbar und änderbar; Suche und Sortierung decken Kategorien ohne
  Schema- oder FTS-Änderung ab.
- Negativ: Tiefe Seiten der Kategorie-Sortierung kosten bei 500 000 Dateien gut eine Sekunde (wie Name/Größe). Der
  Tag-Zweig der Suche liest alle `FileTags`-Zeilen (Abfrageplan `SCAN ft`, bestehend seit 0.2.10) — bei vielen
  Tag-Zuweisungen ein Kandidat für dieselbe `IN`-Form.

## Referenzen

- 0.2.23: `src/PhotoViewerV3.Wpf/Gallery/Categories/ICategoryPickerService.cs`, `CategoryPickerDialog.xaml(.cs)`,
  `Gallery/ViewModels/GalleryViewModel.Categories.cs` (Strg+K, `SortByCategory`), `Gallery/Data/GalleryPageSource.cs`
  (`BuildFilesSource`, `BuildCategoryOrderBy`, `ExplainAsync`), `Gallery/ListView/GalleryListView.xaml.cs` (`OnSorting`),
  `Lightbox/LightboxViewModel.Categories.cs`, `Lightbox/LightboxWindow.xaml(.cs)`; Tests
  `tests/PhotoViewerV3.Wpf.Tests/Categories/CategoryPickerTests.cs`, `GalleryPageSourceCategoryTests.cs`,
  `tests/PhotoViewerV3.Tests/Categories/CategoryServiceTests.cs`, Rauchtest-Schritt „Kategorien“
- `src/PhotoViewerV3.Domain/Entities/Category*.cs`, `FileCategoryValue.cs`, `Data/EntitiesMapping/CategoryConfiguration.cs`,
  Migration `20261020095203_AddCategories`
- `src/PhotoViewerV3.Services/Categories/*`, `Scanning/UserDataCarryOver.cs`
- `src/PhotoViewerV3.Wpf/Settings/CategoryManagement/*`, `Gallery/Categories/*`, `Gallery/ViewModels/GalleryViewModel.Categories.cs`,
  `Gallery/ListView/GalleryListModels.cs` (Spalten-Token), `Gallery/Data/GalleryPageSource.cs` (Filter-SQL)
- Tests: `tests/PhotoViewerV3.Tests/Categories/CategoryServiceTests.cs`, `tests/PhotoViewerV3.Wpf.Tests/Categories/*`,
  Rauchtest-Schritt „Kategorien“

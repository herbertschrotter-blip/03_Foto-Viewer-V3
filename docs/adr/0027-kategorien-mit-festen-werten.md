# ADR-0027: Frei konfigurierbare Kategorien mit festen Werten: ein Wert je Datei, Filter und Listenspalte

- **Status:** Accepted
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

## Referenzen

- `src/PhotoViewerV3.Domain/Entities/Category*.cs`, `FileCategoryValue.cs`, `Data/EntitiesMapping/CategoryConfiguration.cs`,
  Migration `20261020095203_AddCategories`
- `src/PhotoViewerV3.Services/Categories/*`, `Scanning/UserDataCarryOver.cs`
- `src/PhotoViewerV3.Wpf/Settings/CategoryManagement/*`, `Gallery/Categories/*`, `Gallery/ViewModels/GalleryViewModel.Categories.cs`,
  `Gallery/ListView/GalleryListModels.cs` (Spalten-Token), `Gallery/Data/GalleryPageSource.cs` (Filter-SQL)
- Tests: `tests/PhotoViewerV3.Tests/Categories/CategoryServiceTests.cs`, `tests/PhotoViewerV3.Wpf.Tests/Categories/*`,
  Rauchtest-Schritt „Kategorien“

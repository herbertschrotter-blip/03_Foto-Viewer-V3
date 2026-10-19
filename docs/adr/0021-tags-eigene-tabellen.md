# ADR-0021: Tags als eigene Tabellen mit Katalog-Zuweisung

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter (Umsetzung Claude, Agent T)
- **Bezogen auf:** ADR-0003, ADR-0004, ADR-0005, ADR-0010, ADR-0011, ADR-0017

## Kontext

Neben Favorit und Bewertung braucht die Bibliothek frei definierbare Schlagworte („Urlaub“, „Familie“, „Rechnung“), die
man vielen Dateien auf einmal zuweist, nach denen man filtert (eines oder mehrere, UND/ODER) und sucht. PLAN.md Kap. 4
hatte `Tags / FileTags` bereits für V0.2 vorgesehen. Rahmen: 50k–500k Dateien, Galerie mit Raw-SQL-Paging (ADR-0010),
FTS5 als External-Content-Tabelle ohne Trigger (ADR-0011), Single-Writer je Service, stabile FileIds (ADR-0019), und ein
Papierkorb-Pfad (`FileOperationService`), der Files-Zeilen entfernt.

## Entscheidung

Tags liegen in zwei eigenen Tabellen — `Tags (Id, Name UNIQUE NOCASE, Color, SortOrder, CreatedAtUtc)` und
`FileTags (FileId, TagId)` mit zusammengesetztem Primärschlüssel, Index auf `TagId` und **beiden Fremdschlüsseln mit
ON DELETE CASCADE** — und werden ausschließlich über `ITagService` (Services/Tags) geschrieben. Die Galerie-Suche findet
Tag-Namen über einen Join (`f.Id IN (SELECT FileId FROM FileTags JOIN Tags … WHERE Name LIKE %text%)`), nicht über eine
zusätzliche FTS-Spalte.

## Begründung

- **Eigene Tabellen statt Spalte/JSON in `Files`:** Zuweisung an viele Dateien = `INSERT OR IGNORE` in Chunks zu 500 Ids,
  Filter = Index-Lookup über `IX_FileTags_Tag`, Umbenennen/Umfärben ändert eine Zeile statt aller Dateien.
- **Tags global, nicht je Bibliothek:** dieselben Schlagworte gelten in allen Bibliotheken (eine aktive Library,
  zentrale DB, ADR-0003); die Zähler in der Verwaltung zählen bibliotheksübergreifend.
- **ON DELETE CASCADE:** `e_sqlite3` ist mit `SQLITE_DEFAULT_FOREIGN_KEYS=1` gebaut, Fremdschlüssel werden also auf
  jeder Verbindung erzwungen (Test `Loeschen_einer_Datei_entfernt_ihre_Zuweisungen_per_ON_DELETE_CASCADE`). Papierkorb,
  „Neu aufbauen“ (ClearAndRescan) und Tag löschen räumen die Zuweisungen ohne Code in fremden Modulen auf. Für Pfade ohne
  FK-Erzwingung gibt es `ITagService.RemoveForFilesAsync` als Hilfsmethode.
- **Suche per Join statt FTS-Spalte:** `FilesFts` ist External-Content über `Files` (rowid = Files.Id); eine Tag-Spalte
  bräuchte eine eigene Content-Quelle und müsste bei jeder Zuweisung, Umbenennung und Löschung gepflegt werden. Tag-Namen
  sind wenige und kurz — die Vereinigung „FTS-Treffer UNION Tag-Treffer“ wird einmal materialisiert und per
  `CROSS JOIN` über die Rowid an `Files` gebunden (die Treffer treiben die Abfrage wie bisher die FTS-Tabelle; eine
  `OR`-Verknüpfung zweier `IN`-Listen ließ SQLite alle Dateien der Library durchlaufen). Das ist billig und immer
  konsistent. Die Tag-Suche ist eine Teilstring-Suche ohne Groß-/Kleinschreibung (`LIKE` auf der NOCASE-Spalte,
  Sonderzeichen escaped), die Dateinamen-Suche bleibt die FTS-Präfixsuche.
- **Benachrichtigung:** Zuweisungen melden sich als `FilesChangedMessage(Metadata)` je Library (wie Favorit/Bewertung,
  Galerie und Lightbox laden nach), Stammdaten-Änderungen als neue `TagsChangedMessage` (ADR-0017).

## Konsequenzen

### Positiv
- Filter ODER / UND / „ohne Tag“ als reine SQL-Bedingungen in derselben Count- und Page-Abfrage der Galerie.
- Kein Pflegeaufwand im FTS-Index; Dateioperationen anderer Module brauchen keine Tag-Logik.
- Farben aus fester Palette (12 Farben, `TagPalette`) — einheitliche Chips, keine Farbwähler-Logik.

### Negativ
- Suche nach Tag-Namen ist Teilstring-, nicht Präfix-Suche wie bei Dateinamen (bewusst: Tag-Namen sind kurz).
- Tags hängen an FileIds: „Neu aufbauen“ (neue FileIds) verliert Tag-Zuweisungen — wie Favoriten und Bewertungen.
- `TagService` hat einen eigenen Schreib-Lock (wie `CatalogUpdateService`); gegenüber anderen Schreibern greift
  `busy_timeout` 5000.

### Neutral
- Neue Migration `AddTags` (strikt, Bestandsdaten bleiben unverändert).
- Ansichts-Einstellungen der Galerie (Grid/Liste, Spalten der Listenansicht) liegen als `ui.gallery.viewMode` und
  `ui.gallery.listColumns` in der Settings-Tabelle.

## Alternativen

- **Tags als Text-Spalte in `Files` (kommasepariert/JSON):** Zuweisen = Update jeder Datei, Umbenennen = Update aller
  betroffenen Dateien, Filter = `LIKE` ohne Index → verworfen.
- **Zusätzliche FTS5-Spalte `Tags` in `FilesFts`:** Präfixsuche einheitlich, aber Pflege bei jeder Zuweisung/Umbenennung
  und eine zweite Content-Quelle neben `Files` → verworfen.
- **Tags je Bibliothek:** doppelte Pflege gleichnamiger Tags bei mehreren Bibliotheken → verworfen.
- **Aufräumen der Zuweisungen in `FileOperationService.RecycleAsync`:** Kopplung an ein fremdes Modul; die Kaskade erledigt
  das in der DB → nur als Hilfsmethode `RemoveForFilesAsync` vorhanden.

## Referenzen

- Code: `src/PhotoViewerV3.Services/Tags/`, `src/PhotoViewerV3.Data/EntitiesMapping/TagConfiguration.cs`,
  `FileTagConfiguration.cs`, Migration `AddTags`, `src/PhotoViewerV3.Wpf/Gallery/Data/GalleryPageSource.cs`
  (`BuildSearchClause`, `BuildTagClause`), `src/PhotoViewerV3.Wpf/Gallery/Tags/`, `src/PhotoViewerV3.Wpf/Settings/TagManagement/`
- Tests: `tests/PhotoViewerV3.Tests/Tags/TagServiceTests.cs`, `tests/PhotoViewerV3.Wpf.Tests/Tags/`

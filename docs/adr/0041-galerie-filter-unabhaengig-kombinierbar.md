# ADR-0041: Galerie-Filter unabhängig kombinierbar

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0011 (Filter-Pills, hierdurch im Filter-Teil ersetzt), ADR-0020 (Ablage über `RootId → Role`,
  bleibt), ADR-0021 (Tags), ADR-0027/0031 (Kategorien, Kategorie-Sortierung), ADR-0038 (Personen-Filter), ADR-0010 (Paging)

## Kontext

Bis 0.2.42 war der Datei-Filter der Galerie ein einziges Enum `GalleryFilter` (Alle, Bilder, Videos, Favoriten, Archive,
Nicht archiviert, Archiviert) — ein Erbe der Filter-Pills aus ADR-0011, um die Ablage (ADR-0020) und Archive (ADR-0022)
erweitert. Tag-, Kategorie-, Personen-, Ordner- und Suchfilter wirkten in SQL schon mit UND, aber Dateiart, Favoriten
und Ablage schlossen sich gegenseitig aus. Herbert will z. B. „nur Bilder UND Favoriten UND Tag X UND ohne Person UND
noch im Eingang“ und eine Bewertungs-Untergrenze. Die Kopfzeile (seit 0.2.13 zwei umbrechende Zeilen) zeigte außerdem
nicht, was gerade gefiltert ist. Mockup 06 („Galerie-Kopfzeile“) wurde am 2026-10-21 abgenommen.

## Entscheidung

1. **Filtermodell:** Der Datei-Teil ist ein Record `GalleryFilterState(Media, FavoritesOnly, Location, MinRating)` mit
   `GalleryMediaKind` (Alle/Bilder/Videos/Archive), `GalleryLocation` (Überall/Eingang/Bibliothek) und
   Mindest-Bewertung 0–5. Er ersetzt `GalleryFilter` in `GalleryQuery.Filter`; Tag-, Personen-, Kategorie-, Ordner- und
   Suchfilter bleiben eigene Teile der Query. **Alle Teile gelten mit UND** — in Zähl-, Seiten- und
   Kategorie-Segment-SQL derselbe Aufbau (`GalleryPageSource.FileFilter.cs`).
2. **SQL:** je Teil eine eigene `AND`-Bedingung, für die bisherigen Einzel-Filter wortgleich wie vorher
   (`f.MediaType = n`, `f.IsFavorite = 1`, Ablage als `IN`-Subquery über Folders/LibraryRoots). Neu nur
   `f.Rating >= $minRating` — **ohne neuen Index, ohne Migration**; die Bewertung wird je Zeile geprüft wie bisher der
   Favoriten-Filter auf der Seiten-Abfrage.
3. **ViewModel:** `MediaFilter`, `FavoritesOnly`, `LocationFilter`, `MinRating` als eigene Eigenschaften; `FilterState`
   setzt alle vier mit einem Neuladen. „Filter zurücksetzen“ hebt Datei-Filter, Tags, Personen, Kategorien **und die
   Suche** auf, **nicht** die Ordnerauswahl (Ordnerbaum/Ordner-Seite ist Navigation, kein Filter). Die Zeile „Es gilt:
   … UND … → n Treffer“ ist eine Liste von Bedingungen; × entfernt genau eine. Die Trefferzeile „342 von 1.248 Dateien“
   zählt die Bezugsgröße nur mit Ordnerfilter (eine zusätzliche Zählung, nur wenn ein Filter wirkt).
4. **Kopfzeile** (Mockup 06): Titel + Trefferzeile + Suche (Strg+F, Enter sofort, sonst nach 450 ms Tipp-Pause, Esc
   leert), Filterkarte mit Zeilen „Datei“ und „Inhalt“ (Chips in Akzentfarbe, wenn aktiv), Sortierleiste mit **Feld und
   Richtung getrennt** (Kategorien sind weitere Felder; Grundlage bleibt `SelectedSort`), Ansicht-Segment aus einer
   Liste von `GalleryViewModeOption` (ein weiterer `GalleryViewMode` wie „Collage“ = ein Eintrag), Blättern und „Pro
   Seite“ unter der Galerie. Bei 1280 DIP Fensterbreite bleiben Titelzeile, Zeile „Datei“ und Sortierleiste einzeilig —
   auch mit allen Datei-Filtern aktiv (Chips zeigen dafür Kurzwerte „Eingang“, „ab 3 ★“).

## Begründung

- Unabhängige Teile bilden ab, was der Benutzer denkt („Bilder, und davon die Favoriten, und davon …“); ein Enum mit
  Kombinations-Werten wüchse kombinatorisch.
- Wortgleiche SQL für die bisherigen Einzel-Filter hält deren Abfragepläne unverändert (Test
  `Einzelne_Datei_Filter_erzeugen_dieselben_Bedingungen_wie_vor_0_2_43`); die Kombinationen laufen in jedem der 96
  geprüften Fälle über einen Index (`SEARCH f USING …`, kein `SCAN f`): Zählen über den Index der selektivsten
  Gleichheitsbedingung (`IX_Files_Library_MediaType`/`_Favorite`), die Seite über den Sortier-Index
  `IX_Files_Library_Modified`.
- Ein Bewertungs-Index (`LibraryId, Rating`) hätte eine Migration gebraucht. Gemessen (Spike-Harness
  `GallerySearchSortMeasurementTests`, 500 000 Dateien, warmer Cache, Median aus 3): „Bilder“ allein zählt in 21 ms,
  Seite 1 in 7 ms (wie vorher); „Favoriten“ 4 / 56 ms; **„Bilder + Favoriten + ab 3 Sternen“ (8 334 Treffer) 144 ms
  Zählen, 231 ms Seite 1**; „Bilder + ab 3 Sternen + Tag „Familie““ (41 666) 233 / 112 ms — im Rahmen der Tag-Suche
  („familie“ 167 / 188 ms). Wird der Bewertungs-Filter im Alltag auf großen Bibliotheken zu langsam, ist der Index eine
  kleine, eigene Migration.
- Feld und Richtung getrennt ist verständlicher als sechs kombinierte Einträge plus Kategorie-Einträge; die Listenansicht
  (Kopfzeilenklick) und der Rauchtest setzen weiter `SelectedSort` und bleiben unverändert.

## Konsequenzen

### Positiv
- Jede Kombination aus Dateiart, Favoriten, Ablage, Bewertung, Tags, Personen, Kategorien, Suche und Ordner ist möglich
  und in Worten sichtbar; jede Bedingung lässt sich einzeln entfernen.
- Neuer Filter „Bewertung: egal / ab 1–4 Sternen / nur 5 Sterne“.
- Ansichts-Segment ist für „Collage“ (Welle 2) vorbereitet.

### Negativ
- Mit aktivem Filter eine zweite Zählung je Neuladen (Bezugsgröße der Trefferzeile, nur mit Ordnerfilter über
  `IX_Files_Library_Folder`).
- Bewertungs-Filter ohne eigenen Index (siehe Begründung).

### Neutral
- Filter werden wie bisher nicht gespeichert (kein Settings-Schlüssel); Bibliothekswechsel setzt sie zurück.
- `GalleryStatusFilterDropDown` ist jetzt der Chip „Ablage“; neuer Chip `GalleryRatingFilterDropDown`; Stile der Chips,
  Segmente und Flyouts in `Gallery/Controls/GalleryFilterStyles.xaml`.

## Alternativen

- **Enum mit Flags** (`[Flags] GalleryFilter`) → verworfen: Dateiart und Ablage sind je ein Wert aus mehreren, keine
  freien Bits; Bewertung passt nicht hinein.
- **Filter als frei zusammenklickbare Regel-Liste** („Feld – Operator – Wert“) → verworfen: mächtiger als nötig, schwerer
  zu bedienen; die feste Filterkarte deckt die gewünschten Fälle ab.
- **Bewertungs-Index jetzt** → vertagt (Migration, Auftrag ohne Migration).

## Referenzen

- Mockup: `mockups/06-galerie-kopfzeile.html` (Reiter „Galerie-Kopfzeile“), abgenommen 2026-10-21
- Code: `src/PhotoViewerV3.Wpf/Gallery/Data/GalleryFilterState.cs`, `Gallery/Data/GalleryPageSource.FileFilter.cs`,
  `Gallery/ViewModels/GalleryViewModel.Filters.cs`, `Gallery/ViewModels/GalleryViewModel.SortBar.cs`,
  `Gallery/Controls/GalleryToolbar.xaml`
- Tests: `tests/PhotoViewerV3.Wpf.Tests/Gallery/GalleryFilterCombinationTests.cs`, `GalleryToolbarLayoutTests.cs`;
  Rauchtest-Schritt „Kombinierte Filter“

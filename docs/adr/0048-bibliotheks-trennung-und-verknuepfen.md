# ADR-0048: Bibliotheken strikt getrennt, Verknüpfen als Einstellung, Ansicht „Alle verknüpften“

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Vorgabe), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ersetzt teilweise „global“ in [ADR-0021](0021-tags-eigene-tabellen.md) (Tags),
  [ADR-0027](0027-kategorien-mit-festen-werten.md) (Kategorien), [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md)
  (Personen) und [ADR-0045](0045-personen-details-und-beziehungen.md) (Beziehungen); [ADR-0042](0042-navigation-und-einstellungen-mit-reitern.md)
  (Umschalter, Einstellungen), [ADR-0030](0030-schreiber-warten-auf-gesperrten-katalog.md) (Schreib-Wiederholung)

## Kontext

Herbert (2026-10-21): „Eine Bibliothek darf die andere, wenn sie geladen ist, nicht sehen können, also von dort nichts
angezeigt werden, außer beide werden verknüpft. Das soll eine eigene Einstellung werden im Bibliothekenmenü. Die
versteckte Bibliothek soll wirklich niemals gemeinsam mit einer anderen angezeigt werden können.“ Gemeinsame Ansicht für
verknüpfte Bibliotheken: ja.

Galerie, Ordner und Suche filterten schon nach der aktiven Bibliothek, aber Tags (ADR-0021), Kategorien mit Werten
(ADR-0027), Personen (ADR-0035) und Beziehungen (ADR-0045) waren global: Bibliothek B zeigte in Flyouts, Einstellungen und
Personen-Seite die Begriffe und Namen aus A (mit 0 Treffern), und die Eindeutigkeit der Namen galt über alle Bibliotheken.

## Entscheidung

1. **Bibliotheks-Gruppen** (Migration `AddLibraryScoping`): Tabelle `LibraryGroups (Id, Name NOCASE, CreatedAtUtc)`,
   `Libraries.GroupId`; jede Bibliothek gehört genau einer Gruppe (Standard: eine Gruppe nur mit ihr selbst, Name = Name der
   Bibliothek). `Tags`, `Categories` (Werte über ihre Kategorie), `Persons` und `PersonRelations` bekommen `GroupId`. Die
   Eindeutigkeiten gelten je Gruppe: `UX_Tags_Group_Name`, `UX_Categories_Group_Name`, `IX_PersonRelations_Group_Name`,
   `IX_Persons_Group_IsMe` (Teilindex „Das bin ich“ je Gruppe), dazu `IX_Persons_Group_Name` (Anzeigename je Gruppe
   eindeutig, im Code geprüft) und `IX_Libraries_Group`. `GroupId` hat **keinen Fremdschlüssel**: Spalten entstehen per
   `ALTER TABLE … ADD COLUMN … NOT NULL DEFAULT 0` — kein Neuaufbau einer Tabelle, der per FK-Kaskade Zuordnungen löschen
   könnte (vgl. ADR-0045); `Down` wirft (Rückweg = Sicherung vor der Migration). GroupId 0 heißt „ohne Bibliothek“ (Begriffe,
   die angelegt wurden, als es noch keine gab): die nächste Bibliothek, die eine Gruppe bekommt, übernimmt sie.
2. **Datenübernahme** (`AddLibraryScoping.AssignGroupsSql`, eine SQL-Folge in der Migrations-Transaktion): Gruppen-Id =
   Bibliotheks-Id. Ein Begriff gehört der Gruppe jeder Bibliothek, die ihn **benutzt** — Tags über `FileTags`, Kategorien
   über `FileCategoryValues` (Kopien bekommen alle Werte), Personen über bestätigte Gesichter, Vorschläge und Ablehnungen,
   Beziehungen über die Personen der Gruppe. Die kleinste Bibliothek behält die Zeile, jede weitere bekommt eine Kopie mit
   expliziter neuer Id (Zuordnungsliste in einer Temp-Tabelle — unabhängig von Namen), Zuordnungen dieser Bibliothek werden
   umgehängt. Titelbilder bleiben nur dort, wo das Gesicht liegt. **Ungenutzte Begriffe bekommt jede Bibliothek**
   (Entscheidung, siehe Begründung). Ohne Bibliothek bleibt alles bei GroupId 0.
3. **Bereichsdienst** `ILibraryScope` (Services/Library): `GetCurrentAsync` liefert `LibraryScopeInfo` (aktive Bibliothek,
   Gruppe, Gruppenname, `LibraryIds` des Bereichs, alle Bibliotheken der Gruppe, `IsAllLinked`) frisch aus dem Katalog (kein
   veraltender Zwischenstand; Libraries ist klein). `GetForLibraryAsync`, `GetGroupsAsync`, `ShowAllLinkedAsync`,
   `RenameGroupAsync`. Eine Bibliothek ohne Gruppe bekommt beim ersten Zugriff ihre eigene (kurze `BEGIN IMMEDIATE`-
   Transaktion mit Wiederholung). **Alle** Abfragen, die zeigen, suchen, zählen, vorschlagen oder lernen, nehmen den
   Bereich: Dateien über `Files.LibraryId IN (Bereich)`, Begriffe über `GroupId = Gruppe`. Schreibwege prüfen in der
   Datenbank, dass Tag/Kategorie/Person zur Gruppe der Datei-Bibliothek gehören (`EXISTS … JOIN Libraries`) — ein Begriff aus
   A landet nie an einer Datei aus B, auch nicht über eine veraltete Id.
4. **Verknüpfen als eigene Einstellung** (`ILibraryLinkService`, Einstellungen › Bibliotheken & Eingänge, je Bibliothek
   „Verknüpfen mit…“ / „Verknüpfung lösen“ / „Gruppe umbenennen…“): Verknüpfen führt die Gruppe der anderen Bibliothek in
   die eigene (Name bleibt) — gleichnamige Tags, Kategorien (+ gleichnamige Werte) und Beziehungen werden eins, die übrigen
   wandern (Reihenfolge dahinter); gleichnamige Personen nur nach Rückfrage im Dialog (Liste mit Häkchen, Standard
   zusammenführen; getrennt gehaltene bekommen den Bibliotheksnamen als Rufnamen-Zusatz, damit Namen je Gruppe eindeutig
   bleiben); „Das bin ich“: die Zielgruppe gewinnt. Lösen gibt der Bibliothek eine neue Gruppe mit **Kopien aller**
   gemeinsamen Begriffe und Personen und hängt die Zuordnungen ihrer Dateien (Tags, Kategorie-Werte, Gesichter, Vorschläge,
   Ablehnungen) auf die Kopien um — nichts geht verloren; Gesichter bleiben bei ihrer Bibliothek. Beides in **einer**
   Transaktion, eigener Lock, und während die **Scan-Sperre** aller betroffenen Bibliotheken gehalten wird (aufsteigend
   belegt). Kein Rückgängig, aber Bestätigung mit Zusammenfassung (Dialog „Verknüpfen mit…“ zeigt, was eins wird und was
   hinzukommt; Lösen zeigt, was kopiert wird). Danach `LibraryGroupsChangedMessage` + `ActiveLibraryChangedMessage`.
5. **Ansicht „Alle verknüpften“**: Der Umschalter zeigt für jede Gruppe mit ≥ 2 Bibliotheken „Alle verknüpften (Name)“
   (Zweitzeile: Bibliotheken · Dateien). Gemerkt in `Settings["Library.ViewAllLinkedGroup"]`; jedes Öffnen einer einzelnen
   Bibliothek (`SetActiveAsync`, Neu anlegen) beendet sie. Galerie (Zähler, Seiten, Suche/FTS, alle Filter, Kategorie-
   Segmente, Personen-/Beziehungs-Flyout), Personen-Seite, Lernen, Ordnerbaum (je Bibliothek ein Kopfknoten, darunter ihre
   Wurzeln), Statusleiste und Tag-/Kategorie-Zähler zeigen dann alle Bibliotheken der Gruppe. Bei einer einzelnen Bibliothek
   bleibt die Galerie-SQL unverändert (`LibraryId = $libId`, gleiche Abfragepläne). Vorschaubilder und Gesichts-Ausschnitte
   liegen im Cache der **eigenen** Bibliothek jeder Datei.
6. **Datei-Operationen in „Alle verknüpften“** (je Aktion entschieden): *Verschieben…*, *In die Bibliothek* (Galerie,
   Mehrfachauswahl) und *Entpacken* sind **gesperrt** (Auswahl kann Dateien mehrerer Bibliotheken enthalten, ein Ziel wäre
   nicht eindeutig; Hinweis). *Eingang hinzufügen…* auf der Ordner-Seite ist **gesperrt** (es gäbe keine eindeutige
   Ziel-Bibliothek; in den Einstellungen geht es je Bibliothek). *In die Bibliothek übernehmen…* im Ordnerbaum und *Neuer
   Ordner* sind **erlaubt** — Ziel ist die Bibliothek des Ordners. *Ins Archiv* aus der Lightbox (eine Datei) ist erlaubt —
   Ziel ist ihre Bibliothek. Der *Sortierer* arbeitet auf der aktiven Bibliothek (seine Seite zeigt deren Wurzeln, das Ziel
   ist sichtbar). Alles, was je Datei wirkt (Favorit, Bewertung, Tags, Kategorien, Personen, Datum, Papierkorb), bleibt.
7. **Versteckte Bibliotheken** (Agent U): `ICatalogContext.IsHidden` (Standard `CatalogContext`, `SetHidden`). In einem
   versteckten Katalog (genau eine Bibliothek) wirft `ILibraryLinkService` `LibraryLinkNotAllowedException`,
   `ShowAllLinkedAsync` ebenso, die Einstellung ist ausgeblendet und der Umschalter bietet nie „Alle verknüpften“. Das
   Datenmodell funktioniert dort unverändert (eine Bibliothek, eine Gruppe).
8. **Nebenbefund behoben**: `LastOpenedAt` wird in ganzen Sekunden gespeichert; zwei Wechsel in derselben Sekunde ließen die
   höhere Id statt der zuletzt gewählten Bibliothek aktiv. `SetActiveAsync`/`CreateAsync`/`ShowAllLinkedAsync` setzen
   jetzt mindestens eine Sekunde nach dem jüngsten anderen `LastOpenedAt`.

## Begründung

- **Gruppen statt Paar-Verknüpfungen**: Verknüpfen ist transitiv gemeint (A–B und B–C teilen dieselben Begriffe); eine Gruppe
  pro Bibliothek macht jede Abfrage zu „GroupId = x“ bzw. „LibraryId IN (…)“ und braucht keine Graph-Logik.
- **Begriffe je Gruppe statt Sichtbarkeitsfilter auf globalen Begriffen**: nur so können zwei getrennte Bibliotheken
  gleichnamige Begriffe mit eigener Farbe/Reihenfolge haben, und die Eindeutigkeit bleibt in der Datenbank geschützt.
- **Ungenutzte Begriffe → jede Bibliothek** statt „nur die aktive“: die aktive Bibliothek ist ein zufälliger UI-Zustand im
  Moment des Updates; Herbert soll seine vorbereiteten Begriffe in jeder Bibliothek wiederfinden. Mit einer Bibliothek (sein
  heutiger Stand) ändert sich nichts; überzählige Kopien lassen sich je Bibliothek löschen, verlorene nicht wiederherstellen.
- **Kein Fremdschlüssel auf GroupId**: ein FK hätte Tabellen-Neuaufbauten (Kaskaden-Risiko, Erfahrung 0.2.47) und beim
  Verknüpfen/Lösen Reihenfolge-Zwänge erzwungen; die wenigen Schreibwege liegen gebündelt in `LibraryGroupStore`/
  `LibraryLinkStore`, und die Schreibprüfungen schützen die Zuordnungen.
- **Bereich frisch lesen statt zwischenspeichern**: Bibliothekswechsel, Verknüpfen und Lösen ändern ihn; ein veralteter
  Zwischenstand wäre genau die Art Leck, die die Vorgabe verbietet. Die Abfrage kostet < 1 ms.
- **Lernen nur im Bereich**: Vorschläge aus einer anderen (nicht verknüpften) Bibliothek wären ein Leck über Namen; in
  „Alle verknüpften“ lernt das Modell aus allen Bibliotheken der Gruppe (gemeinsame Personen).

## Konsequenzen

### Positiv
- Keine Spur von A in B: Listen, Flyouts, Einstellungen, Suche, Zähler, Vorschläge, Gruppen-Vorschläge, Ausreißer-Prüfung.
- Verknüpfen/Lösen verlustfrei; gemeinsame Ansicht ohne Kopieren von Dateien.
- Grundlage für versteckte Bibliotheken (eigener Katalog, Verknüpfen technisch ausgeschlossen).

### Negativ
- „Alle verknüpften“ sortiert über `LibraryId IN (…)` — die Indizes `IX_Files_Library_*` liefern die Reihenfolge dann nicht
  mehr direkt (zusätzliches Sortieren); bei Herberts Größen unkritisch, nicht gemessen.
- Der Gesichter-Index und die Ordnerüberwachung arbeiten weiter nur auf der aktiven Bibliothek; in „Alle verknüpften“
  werden die übrigen Bibliotheken erst indiziert/überwacht, wenn sie einzeln geöffnet werden.
- Getrennt gehaltene gleichnamige Personen bekommen einen Rufnamen-Zusatz (sichtbar, änderbar).

### Neutral
- Bibliothek entfernen: war sie die letzte ihrer Gruppe, gehen Gruppe und ihre Begriffe mit (sie wären sonst unsichtbar).
- Neue Agenten mit eigenen Begriffstabellen (Orte V/W, Motive X) bekommen `GroupId` nach demselben Muster.

## Alternativen

- **Begriffe global lassen, nur nach Treffern filtern** → verworfen: Namen aus A blieben in Einstellungen/Vorschlägen
  sichtbar, gleichnamige Begriffe zweier Bibliotheken wären nicht unterscheidbar.
- **`LibraryId` an jedem Begriff + Verknüpfungstabelle** → verworfen: jede Abfrage bräuchte die transitive Hülle, Verknüpfen
  hieße Begriffe je Bibliothek doppelt pflegen.
- **Ungenutzte Begriffe nur der aktiven Bibliothek geben** → verworfen (siehe Begründung).
- **Datei-Operationen in „Alle verknüpften“ mit Ziel-Auswahl** → verworfen für Verschieben/In die Bibliothek/Entpacken:
  eine gemischte Auswahl bräuchte je Bibliothek ein Ziel; Sperre mit Hinweis ist eindeutig.

## Referenzen

- Code: `Services/Library/ILibraryScope.cs`, `LibraryScope.cs`, `LibraryGroupStore.cs`, `ILibraryLinkService.cs`,
  `LibraryLinkService.cs`, `LibraryLinkStore.cs`, `ICatalogContext.cs`; `Data/Migrations/*_AddLibraryScoping.cs`;
  `Wpf/Gallery/Data/GalleryPageSource.Scope.cs`, `Wpf/Gallery/ViewModels/GalleryViewModel.Scope.cs`,
  `Wpf/Library/LibraryViewModel.Linking.cs`, `Wpf/Library/Linking/*`, `Wpf/Shell/LibrarySwitcherViewModel.cs`,
  `Wpf/Folders/FolderTreeViewModel.cs`
- Tests: `LibraryScopingTests`, `LibraryScopingMigrationTests`, `GalleryLibraryScopeTests`; Rauchtest-Schritt
  „Bibliotheks-Trennung“

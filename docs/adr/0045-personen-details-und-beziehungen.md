# ADR-0045: Personen mit Vor-/Nachname, Rufname, eigener Beziehung, Geburtsdatum und „Das bin ich“

- **Status:** Accepted; teilweise ersetzt durch [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) — Beziehungen,
  Anzeigenamen und „Das bin ich“ gelten je Bibliotheks-Gruppe statt global (0.2.51)
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0035](0035-gesichtserkennung-buffalo-l-directml.md) (Tabelle `Persons`),
  [ADR-0037](0037-gesichter-zuordnen-personen-seite.md), [ADR-0038](0038-personen-in-galerie-und-lightbox.md),
  [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md)

## Kontext

Eine Person hatte nur einen `Name`. Herbert will Vorname und Nachname getrennt, optional einen Rufnamen („Opa Franz“),
eine Beziehung aus seiner Sicht und ein (auch ungefähres) Geburtsdatum; eine Person ist er selbst. Das bereitet einen
späteren Assistenten vor („Fotos mit meiner Schwester aus meiner Kindheit bis 20“ — Alter auf einem Foto berechnen).
Beziehungen sind ausdrücklich **keine** feste Liste („Cousin mütterlicherseits“, „Taufpate“). `Persons.Name` lesen viele
Stellen: Galerie-Suche, Personen-Filter, Listenspalte, Kachel-Tooltip, Lightbox-Rahmen, Personen-Seite.

## Entscheidung

1. **Schema** (Migration `AddPersonDetails`): `Persons` + `FirstName`, `LastName` (TEXT NOT NULL DEFAULT '', NOCASE),
   `Nickname` (NULL, NOCASE), `RelationId` (NULL → `PersonRelations` ON DELETE SET NULL), `BirthYear`/`BirthMonth`/
   `BirthDay` (NULL), `IsMe` (NOT NULL DEFAULT 0). Neue Tabelle `PersonRelations (Id, Name NOCASE)`. Indizes:
   `IX_PersonRelations_Name` (eindeutig, damit ohne Groß-/Kleinschreibung), `IX_Persons_Relation` (Filter),
   `IX_Persons_IsMe` (eindeutiger Teilindex `WHERE IsMe = 1` — höchstens eine Person „Das bin ich“). Die Spalte
   `RelationId` entsteht per `ALTER TABLE … ADD COLUMN … REFERENCES` statt `AddForeignKey`: Letzteres ließe EF die Tabelle
   `Persons` neu aufbauen (`DROP TABLE`), was bei eingeschalteten Fremdschlüsseln Zuordnungen in `Faces` auf NULL setzen
   und `FaceRejections` löschen würde. Aus demselben Grund hat die Migration **keinen Rückweg** (`Down` wirft) — zurück
   geht es über die Sicherung, die der Start vor jeder Migration anlegt (PLAN Kap. 14).
2. **`Persons.Name` bleibt** als synchron gehaltener **Anzeigename**: Rufname, sonst „Vorname Nachname“
   (`Domain/Entities/PersonNames.DisplayName`). Jeder Schreibweg (neu, bearbeiten, alte Einzelnamen-Schnittstelle)
   setzt ihn mit. Er bleibt ohne Groß-/Kleinschreibung eindeutig (wie bisher, im Code geprüft) — zwei Personen mit
   gleichem Vor- und Nachnamen unterscheidet ein Rufname. Damit funktionieren Liste, Filter, Lightbox, „vorhandene Person
   beim Benennen“ und Zusammenführen unverändert.
3. **Bestand**: die Migration teilt `Name` am **ersten Leerzeichen** (erstes Wort = Vorname, exakter Rest = Nachname,
   ein Wort = nur Vorname); `Name` bleibt stehen → verlustfrei (`Vorname + ' ' + Nachname = Name`). Die alte
   Schnittstelle `CreatePersonAsync(name)`/`RenamePersonAsync(name)` (Lightbox „Neue Person…“) teilt genauso (mehrfache
   Leerzeichen werden dort zu einem, damit gespeicherter und geprüfter Name gleich sind); Umbenennen per Einzelname löscht
   den Rufnamen. Bearbeiten auf einen vergebenen Anzeigenamen führt nach Rückfrage zusammen und übernimmt die
   eingegebenen Angaben für die verbleibende Person.
4. **Beziehung als Freitext**: beim Speichern wird eine vorhandene Beziehung gleichen Namens (NOCASE) verwendet —
   Schreibweise der ersten Verwendung bleibt —, sonst angelegt; leer = keine. Ungenutzte Beziehungen bleiben stehen (wie
   Tags) und werden in den Einstellungen gelöscht. Verwaltung: Einstellungen › Gesichter › **Personen & Beziehungen**
   (umbenennen; auf einen vorhandenen Namen → Rückfrage „zusammenführen“; zusammenführen; löschen → Personen behalten
   leere Beziehung).
5. **Geburtsdatum ungefähr**: Jahr allein, Jahr + Monat oder vollständig (`PersonBirthDate`, Jahr 1800 bis heute, Tag
   nur mit Monat, gültiger Kalendertag). Alter am Stichtag als **Spanne** (`AgeOn`: „etwa 45–46 Jahre“ bei nur Jahr) —
   Grundlage für das spätere Alter auf einem Foto. Heute nur speichern, anzeigen (Detailansicht: „geboren März 1975
   (51 Jahre)“) und bearbeiten.
6. **Dienst**: `IPersonDetailsService` wird vom `FaceRecognitionService` umgesetzt (partial
   `FaceRecognitionService.Details.cs`) — derselbe Single-Writer und dieselbe `PersonsChangedMessage`; Details ändern
   keine Zuordnung, also kein Neu-Lernen. Zusammenführen zweier Personen: das Ziel behält seine Namen, fehlende
   Beziehung und fehlendes Geburtsdatum kommen von der Quelle, „Das bin ich“ wandert mit. `PersonSummary` trägt die
   Details als `Details` (init-Eigenschaft, Positionsparameter unverändert).
7. **Oberfläche**: Dialog „Als neue Person benennen“ / „Person bearbeiten“ (`PersonDetailsDialog`, Logik im testbaren
   `PersonDetailsEditor`): Vorname, Nachname, Rufname, Beziehung (editierbares Auswahlfeld mit den vorhandenen
   Beziehungen, Autovervollständigung ohne Groß-/Kleinschreibung), Geburtsdatum (Tag/Monat optional), „Das bin ich“
   (Hinweis, wer es bisher war), Vorschau des Anzeigenamens und Hinweis, wenn er schon vergeben ist. Personen-Seite:
   Suche (Name, Vor-/Nachname, Rufname, Beziehung), Sortieren nach Anzeigename / Nachname / Anzahl Fotos, **nach
   Beziehung gruppieren** (Abschnitte, „Ohne Beziehung“ am Ende; nicht virtualisiert, Titelbilder für alle angefordert,
   Kacheln bleiben Blatt-Peers), Kachel zeigt die Beziehung, Detailzeile mit allen Feldern + „Bearbeiten…“.
8. **Galerie**: Suche findet zusätzlich Vorname, Nachname, Rufname und Beziehung (bestätigte Gesichter). Personen-
   Flyout: Suche über dieselben Felder, neuer Abschnitt **Beziehung** (Häkchen mit Fotozahl, ODER untereinander, UND mit
   den Personen und allen anderen Filtern; „auch Vorschläge“ gilt mit). Der Beziehungs-Teil reist in
   `GalleryPersonFilter.RelationIds` mit (eine Abfrage-Stelle für alle Pfade), steht als eigene Bedingung in „Es gilt“
   („Beziehung „Schwester““, eigenes ×) und im Chip-Wert. „Leeren“ im Flyout leert beides, × an „Person …“ nur die
   Personen.

## Begründung

- `Name` als Anzeigename behalten statt überall Vor-/Nachname zu lesen: ein Feld, ein Index, keine Änderung an FTS-freien
  Such-SQLs, Listen und Lightbox (Datei-Besitz paralleler Agenten); die Details sind Zusatz.
- Beziehung als eigene Tabelle statt Freitext je Person: Umbenennen/Zusammenführen wirkt auf einen Schlag bei allen
  Personen, Vorschläge sind eine kleine Abfrage, der Filter arbeitet über Ids und `IX_Persons_Relation`.
- Teilindex für `IsMe`: die Datenbank schützt die Eindeutigkeit auch gegen Fehler im Code; der Dienst nimmt die
  Markierung vorher anderen ab.
- Suche per `LIKE '%…%'` über die kleine `Persons`-Tabelle (hunderte Zeilen) — ein Index hilft dort nicht, die
  Gesichter laufen weiter über `IX_Faces_Person_State_File`.

## Konsequenzen

- Lightbox „Neue Person…“ fragt weiter nur einen Namen (geteilt am ersten Leerzeichen); Details pflegt Herbert danach
  auf der Personen-Seite. Ein späterer Assistent kann `BirthYear/Month/Day` + `IsMe` + Aufnahmedatum kombinieren.
- „Gesichtsdaten löschen“ löscht Personen, nicht die Beziehungen (sie sind Benutzer-Vokabular wie Tags).

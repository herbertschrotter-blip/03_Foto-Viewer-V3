# ADR-0038: Personen in Galerie und Lightbox — Filter direkt in SQL über abdeckende Indizes, nur Bestätigtes zählt; Gesichtsdaten je Bibliothek löschen

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Auftrag Gesichtserkennung Teil 4), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage)
- **Bezogen auf:** ADR-0035 (Schema Faces/Persons, `FaceState`), ADR-0036 (Lernen nur aus Bestätigtem), ADR-0021/0027
  (Tag-/Kategorie-Filter und Suche als Muster), ADR-0031 (Kategorie-Sortierung), ADR-0010 (Paging)

## Kontext

Nach Erkennung (Teil 1), Lernen (Teil 2) und Zuordnungsdienst/Personen-Seite (Teil 3, parallel) sollen Personen dort
erscheinen, wo Herbert Fotos sucht: Galerie-Filter, Suche, Listenspalte, Kachel und Lightbox. Bibliotheken bis 500 000
Fotos, ≈ 1,1 Gesichter je Foto mit Gesicht. In `Faces` liegen `State`, `PersonId` und `SuggestedPersonId` im Datensatz
**hinter** dem 2-KB-Fingerabdruck — jede Prüfung über die Tabellenzeile liest dessen Überlaufseiten. Personen sind
global (wie Tags), Gesichter hängen an Dateien einer Bibliothek. Datenschutz: Gesichtsdaten müssen sich vollständig
entfernen lassen, ohne die Fotos anzufassen.

## Entscheidung

1. **Als Person zählt nur ein bestätigtes Gesicht** (`State = 2` mit `PersonId`). Vorschläge (`State = 1`) zählen in
   Filtern nur mit der Option **„auch Vorschläge“** (Standard aus), erscheinen in der Liste kursiv/grau mit „?“ und nie
   im Kachel-Symbol. „Unbenannt“ = unbestätigt oder Vorschlag (mit „auch Vorschläge“: nur unbestätigt); ignorierte
   Gesichter (`State = 3`) zählen nirgends. „Ohne Person“ meint nur Fotos (`MediaType = 0`) — Videos und Archive
   analysiert die Erkennung nicht; noch nicht analysierte Fotos gehören dazu.
2. **Filter, Suche, Spalte direkt in SQL** im Lesepfad der Galerie (`GalleryPageSource.Persons.cs`), ohne den
   Zuordnungsdienst: ODER/UND als `f.Id IN (…)` über die Personen-Ids (UND per `GROUP BY … HAVING COUNT(DISTINCT) = n`),
   „ohne Person“/„unbenannte Gesichter“ als korreliertes `NOT EXISTS`/`EXISTS` je Datei, Suche als weiterer
   `UNION`-Zweig (Personen-Ids aus der kleinen `Persons`-Tabelle per `LIKE`, wie Tags/Kategorien). Der Filter wirkt auch
   in der segmentweisen Kategorie-Sortierung (ADR-0031). Mehrere Filter (Typ, Status, Tags, Kategorien, Personen) sind
   UND-verknüpft.
3. **Migration `AddFacePersonIndex`** mit drei abdeckenden Indizes: `IX_Faces_Person_State_File (PersonId, State,
   FileId)`, `IX_Faces_SuggestedPerson_State_File (SuggestedPersonId, State, FileId)`, `IX_Faces_File_State_Person
   (FileId, State, PersonId, SuggestedPersonId)`. Die bestehenden Einzelspalten-Indizes bleiben (Tests/Kaskaden von
   ADR-0035 verlassen sich darauf). Tests prüfen per `EXPLAIN QUERY PLAN`, dass kein Plan `SCAN fa/fs/fp` enthält.
4. **Lightbox**: Taste **F** schaltet Gesichtsrahmen (Vollbild nur noch F11). Die Rahmen liegen als Overlay im
   `ZoomPanControl` (`IZoomPanOverlay` bekommt nach jedem Zoom/Schwenk das Bildrechteck) — Mausrad und Ziehen außerhalb
   der Rahmen bleiben beim Bild. Lesen und Schreiben (bestätigen, ablehnen, neue Person, ignorieren) laufen über
   `IFaceRecognitionService` (P1); die Logik steckt in `LightboxFacesViewModel` (ohne LibVLC testbar).
5. **„Gesichtsdaten löschen…“** (`IFaceDataEraser`, Einstellungen → Gesichtserkennung, nur nach Bestätigung im
   AppDialogWindow): schaltet den Index aus, löscht in EINER Transaktion für die aktive Bibliothek Gesichter samt
   Fingerabdrücken, FaceScans und Ablehnungen, dazu Personen, die danach in keiner Bibliothek mehr ein Gesicht oder einen
   Vorschlag haben, setzt verwaiste `CoverFaceId` auf NULL und löscht den Gesichter-Cache `thumbs\{libraryId}\faces\`.
   Fotos, Bewertungen, Tags, Kategorien und Vorschaubilder bleiben.

## Begründung

- **Nur Bestätigtes** passt zu ADR-0036 (das Modell lernt nur daraus) und zu Herberts Sorge vor falschen Zuordnungen:
  ein Filter „Anna“ darf keine Fotos zeigen, auf denen nur die Maschine Anna vermutet.
- **SQL statt Dienst**: die Galerie liest seitenweise (≤ 1 000 Zeilen) über eine eigene Verbindung; Filter müssen in
  Zählung und Seite wirken. Eine Personen-Liste aus dem Dienst ließe sich nicht in die Abfrage einbauen, ohne alle
  Datei-Ids zu laden.
- **Abdeckende Indizes**: gemessen an 200 000 Dateien / 98 000 Gesichtern / 200 Personen (430 MB DB, ohne ANALYZE):
  Personen-Filter ODER zählen 22 ms, + Vorschläge 21 ms, UND 18 ms; „ohne Person“ 72 ms (182 000 Treffer), „unbenannte
  Gesichter“ 59 ms; Suche nach Personennamen 8 ms; Namen für 500 Dateien (Spalte/Kachel) 2–11 ms; Personen-Liste mit
  Zählern 93 ms. Alle Pläne `SEARCH … USING COVERING INDEX`. „Ohne Person“/„unbenannt“ prüfen je Datei — linear in der
  Dateizahl, bei 500 000 Fotos ≈ 0,2 s, aber ohne Tabellenzugriff.
- **Löschen je Bibliothek, Personen global**: Personen sind bibliotheksübergreifend; eine Person, die noch in einer
  anderen Bibliothek vorkommt, darf nicht verschwinden.

## Konsequenzen

### Positiv
- Filter, Suche und Spalte funktionieren sofort nach Bestätigungen (PersonsChangedMessage) und ohne Neuanalyse.
- Gesichtsdaten lassen sich mit einem Knopf vollständig entfernen; nichts verlässt den Rechner.

### Negativ
- Drei zusätzliche Indizes auf `Faces` (≈ 20–30 Bytes je Gesicht und Index; bei 560 000 Gesichtern ≈ 40 MB) und etwas
  mehr Schreibaufwand im Hintergrund-Index.
- Taste F wechselt die Bedeutung (vorher Vollbild).
- „Gesichtsdaten löschen“ wartet nach dem Löschen 1,5 s und räumt nach, falls ein Schreib-Stapel des Index, der vor dem
  Ausschalten begann, erst danach committet (der Index hat keine „warte bis gestoppt“-Schnittstelle).
- Die Listenspalte sortiert nur innerhalb der Seite (wie Tags).

### Neutral
- Bis zum Merge mit P1 liefert `InterimFaceRecognitionService` (Wpf/Lightbox/Faces) eine minimale SQL-Umsetzung des
  Dienstes, angeboten über `FaceRecognitionForwarder` (DispatchProxy, implementiert das Interface nicht selbst) und nur
  per `TryAdd` registriert — P1s Registrierung gewinnt; beim Merge wird nur `_P2Contract.cs` gelöscht. Der Ersatz kann
  später entfallen.
- Nach dem Löschen ruft der Eraser `IFaceRecognitionService.InvalidateAll()` und `IFaceCropService.DeleteAll()` (P1);
  der Gesichter-Cache liegt wie bei P1 unter `thumbs\{libraryId}\faces\` und wird zusätzlich direkt gelöscht.

## Alternativen

- **Vorschläge standardmäßig mitzählen** → verworfen (falsche Treffer, widerspricht ADR-0036).
- **Personen-Filter über den Zuordnungsdienst (Datei-Id-Listen)** → verworfen: zählt nicht in SQL, sprengt Paging.
- **Einzelspalten-Indizes ersetzen statt ergänzen** → verworfen: ADR-0035-Tests und Kaskaden nutzen sie; Ersparnis klein.
- **Rahmen als eigene Ebene über dem ZoomPanControl** → verworfen: fängt Mausrad/Ziehen ab bzw. bekäme sie nicht.

## Referenzen

- Code: `src/Lumora.Wpf/Gallery/Data/GalleryPageSource.Persons.cs`, `Gallery/ViewModels/GalleryViewModel.Persons.cs`,
  `Gallery/People/`, `Lightbox/Faces/`, `Lightbox/Controls/FaceBoxesLayer.cs`, `Settings/FaceData/`,
  `src/Lumora.Services/Faces/Erase/FaceDataEraser.cs`, Migration `20261021062846_AddFacePersonIndex`
- Tests: `tests/Lumora.Wpf.Tests/Persons/`, `tests/Lumora.Tests/Faces/FaceDataEraserTests.cs`

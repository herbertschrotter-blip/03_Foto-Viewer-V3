# ADR-0037: Gesichter zuordnen — Zuordnungsdienst, Lebenszyklus der Vorschläge, Gruppen-Cache, Personen-Seite

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Lernen nur aus Bestätigtem, Gruppen für den Einstieg, Schutz vor „lernt falsch“),
  Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ADR-0035 (Erkennung, Schema, Hintergrund-Index), ADR-0036 (Lernen und Gruppieren), ADR-0030
  (Schreiber warten auf den Katalog), ADR-0017 (Messenger), PLAN Phase 9

## Kontext

Teil 1 (ADR-0035) schreibt Gesichter mit Fingerabdruck in den Katalog, Teil 2 (ADR-0036) liefert reine Bausteine zum
Lernen (`IFaceRecognizer`, `IFaceModel.Suggest`, `CheckOutlier`) und Gruppieren (`IFaceClusterer`) — ohne DB und ohne
DI. Es fehlt der Dienst, der beides mit dem Katalog verbindet (wann wird gelernt, wann werden Vorschläge geschrieben,
was passiert bei Korrekturen), und die Oberfläche zum Benennen. Herberts Sorge: „wenn in der Gruppe was anderes drin ist,
lernt es falsch.“ Abgesprochen sind: strenge Gruppen, Gruppe nach Ähnlichkeit sortiert mit markierten Ausreißern am Ende,
Falsches per Klick entfernen, nur Bestätigtes lernt, Ausreißer-Rückfrage beim Bestätigen, nach Korrektur Neu-Lernen.
Parallel nutzt die Galerie/Lightbox (anderer Agent) dieselben Daten über eine vereinbarte Schnittstelle.

## Entscheidung

1. **Ein Zuordnungsdienst** `IFaceRecognitionService` (`Services/Faces/Assignment`, `AddFaceRecognition()` registriert
   ihn samt `FaceRecognizer`/`FaceClusterer` als Singletons). Er ist die einzige Stelle, die Zustände 1–3, Personen,
   Ablehnungen und Titelbilder schreibt; die Oberfläche (Personen-Seite, Galerie, Lightbox) ruft nur ihn.
   Vereinbarte Schnittstelle für Galerie/Lightbox: `GetFacesForFilesAsync`, `GetPersonsAsync` (`PersonSummary`),
   `ConfirmAsync`, `ConfirmNewPersonAsync`, `RejectSuggestionAsync`, `IgnoreFaceAsync`, Nachricht
   `PersonsChangedMessage(LibraryId)` nach jeder Änderung.
2. **Modell im Speicher, je aktiver Bibliothek.** Gelernt wird aus den bestätigten Gesichtern (Zustand 2) der aktiven
   Bibliothek mit der dort häufigsten Modell-Kennung (`Faces.ModelVersion`) — Fingerabdrücke anderer Kennungen sind
   nicht vergleichbar und lernen nicht mit (ADR-0035). Ablehnungen kommen aus `FaceRejections`.
3. **Lebenszyklus der Vorschläge** (ein Hintergrund-Worker, entprellt 1,5 s, erster Lauf 15 s nach dem Start):
   - *Neu lernen* nach jeder Bestätigung, Entfernung, Ablehnung, Zusammenführung, Löschung, beim Wechsel der Bibliothek
     und beim Start → danach Vorschläge für **alle** offenen Gesichter (Zustand 0/1) stapelweise (2 000 je Stapel, eine
     Transaktion je Stapel). Kommt währenddessen eine neue Korrektur, bricht der Durchgang ab und beginnt mit dem neuen
     Modell von vorn.
   - *Index-Stapel* (`FacesChangedMessage`) bekommen nur Vorschläge für ihre Dateien (kein Neu-Lernen).
   - Vorschlag = `Suggest` mit den Ablehnungen des Gesichts als Ausschluss; „unbekannt“ → Zustand 0 ohne Person.
     Geschrieben wird nur, was sich ändert, und nur per **bedingtem UPDATE** (Zustand noch 0/1, Person nicht inzwischen
     abgelehnt): eine Benutzerentscheidung zwischen Lesen und Schreiben gewinnt immer. Eigene Vorschläge werden nie
     Lernbeispiel (ADR-0036).
   - Hintergrund-Schreiben wartet still (eigener Messenger wie der Index), Benutzeraktionen melden das Warten in der
     Statusleiste (ADR-0030). Ein Schreiber des Dienstes zur Zeit (`SemaphoreSlim`). Face-Ids bleiben über „Neu
     aufbauen“ stabil (ADR-0035) — anders als bei Bewertungen braucht es keine Umrechnung der Ziele (`CatalogFileTargets`).
4. **Operationen:** Bestätigen (auch mehrere, eine Transaktion; löscht eine alte Ablehnung derselben Person), Vorschlag
   ablehnen (`FaceRejections` + sofort der nächstbeste Kandidat aus dem vorhandenen Modell, ohne auf das Training zu
   warten), aus Person entfernen (zurück auf 0 **plus** Ablehnung dieser Person — sie wird für das Gesicht nicht wieder
   vorgeschlagen), ignorieren (Zustand 3), Titelbild (nur bestätigtes Gesicht der Person), umbenennen (Namen ohne
   Groß-/Kleinschreibung eindeutig, `PersonNameTakenException`), zusammenführen (Gesichter, Vorschläge, Ablehnungen →
   Ziel, Titelbild übernehmen, Quelle löschen), ausblenden, löschen (Gesichter zurück auf 0, Ablehnungen per Kaskade).
   Ein Titelbild auf ein Gesicht, das die Person verliert, wird gelöst; ohne gesetztes Titelbild zeigt die Person ihr
   bestätigtes Gesicht mit der besten Qualität.
5. **Ausreißer-Prüfung vor dem Bestätigen** (`CheckOutliersAsync`): für eine bekannte Person `CheckOutlier` (ADR-0036);
   für eine **neue** Person Rückfrage, wenn ein Gesicht sicher einer **bekannten** Person ähnelt. Die Oberfläche fragt
   bei Treffern nach: „Ohne diese bestätigen“ / „Trotzdem alle“ / „Abbrechen“ — die Person wird erst danach angelegt.
6. **Gruppen** über die unbekannten Gesichter (Zustand 0 — Gesichter mit Vorschlag stehen bei ihrer Person, ignorierte
   und bestätigte nie), Mindestqualität wie beim Lernen, **abschnittsweise je 50 000** (neueste zuerst; ADR-0036: O(n²)).
   Das Ergebnis bleibt **zwischengespeichert**: eigene Zuordnungen nehmen Gesichter sofort heraus (Gruppen unter 3
   Gesichtern fallen weg), neue Index-Gesichter, Löschungen, Entfernen und weggefallene Vorschläge machen es ungültig →
   neu berechnet beim nächsten Abruf (Seite öffnen, „Aktualisieren“). Während einer Berechnung herausgenommene
   Gesichter werden auf das neue Ergebnis angewandt. Jede Gruppe bekommt beim Abruf einen Namensvorschlag über
   `Suggest` auf ihr Zentrum.
7. **Gesichts-Ausschnitte** 128 × 128 (Box + 30 % Rand, quadratisch, ins Bild geschoben) im Thumbnail-Ordner
   `thumbs\{libraryId}\faces\{faceId}.jpg`; gerendert im Wpf-Projekt über WIC (`WicFaceCropRenderer` hinter
   `IFaceCropRenderer`, je Quelldatei ein Dekodieren, gerade so groß wie für das kleinste Gesicht nötig), angefordert
   nur für den sichtbaren Bereich der virtualisierten Listen, fertige Bilder gebündelt mit Background-Priorität an den
   UI-Thread. Index schreibt eine Datei neu → ihre Ausschnitte werden verworfen (Box kann sich geändert haben);
   Ausschnitte ohne Gesichter-Zeile (gelöscht, Kaskade) und alte `.tmp` räumt ein entprellter Abgleich weg;
   `DeleteAll()` löscht alle (für „Gesichtsdaten löschen“). Nicht lesbare Quellen werden bis zum nächsten Scan nicht
   erneut versucht.
8. **Personen-Seite** (`Wpf/Persons`, Navigation „Personen“): Übersicht mit benannten Personen und unbenannten
   Gruppen, Detailansicht (Vorschläge bestätigen/ablehnen einzeln, für die Auswahl und „alle sichtbaren“; bestätigte
   entfernen, Titelbild, umbenennen, zusammenführen, ausblenden, löschen; Doppelklick → Lightbox), Gruppenansicht
   (nach Nähe sortiert, **Ausreißer am Ende rot markiert und abgewählt**; benennen, zu Person hinzufügen mit
   Vorschlag, ignorieren). Kachel-Listen = `TileListBox` (Blatt-Peers) + `VirtualizingTilePanel` (CLAUDE.md), Fenster nur
   über `AppDialogWindow`/`AppFluentWindow` (`PersonPickerDialog`). Kopfzeile mit Indexstand und Hinweis, wenn die
   Erkennung aus ist (Knopf „Einstellungen öffnen“). Die Seite arbeitet auch bei ausgeschalteter Erkennung mit den
   vorhandenen Gesichtern.

## Begründung

- **Ein Dienst statt Logik in den Seiten:** Galerie, Lightbox und Personen-Seite schreiben dieselben Zustände; nur ein
  Schreiber mit bedingten Updates kann garantieren, dass kein Hintergrund-Vorschlag eine Bestätigung überschreibt.
- **Vollständiges Neu-Lernen nach jeder Korrektur** (entprellt) ist nach Spike 10 billig (10 000 Beispiele ≈ 0,2 s) und
  setzt Herberts Forderung „nach Korrektur Neu-Lernen“ ohne Sonderfälle um. Vorschläge für alle offenen Gesichter
  kosten bei 500 000 Gesichtern einige zehn Sekunden im Hintergrund und brechen bei neuer Korrektur ab.
- **Sofortiger Nachrücker beim Ablehnen** ohne Training: Herbert sieht sofort den nächsten Kandidaten; das spätere
  Training bestätigt ihn (Ablehnung ist Trainingseingabe).
- **Entfernen = Ablehnung:** wer ein Gesicht aus „Anna“ nimmt, sagt „das ist nicht Anna“ — ohne die Ablehnung schlüge
  das nächste Training es sofort wieder vor.
- **Gruppen nur aus Zustand 0:** Gesichter mit Vorschlag haben schon eine Antwort; doppelt gezeigt würden sie zweimal
  entschieden. Das zieht bekannte Personen vor dem O(n²)-Gruppieren ab (ADR-0036, „vorher per Vorschlag abziehen“).
- **Ausreißer abgewählt statt entfernt:** Herbert sieht sie und kann sie mit einem Klick dazunehmen; die Rückfrage beim
  Bestätigen fängt zusätzlich Fälle, die das Gruppenzentrum nicht verrät.
- **Ausschnitte als Dateien im Thumbnail-Ordner:** gleiche Lebensdauer und Aufräumwege wie Vorschaubilder (Umzug,
  „Neu aufbauen“ löscht den Bibliotheksordner); nur sichtbare Kacheln kosten Dekodierzeit.

## Konsequenzen

### Positiv
- Benennen einer Gruppe führt in Sekunden zu Vorschlägen auf allen Fotos; jede Korrektur wirkt sofort und dauerhaft.
- Keine Benutzerentscheidung geht durch Hintergrundarbeit verloren (bedingte Updates, Test).
- Galerie und Lightbox brauchen nur den Dienst und eine Nachricht.

### Negativ
- Das Modell gilt je aktiver Bibliothek; Personen sind global, lernen aber nur aus Gesichtern der aktiven Bibliothek.
- Neue Index-Gesichter tauchen in den Gruppen erst nach „Aktualisieren“ bzw. erneutem Öffnen der Seite auf (bewusst:
  Gruppen springen nicht, während Herbert entscheidet).
- Abschnittsweises Gruppieren trennt dieselbe Person über Abschnittsgrenzen (erst ab 50 000 unbekannten Gesichtern).
- Schwellen stammen weiter aus synthetischen Daten (ADR-0036) — Nachjustieren an echten Fingerabdrücken steht aus.

## Alternativen

- **Lernen/Schreiben in der Seite** (ViewModel ruft Recognizer direkt): verworfen — mehrere Schreiber, kein
  gemeinsamer Lebenszyklus für Galerie/Lightbox.
- **Inkrementelles Lernen** (Beispiel hinzufügen statt neu trainieren): verworfen — Korrekturen müssten Beispiele
  gezielt entfernen; ein vollständiger Neuaufbau ist schnell genug und hinterlässt keinen Rest (ADR-0036).
- **Gruppen persistieren** (Tabelle): verworfen — Gruppen sind Vorschläge, ändern sich mit jedem Index-Stapel; der
  Zwischenspeicher im Speicher reicht.
- **Ausschnitte im Speicher** statt als Dateien: verworfen — bei tausenden Gesichtern zu viel RAM, kein Nutzen über
  Sitzungen hinweg.
- **Ausreißer automatisch aus der Gruppe nehmen:** verworfen — Herbert soll sehen, was zweifelhaft ist, und selbst
  entscheiden.

## Referenzen

- Code: `src/PhotoViewerV3.Services/Faces/Assignment/` (`IFaceRecognitionService`, `FaceRecognitionService`,
  `FaceAssignmentStore`, `FaceAssignmentModels`), `src/PhotoViewerV3.Services/Faces/Crops/`,
  `src/PhotoViewerV3.Wpf/Persons/`
- Tests: `tests/PhotoViewerV3.Tests/Faces/Assignment/`, `tests/PhotoViewerV3.Wpf.Tests/Persons/`; Rauchtest-Schritt
  „Personen“ (`Shell/Smoke/SmokeScenario.Persons.cs`)

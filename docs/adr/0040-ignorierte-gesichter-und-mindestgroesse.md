# ADR-0040: Ignorierte Gesichter ansehen und zurückholen, ganze Gruppe ignorieren, Mindestgröße als „automatisch ignoriert“

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Wunsch „Gesichter, die man nicht braucht, nicht berücksichtigen / ausblenden“),
  Claude (Umsetzung und Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ADR-0035 (Schema, `FaceState`, Index, `FaceRowMerger`), ADR-0036 (Lernen nur aus Bestätigtem),
  ADR-0037 (Zuordnungsdienst als einziger Schreiber der Zustände 1–3, Gruppen-Cache), ADR-0038 (Galerie zählt nur
  `State` 0/1/2), ADR-0030 (Warte-Logik), [Spike 09](../spikes/09-gesichtserkennung.md)

## Kontext

„Ignorieren“ (Zustand 3: keine Vorschläge, keine Gruppen, nie Lernbeispiel) gibt es seit 0.2.37 je Gesicht bzw. für die
Auswahl einer Gruppe. Es fehlten: ein Weg zurück (ignorierte Gesichter waren unsichtbar), das Ignorieren ganzer Gruppen
(Fremde im Hintergrund, inklusive der abgewählten Ausreißer) und eine Regel für sehr kleine Gesichter. Spike 09 misst
an 1 000 Fotos die Gesichtsgröße (Box-Seite im auf 1280 px verkleinerten Bild): p10 ≈ 25 px, p50 ≈ 62 px, p90 ≈ 203 px.
Kleine Gesichter haben schwache Fingerabdrücke (ArcFace vergrößert sie auf 112 px), erzeugen unsichere Vorschläge und
blähen „unbenannte Gesichter“ auf. Bedingung: automatisch ignorierte müssen von manuell ignorierten unterscheidbar sein,
damit eine geänderte oder ausgeschaltete Schwelle sie zurückholt, manuell ignorierte aber nicht. Parallel arbeitet die
Galerie (anderer Agent) mit Abfragen über `Faces.State IN (0,1)`/`(0,1,2)`.

## Entscheidung

1. **Zustand bleibt 3, die Markierung „automatisch (zu klein)“ steht in `Faces.SuggestionScore = -1`.** Bei Zustand 3
   ist `SuggestionScore` sonst immer NULL (Ignorieren, Bestätigen, Zurückholen setzen ihn auf NULL; Vorschläge schreiben
   nur in 0/1). `FaceRowMerger` trägt ihn bei erneuter Analyse mit, „Neu aufbauen“ parkt die ganze Zeile (ADR-0035).
   Kein neuer State-Wert, **keine Migration**. Konstante und Logik: `Services/Faces/FaceAutoIgnore`.
2. **Schwelle global in der Settings-Tabelle** (`faces.autoIgnoreMinSize`, Pixel, 0 = aus), **Standard 24 px**;
   Auswahl in den Einstellungen: Aus, 16, 20, 24, 32, 40, 48, 64.
3. **Anwenden:**
   - *Neue Gesichter:* `FaceIndexStore` liest die Schwelle je Schreib-Stapel in dessen Transaktion und fügt zu kleine
     Gesichter gleich als automatisch ignoriert ein (sie erscheinen nie als „unbenannt“). Fortgeschriebene Gesichter
     behalten ihren Zustand.
   - *Bestand:* Der Hintergrund-Worker des Zuordnungsdienstes vergleicht die Schwelle mit der zuletzt angewandten
     (`faces.autoIgnoreMinSize.applied`) — beim Start und nach jeder Änderung — und wendet sie bei Abweichung **einmal**
     auf alle Gesichter an (eine Transaktion, stille Warte-Logik, Single-Writer des Dienstes, vor Training und
     Vorschlägen): automatisch ignorierte, die die Regel nicht mehr trifft, → 0; offene (0/1) unter der Schwelle → 3 mit
     Markierung. **Bestätigte und manuell ignorierte fasst die Regel nie an.** Zurückgeholte → Neu-Lernen + Vorschläge;
     neu ignorierte → aus dem Gruppen-Cache.
4. **Zurückholen** (`IFaceIgnoreService.UnignoreFacesAsync`): Zustand 3 → 0 (manuell wie automatisch), Gruppen
   ungültig, entprellt neu lernen und Vorschläge schreiben. Ein zurückgeholtes zu kleines Gesicht bleibt offen, bis die
   Schwelle geändert wird (die Regel läuft nicht bei jedem Start, sondern nur bei einer Änderung).
5. **Ganze Gruppe ignorieren** (`IgnoreGroupAsync(groupId)`): alle Mitglieder der zwischengespeicherten Gruppe inkl.
   Ausreißer, soweit noch offen (0/1 — ein inzwischen bestätigtes bleibt bestätigt), manuell ignoriert; die Gruppe fällt
   aus dem Cache. Oberfläche: Knopf „Ganze Gruppe ignorieren…“ in der Gruppenansicht und Kontextmenü der Gruppenkachel,
   jeweils mit Rückfrage samt Anzahl und Ausreißern.
6. **Eigenes Interface `IFaceIgnoreService`**, implementiert vom `FaceRecognitionService` (partial, gleiche Instanz) —
   die vereinbarte `IFaceRecognitionService`-Schnittstelle und ihre Fakes (Galerie, Lightbox) bleiben unverändert.
7. **Personen-Seite, Ansicht „Ignorierte Gesichter“** (Knopf in der Übersicht mit Anzahl): seitenweise je 500 (neueste
   Fotos zuerst, nachgeladen, sobald der sichtbare Bereich das Ende erreicht), Kacheln wie die übrigen (TileListBox,
   VirtualizingTilePanel, Ausschnitte nur sichtbar), „zu klein“-Markierung mit Größe in Pixeln, Auswahl → Zurückholen,
   Doppelklick → Lightbox.

## Begründung

- **Markierung statt neuem Zustand:** Ein Zustand 4 müsste sich überall wie 3 verhalten — in den Galerie-Abfragen
  (`State IN (0,1)`, `(1,2)`, `= 2`), im Zuordnungsdienst, im `FaceRowMerger`, in der Lightbox. Mit Zustand 3 wirkt die
  Regel auf Lernen, Gruppen, Vorschläge, Filter und Zähler automatisch genau wie manuelles Ignorieren, ohne dass ein
  anderer Teil angepasst werden muss.
- **Markierung in `SuggestionScore` statt neuer Spalte:** Die Spalte ist bei Zustand 3 ungenutzt, `NULL`/`-1` reicht
  für „manuell/automatisch“. Eine Migration (`AddFaceIgnoreReason`) wäre sauberer lesbar, aber vermeidbar; sie hätte
  zudem mit parallelen Schema-Änderungen kollidiert (Modell-Snapshot). Die Bedeutung steht an einer Stelle
  (`FaceAutoIgnore.AutoMarker`, `IsAutomatic`).
- **Standard 24 px statt aus:** p10 = 25 px — die Regel trifft etwa das kleinste Zehntel, typischerweise Gesichter im
  Hintergrund. Unter 24 px trägt die Größe nach `FaceQuality.MinUsefulSize` nichts mehr zur Qualität bei; solche
  Gesichter liegen meist unter der Mindestqualität fürs Gruppieren (0,4), bekommen aber Vorschläge und zählen bei
  „unbenannte Gesichter“. Herberts Wunsch zielt genau auf diese Gesichter. Die Wirkung ist vollständig umkehrbar (Aus
  holt alles zurück) und sichtbar („zu klein“ in der Ansicht).
- **Einmal je Änderung statt bei jedem Start:** Sonst würde ein vom Benutzer zurückgeholtes kleines Gesicht beim
  nächsten Start wieder ignoriert. Die gespeicherte „angewandte“ Schwelle macht den Lauf idempotent und holt eine
  Änderung nach, die vor dem Beenden nicht mehr angewandt wurde.
- **Neue Gesichter im Index statt im Zuordnungsdienst:** in derselben Transaktion — kein Zeitfenster, in dem die Galerie
  ein zu kleines Gesicht als „unbenannt“ zählt; keine zweite Schreibrunde.

## Konsequenzen

### Positiv
- Ignorieren ist jetzt umkehrbar und sichtbar; Gruppen mit Fremden verschwinden mit einem Klick.
- Galerie, Lightbox, Lernen und Gruppen brauchen keine Änderung (Zustand 3).
- Keine Schema-Änderung, kein Eingriff in den Modell-Snapshot.

### Negativ
- `SuggestionScore` trägt bei Zustand 3 eine zweite Bedeutung — wer die Spalte liest, muss den Zustand beachten.
- Das Ändern der Schwelle ignoriert auch zurückgeholte kleine Gesichter erneut (die Regel kennt „vom Benutzer
  ausdrücklich gewollt“ nur für bestätigte und manuell ignorierte).
- Analysiert der Index eine Datei erneut, behält ein fortgeschriebenes Gesicht seinen Zustand — ein automatisch
  ignoriertes, das jetzt größer erkannt wird, kommt erst bei der nächsten Schwellenänderung zurück.
- Der erste Start nach dem Update ignoriert bei Herberts Katalog etwa ein Zehntel der Gesichter (umkehrbar).
- Die Anwendung auf den Bestand liest alle offenen Gesichter über `IX_Faces_State` (bei 500 000 Gesichtern einige
  Sekunden im Hintergrund, nicht gemessen).

### Neutral
- Die Schwelle gilt für alle Bibliotheken; der Lauf auf den Bestand ebenfalls.
- Die Schwelle misst `Faces.Size` (Box-Seite im analysierten Bild, längste Kante ≤ 1280 px); kleinere Fotos werden nicht
  hochgerechnet.

## Alternativen

- **Neuer Zustand 4 „automatisch ignoriert“:** verworfen — alle Abfragen über `Faces.State` (Galerie, Dienst, Index,
  Lightbox) müssten 4 wie 3 behandeln; hohes Risiko, eine Stelle zu vergessen.
- **Migration `AddFaceIgnoreReason` (eigene Spalte):** verworfen, weil vermeidbar (siehe Begründung).
- **Regel nur beim Index, nie auf den Bestand:** verworfen — Herberts vorhandener Katalog bliebe unberührt; Ändern der
  Schwelle hätte keine Wirkung.
- **Regel bei jedem Start anwenden:** verworfen — machte „Zurückholen“ eines kleinen Gesichts wirkungslos.
- **Standard aus:** verworfen — der Nutzen liegt gerade im Bestand; die Wirkung ist sichtbar und umkehrbar.

## Referenzen

- Code: `src/Lumora.Services/Faces/FaceAutoIgnore.cs`, `Faces/Assignment/IFaceIgnoreService.cs`,
  `Faces/Assignment/FaceRecognitionService.Ignore.cs`, `Faces/Indexing/FaceIndexStore.cs`,
  `src/Lumora.Wpf/Persons/ViewModels/PersonsViewModel.Ignored.cs`, `Wpf/Faces/FaceSettingsViewModel.MinSize.cs`
- Tests: `tests/Lumora.Tests/Faces/Assignment/FaceIgnoreServiceTests.cs`,
  `tests/Lumora.Wpf.Tests/Persons/PersonsIgnoredViewModelTests.cs`, `PersonsSuggestionsListUiTests.cs`;
  Rauchtest-Block „Ignorierte Gesichter“ im Schritt „Personen“ (`Shell/Smoke/SmokeScenario.PersonsIgnored.cs`)

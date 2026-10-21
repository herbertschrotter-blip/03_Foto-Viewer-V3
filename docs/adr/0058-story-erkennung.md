# ADR-0058: Storys — automatische Erkennung von Ereignissen, Reisen, Rückblicken, Personen und Orten

- **Status:** Accepted
- **Datum:** 2026-10-22
- **Entscheider:** Herbert Schrotter (Umfang, Mockup 09 abgenommen 2026-10-21), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** [ADR-0046](0046-aufnahmedatum.md) (Aufnahmedatum), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bibliotheks-Gruppen),
  [ADR-0049](0049-versteckte-bibliotheken.md) (versteckter Katalog), [ADR-0050](0050-orte.md) (Orte, GeoNames), [ADR-0053](0053-doppelte-erkennen.md)
  (Hintergrund-Index), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (Alben, `FileLinkUndo`), ADR-0057 (Foto-Güte, Highlights — parallel),
  Mockup `mockups/09-storys.html`, Messung [Spike 17](../spikes/17-story-erkennung.md)

## Kontext

Welle 6 „Storys“: Lumora soll aus Datum, Ort und Personen selbst Geschichten bilden — Ereignisse und Reisen,
Rückblicke („Heute vor 3 Jahren“), Personen-Storys („Anna · 2019“) und Orte-Storys („Grado · 2014–2025“). Herbert hat
entschieden: alle vier Arten automatisch, **kein erfundener Text** (Überschriften nur sachlich aus Datum, GeoNames-Orts-
und Personennamen; eigener Titel nur von Hand), Storys je Bibliotheks-Gruppe. Die Story-Seite (Agent U) baut gegen
einen WPF-freien Dienst; die Auswahl der Highlights liefert die Foto-Güte (Agent Q) über einen gemeinsamen Vertrag.
Bibliotheken mit 500 000 Dateien müssen flüssig bleiben, eine Neuberechnung darf nie verlieren, was der Benutzer an
einer Story eingestellt hat.

## Entscheidung

1. **Schema** (Migration `AddStories`, nur CREATE TABLE): `Stories` je Gruppe (`GroupId` ohne Fremdschlüssel wie Alben)
   mit `Kind` (0 Ereignis, 1 Reise, 2 Rückblick, 3 Person, 4 Ort, 5 eigene), **`Signature`** (je Gruppe eindeutig),
   Zeitraum (`StartAt`/`EndAt` Unix-Sekunden UTC, `FirstDay`/`LastDay` als Kalendertag in Ortszeit, `DatePrecision`),
   `PlaceId`, `PersonId` (→ Persons CASCADE), `PlaceIds` („id,id,…“), `VisitCount`, `IsAway` (Ausflug), Benutzer-Zustand
   (`CustomTitle`, `CoverFileId` → Files SET NULL + `CoverIsManual`, `IsPinned`/`PinnedAtUtc`, `IsHidden`,
   `IsSeen`/`SeenAtUtc`), Zähler `FileCount`/`VideoCount`. `StoryItems` (StoryId → Stories, FileId → Files, beide
   CASCADE; PK beide, `IX_StoryItems_File`) mit `Day` (Tagesabschnitt) und **`Origin`**: 0 automatisch, +1 vom Benutzer
   hinzugefügt, −1 vom Benutzer entfernt (Ausschluss-Zeile bleibt stehen, zählt nicht, wird nie wieder automatisch
   aufgenommen). Überschriften werden **nicht gespeichert**, sondern beim Lesen gebaut (`StoryTitleBuilder`).
2. **Erkennung** (`StoryDetector`, rein): ein Durchlauf über `COALESCE(CapturedAt, ModifiedAtUtc)` je Gruppe, nur
   Fotos/Videos, nicht „fehlt“, **Aufnahmedatum schon gelesen** (`CapturedAtSource IS NOT NULL`).
   - Ereignis: Trennung bei Pause > Einstellung (6 h) oder Ortswechsel > 30 km zum letzten verorteten Foto;
     Mindestgröße (12) gilt für Ereignisse, Reisen und Orte. Ungefähre Daten (nur Monat/Jahr) bilden eigene Gruppen
     („Juli 2025“, „1987“) statt in ein echtes Ereignis zu fallen.
   - Reise: aufeinanderfolgende Ereignisse **weg von zu Hause** (Schwerpunkt > 30 km), höchstens 48 h auseinander, über
     mindestens zwei Kalendertage; Ereignisse ohne Ort dazwischen gehören dazu, eines zu Hause beendet die Reise. Ein
     einzelner Tag weg bleibt ein Ereignis mit `IsAway` („Ausflug“). Zu Hause: Einstellung, sonst automatisch der
     häufigste `PlaceId` (Schwerpunkt seiner Fotos), ohne Orte die häufigste 0,1°-Zelle.
   - Person: je (nicht ausgeblendeter) Person Jahre mit ≥ 30 Fotos mit bestätigtem Gesicht; benachbarte schwächere Jahre
     werden zusammengezählt, bis sie die Schwelle erreichen („Max · 2021–2023“).
   - Ort: `PlaceId` mit Besuchen in ≥ 3 verschiedenen Jahren, nicht zu Hause bzw. näher als 30 km daran.
   - Signaturen: `E:<Start>`, `T:<erster Tag>-<letzter Tag>`, `A:M:yyyy-MM`/`A:Y:yyyy`, `P:<Person>:<Jahr>-<Jahr>`,
     `O:<Ort>`, `R:D:yyyy-MM-dd`/`R:M:yyyy-MM`, `C:<Guid>`.
3. **Wiederfinden** (`StoryReconciler`): erst gleiche Signatur, dann je Familie (Ereignis/Reise zusammen, Person je
   Person, Ort je Ort) die meisten gemeinsamen Dateien, ersatzweise zeitliche Überschneidung; Storys mit
   Benutzer-Zustand werden bevorzugt. Die Erkennung schreibt nur ihre Spalten (Signatur, Art, Orte, Zeitraum, Zähler)
   und nur automatische Einträge — Pins, Ausblenden, Titel, manuelles Titelbild, manuelle Zu-/Abgänge bleiben. Was ohne
   Gegenstück bleibt, wird gelöscht, außer es trägt Benutzer-Zustand (dann bleibt es, bis keine Datei mehr übrig ist).
   Eigene Storys und gespeicherte Rückblicke fasst die Erkennung nie an. Die allererste Erkennung einer Gruppe zählt
   nicht als „neu“ (es gab keinen letzten Besuch).
4. **Hintergrund** (`StoryIndexService`, Muster Doppelte): 20 s nach dem Start alle Gruppen ganz, dann nach Scan,
   entfernten Dateien, Verknüpfen/Lösen und geänderten Einstellungen ganz; nach neuen Dateien, Aufnahmedatum-, Orts-
   und Gesichts-Änderungen **inkrementell**: Fenster um die geänderten Zeitpunkte und die Storys, in denen die Dateien
   stehen, bis zur nächsten Lücke über max(Pause, 48 h) (über den Ausdrucks-Index), dazu nur die betroffenen Orte und
   Personen. Ruht bei Scan-Sperre, während die DB umzieht und **solange Aufnahmedaten bzw. Orte nachgetragen werden**
   (sonst entstünden Storys aus vorläufigen Daten, z. B. direkt nach „Neu aufbauen“). Eine Transaktion je Durchgang;
   danach automatische Titelbilder über `IHighlightSelector.PickCoverAsync`.
5. **Rückblicke dynamisch je Tag** (`GetMemoriesAsync`): „Heute vor N Jahren“ (≥ 6 Fotos an diesem Kalendertag) und
   „<Monat> in früheren Jahren“ (≥ Mindestgröße) werden beim Lesen berechnet und je Tag zwischengespeichert (jede
   Änderung verwirft den Zwischenstand). Gespeichert wird ein Rückblick erst, wenn der Benutzer ihn öffnet
   (`OpenMemoryAsync`, Signatur = Schlüssel) — ab dann trägt er Zustand wie jede Story (anheften, Titelbild …).
6. **Dienst** `IStoryService` (WPF-frei): Übersicht (Rückblicke, Angeheftet, Neu = ungesehen, Ereignisse & Reisen mit
   Jahren, Personen, Orte, eigene, Zahl der ausgeblendeten, Zähler je Art), Karte, Details (Kopfzeile „Reise · 8 Tage“,
   Orte, Personen mit Namen, Tagesabschnitte mit Datei-Ids in Aufnahme-Reihenfolge), Highlights, Vorschaubilder,
   Aktionen (anheften, ausblenden, gesehen, eigener Titel, Titelbild, Dateien hinzufügen/entfernen, „Story aus
   Auswahl“, „Als Album speichern“ über `IAlbumService`, löschen nur für eigene/Rückblicke, neu berechnen). Nachrichten
   `StoriesChangedMessage`, `StoryIndexStatusMessage`, `StorySettingsChangedMessage`. Einstellungen `stories.*`
   (Arten an/aus, Pause, Mindestgröße, zu Hause, Mindestfotos Person, Highlight-Anteil); abgeschaltete Arten werden
   nicht erkannt und nicht gezeigt, bleiben aber gespeichert.
7. **Vertrag Highlights** `Services/Quality/IHighlightSelector.cs` (wörtlich gleich bei Agent Q). Bis zur Foto-Güte
   registriert `AddStories()` per `TryAddSingleton` die Ersatz-Auswahl `BasicHighlightSelector` (Favorit, Bewertung,
   Bildgröße, gleichmäßig über die Zeit, keine Serien); ADR-0057 ersetzt sie per `Replace`.
8. **Bereich und Gruppen** (ADR-0048): Storys gehören der Gruppe und werden über alle Bibliotheken der Gruppe erkannt;
   einzeln geöffnet zeigt und zählt eine verknüpfte Bibliothek nur ihre Dateien (Storys ohne Datei im Bereich fehlen),
   „Alle verknüpften“ die ganze Gruppe. `Stories` steht in `LibraryGroupStore.TermTables` und `DeleteGroupIfEmpty`;
   Verknüpfen führt Storys gleicher Signatur zusammen (Dateien vereinigt, Zustand der Zielgruppe, fehlender kommt von der
   hinzukommenden; manuelle Zu-/Abgänge gewinnen über automatische), Personen-Storys einer zusammengeführten Person
   ziehen vorher samt Signatur auf die bleibende um (sonst nähme die Kaskade sie mit); Lösen kopiert die Storys mit
   Dateien der herausgelösten Bibliothek samt Zustand (Personen-Storys auf die kopierte Person).
9. **„Neu aufbauen“** (`UserDataCarryOver.Stories.cs`): **alle** `StoryItems` werden wie die Album-Einträge geparkt und
   über Ordnerpfad + Name auf die neuen FileIds gesetzt, Titelbilder samt Merkmal „manuell“ ebenso — die Stories-Zeilen
   selbst hängen an keiner Datei. **Datei-Operationen**: Einträge gehen per Kaskade; manuelle (+1/−1) sichert
   `FileLinkUndoStore` zur `FileOperation` (neue `FileLinkKind` 2 = hinzugefügt, 3 = Ausschluss), „Rückgängig“ bzw. die
   Rückkehr derselben Datei stellt sie wieder her.

## Begründung

- Signatur + Wiederfinden über gemeinsame Dateien hält den Benutzer-Zustand stabil, auch wenn neue Fotos am Rand den
  Anfang verschieben, eine kürzere Pause ein Ereignis teilt oder ein Ausflug durch einen weiteren Tag zur Reise wird.
- `Origin` in derselben Zeile statt eigener Ausnahme-Tabelle: ein Abgleich, ein Index, ein Zähler-Ausdruck
  (`Origin >= 0`); die Erkennung löscht nur `Origin = 0`.
- Überschriften beim Lesen: kein veralteter Text nach Umbenennen einer Person, eines Orts (GeoNames-Update) oder nach
  geänderten Daten; „kein erfundener Text“ ist so technisch erzwungen.
- Rückblicke dynamisch: jeder Tag hat andere — gespeichert müssten sie täglich neu geschrieben werden und würden „Neu“
  fluten; mit `OpenMemoryAsync` bleibt Anheften trotzdem möglich.
- Ruhe während des Nachtragens: nach „Neu aufbauen“ sind alle nicht manuellen Aufnahmedaten zunächst leer; Storys aus
  Änderungsdaten würden gelöscht und neu (mit neuen Ids, „neu“) angelegt.
- Inkrementelle Fenster bis zu einer Lücke über max(Pause, Reise-Lücke): kein Ereignis und keine Reise kann eine solche
  Lücke überspannen, also ist das Fenster in sich vollständig.

## Konsequenzen

### Positiv
- Messung (Spike 17, 500 000 Dateien, 2 109 Storys, 612 011 Einträge): volle Erkennung ohne Änderungen **2,0–2,3 s**,
  allererste Erkennung (alles neu schreiben) **4,6–5,0 s** plus 2,0–2,1 s Titelbilder (Ersatz-Auswahl), inkrementell nach
  20 neuen Fotos **≈ 0,1 s**, Übersicht laden **52–56 ms** (warm; kalt mit Rückblicken und Zuhause 240–270 ms).
- Story-Seite (Agent U) und Foto-Güte (Agent Q) bauen unabhängig gegen `IStoryService` bzw. `IHighlightSelector`.
- Versteckte Bibliotheken: alles im eigenen Katalog, nichts außerhalb der DB.

### Negativ
- Die allererste Erkennung einer sehr großen Bibliothek schreibt alle Einträge (≈ 600 000 Zeilen) — einmalig ≈ 5 s im
  Hintergrund, die Titelbilder folgen danach.
- Storys entstehen erst, wenn das Nachtragen der Aufnahmedaten und Orte fertig ist.
- `StoryItems` dupliziert Zugehörigkeit (Ereignis + Person + Ort) — ≈ 1,2 Einträge je Datei.

### Neutral
- Entfernte Dateien: Einträge gehen sofort (Kaskade), Zähler und Zeiträume zieht der nächste Durchgang nach (wenige
  Sekunden nach der Nachricht).
- Titelbilder der Ersatz-Auswahl: das Gegenstück liest alle Kandidaten über eine Verbindung (Katalog-Verbindungen sind
  nicht gepoolt); eine andere Auswahl wird parallel (höchstens 4) aufgerufen.
- Rauchtest: neuer Schritt „Storys“ vor „Versteckte Bibliothek“ (Mindestgröße für die kleine Testbibliothek 5).

## Alternativen

- **Storys nicht speichern, immer berechnen** → verworfen: Benutzer-Zustand (Pins, Titel, Ausschlüsse) bräuchte doch
  eine Tabelle, und die Übersicht wäre bei 500 000 Dateien nicht unter 100 ms.
- **Signatur allein als Schlüssel** → verworfen: neue Fotos am Rand würden aus einer angehefteten eine neue Story machen.
- **Rückblicke dauerhaft speichern** → verworfen (siehe Begründung).
- **Eigene Ausschluss-Tabelle** → verworfen zugunsten `Origin` (eine Zeile je Datei und Story).
- **Nur manuelle Einträge über „Neu aufbauen“ tragen** → verworfen: die Storys wären bis zum nächsten Durchgang leer und
  ohne Aufnahmedaten nicht wiederzufinden.
- **Mehrfach-INSERT/WITHOUT ROWID für `StoryItems`** gemessen: Mehrfach-INSERT (200 Zeilen) bleibt, WITHOUT ROWID brachte
  keinen messbaren Gewinn und bliebe EF-fremd → verworfen.

## Referenzen

- Code: `src/Lumora.Services/Stories/**`, `src/Lumora.Services/Quality/IHighlightSelector.cs`,
  `src/Lumora.Domain/Entities/Story*.cs`, `src/Lumora.Data/EntitiesMapping/StoryConfiguration.cs`,
  `src/Lumora.Services/Scanning/UserDataCarryOver.Stories.cs`, Rauchtest `src/Lumora.Wpf/Shell/Smoke/SmokeScenario.Stories.cs`
- Tests: `tests/Lumora.Tests/Stories/**` (Messung mit `LUMORA_STORY_PERF=1`)
- Messung: [Spike 17](../spikes/17-story-erkennung.md)

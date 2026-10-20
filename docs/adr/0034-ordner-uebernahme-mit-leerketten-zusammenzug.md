# ADR-0034: Ordner-Übernahme mit Leerketten-Zusammenzug

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** ADR-0012 (Operation-Logging), ADR-0018 (Datei-Operationen nur über den Service), ADR-0020 (mehrere
  Wurzeln), ADR-0024 (Sortierer-Ziele), ADR-0025 (Scan-Sperre), ADR-0030 (Schreiber warten auf gesperrten Katalog)

## Kontext

Herberts Arbeitsweise: ein unsortierter Ordner (oft tief verschachtelt) wird als Eingang aufgenommen, angesehen, bewertet
und kategorisiert — einzelne Dateien oder ganze Ordner — und danach als Ganzes in die Bibliothek (Archiv-Wurzel)
verschoben; der Quellordner ist danach weg. Bisher gab es nur „Ins Archiv…“ (ausgewählte Dateien flach in EINEN
Zielordner) und das aus V2 portierte „Flatten & Move“, das jeden Medienordner mit zusammengesetztem Namen auf eine Ebene
zieht. Beides zerstört die Unterordner-Struktur. Seine Vorgabe: „die Unterordner-Struktur soll nie verloren gehen“; das
Einzige, was passieren darf, ist das Zusammenziehen leerer Ketten (`Fotos/leer/leer/leer/inhalt` → `Fotos/inhalt`).
Außerdem waren die Begriffe „Archiv“ (Archiv-Wurzel) und „Archive“ (ZIP/RAR/7z) in der Oberfläche verwechselbar.

## Entscheidung

1. **Neue Aktion „Ordner in die Bibliothek übernehmen…“** (Ordnerbaum-Kontextmenü für Ordner unter einem Eingang inkl.
   Eingangs-Wurzel, Bibliotheksseite je Eingang). Der Ordner landet samt Struktur als Unterordner des gewählten
   Zielordners im Bibliotheksordner. Plan-Logik WPF-frei und rein (`FolderAdoptionPlanner`):
   - **Zusammenziehen** (Option, Standard an): ein Ordner ohne Dateien mit genau einem nicht leeren Unterordner ist ein
     Durchgangsordner und entfällt; der nächste rückt nach, bis zum ersten Ordner mit Dateien oder mehr als einem
     Unterordner — dessen Name bleibt. Gilt auch für den übernommenen Ordner selbst (`Import/leer/inhalt` → `Ziel/inhalt`).
     Leere Teilbäume werden nie angelegt und zählen nicht als Unterordner.
   - Würden **Geschwister nach dem Zusammenziehen gleich heißen** (ohne Groß-/Kleinschreibung), werden sie an dieser
     Stelle nicht zusammengezogen (`A/Bilder` und `B/bilder` bleiben unterscheidbar). Echte Namensgleichheit und
     vorhandene Zielordner werden **zusammengeführt** (vorhandene Schreibweise gewinnt).
   - **Dateinamenskonflikte** → `name_1.ext`, `name_2.ext` … (wie Flatten); eine Datei mit dem Namen eines Zielordners →
     Ordner `name_1`.
   - Übernommen werden **alle** Dateien (auch .xmp, .txt), nicht aber Systemordner (`.thumbs`, `.cache`, `.temp`,
     `.converted`, `.thumbnails`, `@eaDir`) und Systemdateien (`Thumbs.db`, `desktop.ini`, `.DS_Store`, System-Attribut).
2. **Ausführung in EINEM Batch** des `IFileOperationService` (`AdoptFolderAsync`, neuer `OperationType.AdoptFolder = 6`,
   keine Schema-Änderung): Protokoll Ordner-Operation → MkDir je neuem Ordner → Move je Datei → optional Recycle des
   Quellordners, alles vor dem ersten Dateisystem-Schritt persistiert (ADR-0012); Scan-Sperre der Bibliothek (ADR-0025);
   FileIds bleiben (Bewertung, Tags, Kategorien, Vorschaubilder wandern mit), umbenannte Dateien mit FTS-'delete'/Insert,
   nicht katalogisierte Medien werden aufgenommen, neue Ordner bekommen `Folders.RootId` der Archiv-Wurzel.
3. **Quellordner** kommt nur in den Papierkorb, wenn danach keine Datei mehr darin liegt (Systemordner/-dateien zählen
   nicht und gehen mit); sonst bleibt er und der Bericht nennt die Reste. Noch katalogisierte Dateien darunter (der Scanner
   nimmt V2-`.thumbs` als Bilder auf) werden dabei wie beim Papierkorb ausgetragen, leere Ordnerzeilen entfernt.
   Bleibt der Eingang eingetragen (ganze Eingangs-Wurzel, „austragen“ aus), geht nur der Inhalt in den Papierkorb — der
   Eingangsordner bleibt leer bestehen.
4. **Eingang austragen** (Option, nur bei ganzer Eingangs-Wurzel) über `ILibraryService.RemoveRootAsync`, und nur, wenn
   nichts zurückblieb und keine Katalogdatei mehr unter ihm liegt.
5. **Rückgängig** über das vorhandene „Rückgängig“: Dateien zurück an die alten Pfade (Ordner entstehen neu, der
   Papierkorb wird nicht angefasst), leer gewordene Zielordner samt Ordnerzeilen entfernen; liegt der Quellordner unter
   keiner Wurzel mehr, wird er vorher wieder als Eingang eingetragen (Name = Ordnername).
6. **Ordner bewerten/kategorisieren**: Kontextmenü „Bewertung / Favorit / Kategorie / Tags für alle Dateien“, rekursiv
   über den Ordnerpfad, mit Bestätigung und Anzahl; geschrieben über die vorhandenen Dienste in Abschnitten zu 2 000
   Dateien (`IFolderMetadataService`), also mit deren Warte-Logik (ADR-0030).
7. **Begriffe in der Oberfläche** (keine Code-Bezeichner): Archiv-Wurzel → „Bibliotheksordner“, „Ins Archiv…“ → „In die
   Bibliothek…“ (Strg+M bleibt), Filter „Nicht archiviert“/„Archiviert“ → „Noch im Eingang“/„In der Bibliothek“,
   „Eingang“ = unsortierter Quellordner. Archiv-DATEIEN (ZIP/RAR/7z, Filter „Archive“, „Entpacken…“) bleiben „Archive“.

## Begründung

- Die Struktur ist Herberts Ordnungssystem; Flatten war die falsche Abstraktion. Das Zusammenziehen beseitigt genau den
  Ballast typischer Kamera-/Cloud-Exporte (`DCIM/100MEDIA/…`), ohne Information zu verlieren: ein Durchgangsordner trägt
  außer seinem Namen nichts.
- Ein Batch statt vieler „Move“-Batches: ein Rückgängig nimmt die ganze Übernahme zurück, das Protokoll zeigt eine Zeile.
  Ein eigener OperationType ist nötig, weil das Undo mehr kann als einzelne Moves (Eingang wieder eintragen, leere
  Zielordner entfernen, Papierkorb-Schritt vermerken).
- Plan rein und testbar: alle Regeln (Kette, Verzweigung, Konflikte, Groß-/Kleinschreibung) sind Unit-Tests ohne
  Dateisystem; die Vorschau im Dialog zeigt genau den Plan, der ausgeführt wird.
- Papierkorb nur ohne Rest und nie Hard-Delete (Repo-Regel); Systemordner wie V2-`.thumbs` würden sonst jede Übernahme
  blockieren.

## Konsequenzen

### Positiv
- Ablauf „Eingang → ansehen/bewerten → übernehmen“ in einem Schritt, Struktur bleibt, Benutzerdaten wandern mit.
- Klare Begriffe; keine Verwechslung mehr zwischen Bibliotheksordner und ZIP-Archiven.

### Negativ
- Große Übernahmen sind so schnell wie „Verschieben“ (eine kurze Transaktion je Datei); der Schreib-Lock des
  `FileOperationService` ist so lange belegt.
- Rückgängig holt Systemordner (`.thumbs` …) und ausgetragene Katalogzeilen darunter nicht zurück (liegen im Papierkorb);
  ein wieder eingetragener Eingang heißt wie sein Ordner, nicht wie vorher.

### Neutral
- `IRecycleBin.MoveDirectoryToRecycleBin` (Standard-Implementierung wirft; `WindowsRecycleBin` mit denselben
  Schutzregeln wie für Dateien).
- „Zuletzt benutzt“ teilen sich „In die Bibliothek…“ und die Übernahme (`archive.lastTarget.{LibraryId}`).
- Flatten & Move bleibt unverändert (V2-Port), wird für die Übernahme nicht benutzt.

## Alternativen

- **Flatten & Move mit Option „Struktur behalten“** → verworfen: andere Semantik (zusammengesetzte Namen, Wurzel-Dateien
  nach `_root`), andere Undo-Logik (Sortierer-Lauf); die Mischung wäre schwer erklärbar.
- **`Directory.Move` des ganzen Ordners** → verworfen: kein Zusammenziehen, keine Zusammenführung mit vorhandenen
  Ordnern, über Laufwerksgrenzen nicht möglich, Katalog müsste trotzdem je Datei umgebucht werden.
- **Viele Move-Batches (je Zielordner)** → verworfen: mehrere Rückgängig-Schritte für eine Benutzeraktion, kein Ort für
  „Eingang wieder eintragen“.
- **Kollidierende Geschwister nach dem Zusammenziehen immer zusammenführen** → verworfen: zwei verschiedene Quellen
  liefen in einen Ordner, die Struktur ginge verloren.

## Referenzen

- `src/PhotoViewerV3.Services/Operations/FolderAdoptionPlanner.cs`, `FolderAdoptionSourceReader.cs`,
  `FolderAdoptionService.cs`, `FileOperationService.Adopt.cs`; `src/PhotoViewerV3.Services/Catalog/FolderMetadataService.cs`
- `src/PhotoViewerV3.Wpf/Folders/Adoption/` (Dialog), `Folders/FolderActions.cs`
- Tests: `tests/PhotoViewerV3.Tests/Operations/FolderAdoptionPlannerTests.cs`, `FolderAdoptionExecutionTests.cs`,
  `tests/PhotoViewerV3.Wpf.Tests/ArchiveTarget/FolderAdoptionDialogTests.cs`; Rauchtest-Schritt „Ordner übernehmen“

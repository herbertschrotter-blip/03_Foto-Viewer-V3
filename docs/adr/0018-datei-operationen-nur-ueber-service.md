# ADR-0018: Datei-Operationen ausschließlich über IFileOperationService

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0012 (Operation-Logging), ADR-0009 (Cache-Strategie), ADR-0017 (Nachrichten)

## Kontext

In 0.2.0 ändern vier Stellen Dateien auf der Platte: die Galerie (Verschieben nach…, Papierkorb, Rückgängig), die
Lightbox (Papierkorb), der File Sorter (Verschieben in Gruppenordner, Umbenennen bei Namenskonflikten) und Flatten &
Move (Verschieben, Umbenennen). Jede Operation muss Dateisystem und Katalog konsistent halten (Files.FolderId,
Folders-Kette, Thumbnails-Zeile und Cache-Datei, FTS-Eintrag), nach ADR-0012 protokolliert sein und rückgängig gemacht
werden können. Vier eigene Implementierungen hätten vier Arten von Inkonsistenz erzeugt — und ein stilles Hard-Delete
(z. B. weil ein Netzlaufwerk keinen Papierkorb hat) wäre für den Benutzer die schlimmste Überraschung.

## Entscheidung

Der einzige Weg, Dateien zu verschieben, umzubenennen oder zu löschen, ist `IFileOperationService` (`RecycleAsync`,
`MoveAsync`, `RenameAsync`, `UndoLastAsync`, `UndoBatchAsync`, `GetRecentBatchesAsync`):

- Jeder Aufruf ist ein `OperationBatch` mit `FileOperation`-Zeilen, die **vor dem ersten Dateisystem-Schritt**
  persistiert sind (ADR-0012); Single-Writer; die Katalog-Buchung nach dem Dateisystem-Schritt ist nicht mehr
  abbrechbar, damit kein Move ohne Buchung bleibt. Fehler einzelner Dateien brechen den Batch nicht ab.
- Löschen heißt Papierkorb über `IRecycleBin` (`WindowsRecycleBin`, `SHFileOperation` ohne Shell-UI). Wo kein
  Papierkorb möglich ist (Netz-/UNC-/Wechseldatenträger), wird die Operation **abgelehnt** statt still hart gelöscht.
- Verschieben innerhalb der Bibliothek legt die Folders-Kette an und aktualisiert `Files.FolderId`; Verschieben aus
  der Bibliothek heraus entfernt den Katalogeintrag samt Thumbnail-Datei und FTS-Eintrag. Ein vorhandener Zielname
  lässt das Item fehlschlagen (kein Überschreiben).
- Nach jeder Operation sendet der Service eine `FilesChangedMessage` (`Moved`/`Removed`/`Added`), auf die Galerie,
  Lightbox, Ordnerbaum und Statusleiste reagieren (ADR-0017).
- `UndoLastAsync` nimmt den jüngsten Move/Rename-Batch rückwärts; der Papierkorb ist nicht per Undo rückholbar.
  File Sorter und Flatten merken sich zusätzlich die Batch-Ids ihres Laufs in der Settings-Tabelle (`sorter.lastRun`,
  `sorter.lastFlattenRun`) und bieten „Rückgängig (letzter Lauf)“ über alle Batches des Laufs.

## Begründung

- Eine Implementierung, die den Katalog in jeder Variante korrekt nachzieht, ist leichter zu härten und zu testen
  (`FileOperationServiceTests`, `FileOperationHardeningTests`, `WindowsRecycleBinTests`) als vier.
- Das Protokoll ist die Grundlage für Undo heute und für den Recovery-Dialog (ADR-0012) morgen — nur wenn alle
  Schreiber hindurchgehen, ist es vollständig.
- Der Papierkorb-Schutz muss zentral sein: `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` (PLAN Kap. 2) fällt
  auf Netzlaufwerken auf ein hartes Löschen zurück und zeigt bei gesperrten Dateien Dialoge — beides unbrauchbar für
  einen unbeaufsichtigten Batch.

## Konsequenzen

### Positiv
- Galerie, Lightbox, Sortierer und Flatten verhalten sich beim Verschieben/Löschen identisch; Undo funktioniert
  überall gleich.
- Der Katalog bleibt nach jeder Operation konsistent (inkl. Thumbnails und FTS); verwaiste Ordnerzeilen aus
  Altkatalogen werden beim Anlegen der Kette repariert.
- Kein stilles Hard-Delete; der Benutzer sieht abgelehnte Löschungen als Fehler je Datei.

### Negativ
- Jede neue Dateiänderung (später Plan-Apply, Kopieren) muss den Service erweitern, statt lokal `File.Move` zu rufen.
- Pro Operation zwei DB-Roundtrips vor der Ausführung (Batch + Items), spürbar bei Tausenden Dateien — akzeptiert,
  weil die Dateisystem-Zeit dominiert.
- Papierkorb-Löschungen sind nicht per Undo rückholbar (nur über den Windows-Papierkorb selbst).

### Neutral
- Der Scanner ist kein Schreiber im Sinne dieses ADRs: er ändert keine Dateien, nur den Katalog (ADR-0019).
- Die Statusleiste zeigt unfertige/fehlgeschlagene Batches an; der Recovery-Dialog aus ADR-0012 ist weiterhin offen.

## Alternativen

- **`File.Move`/`File.Delete` direkt in ViewModels und Sorter-Services** → verworfen weil: Katalog, Thumbnails und
  FTS laufen auseinander, kein Protokoll, kein Undo.
- **`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` mit `RecycleOption`** → verworfen weil: Shell-Dialoge pro
  gesperrter Datei, stilles Hard-Delete, wo kein Papierkorb existiert.
- **Transactional NTFS (TxF)** → verworfen weil: von Microsoft abgekündigt, nicht auf Netzlaufwerken.

## Referenzen

- [src/Lumora.Services/Operations/IFileOperationService.cs](../../src/Lumora.Services/Operations/IFileOperationService.cs)
- [src/Lumora.Services/Operations/FileOperationService.cs](../../src/Lumora.Services/Operations/FileOperationService.cs)
- [src/Lumora.Services/Operations/WindowsRecycleBin.cs](../../src/Lumora.Services/Operations/WindowsRecycleBin.cs)
- [src/Lumora.Services/Sorter/SorterMoveExecutor.cs](../../src/Lumora.Services/Sorter/SorterMoveExecutor.cs) (Sorter/Flatten nutzen den Service)
- [ADR-0012](0012-operation-logging.md), [CLAUDE.md](../../CLAUDE.md) (Regel „Datei-Operationen immer mit OperationBatch“)

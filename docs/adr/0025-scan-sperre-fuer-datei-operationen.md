# ADR-0025: Scan und Datei-Batch einer Bibliothek schließen sich aus; eigene Schreibvorgänge zählen nicht für die Ordnerüberwachung

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** ADR-0012 (Operation-Logging), ADR-0018 (Datei-Operationen nur über den Service), ADR-0019 (Incremental-Scan), ADR-0020 (mehrere Wurzeln)

## Kontext

Seit 0.2.14 serialisiert `IScanGate` (ein `SemaphoreSlim` je Bibliothek) alle Scans; der `FileOperationService` war
davon unberührt. Ein Incremental-Scan, der den Baum liest, während ein Verschiebe-Batch läuft, kann eine Datei am alten
Ort als „fehlt“ und am neuen als „neu“ sehen (neue FileId, Tags und Bewertung hängen an der alten). Außerdem löste jede
eigene Operation — Verschieben, Papierkorb, „Ins Archiv…“, Ordner umbenennen, Rückgängig — über den FileSystemWatcher
einen automatischen Scan aus, der nichts fand: der Katalog war vom Batch längst gebucht (CHANGELOG 0.2.14, „Geprüft,
nicht umgesetzt“). Ein pauschales Ignorieren aller Ereignisse während eines Batches verlöre echte externe Änderungen.

## Entscheidung

1. Jeder Batch des `FileOperationService` (Move, Recycle, Rename, Ordner anlegen/umbenennen, Undo) belegt über
   `IScanGate.AcquireForFileOperationAsync` die beteiligten Bibliotheken — vor dem Lesen des Katalogs, bis vor der
   Rückkehr. Ein Scan wartet auf das Batch-Ende, ein Batch auf das Scan-Ende.
2. Vor jedem Dateisystem-Schritt trägt der Batch Quelle, Ziel und neu angelegte Ordner in die kurzlebige
   `IOwnChangeRegistry` ein — nur Pfade, deren Katalog er mitbucht. Die Ordnerüberwachung verwirft Ereignisse, deren
   Pfade dort stehen (bei „Renamed“ beide Pfade), bis 10 s nach Batch-Ende. Scheitert Dateisystem- oder Katalog-Schritt,
   nimmt der Batch den Pfad zurück; die Überwachung zählt ihn dann wie ein Ereignis.

## Begründung

- Dieselbe Sperre wie für Scans: kein zweiter Mechanismus, keine neue Reihenfolgeregel. Die Datei-Variante löst
  `Acquired` nicht aus, denn ein Batch liest das Dateisystem nicht neu ein und deckt keine gesammelten Ereignisse ab.
- Pfadgenau statt zeitlich pauschal: eine externe Änderung an einem anderen Pfad zählt weiter, auch mitten im Batch.
- Nur gebuchte Pfade: das Ziel eines Verschiebens aus der Bibliothek heraus bleibt fremd — liegt es in einer anderen
  überwachten Bibliothek, liest diese es selbst ein. Ein gescheiterter Katalog-Schritt ruft den Scan, der ihn heilt
  (ADR-0012: „der nächste Scan heilt den Katalog“).

## Konsequenzen

### Positiv
- Kein Scan sieht eine halb verschobene Bibliothek; keine „fehlt + neu“-Paare durch eigene Batches.
- Nach Verschieben, Papierkorb, „Ins Archiv…“, Sortierer-Lauf und Rückgängig startet kein leerer automatischer Scan.

### Negativ
- Ein Batch wartet auf einen laufenden Scan (bei 500k Dateien ggf. Minuten); die Galerie zeigt dabei nur den
  laufenden Scan in der Statusleiste. Abbrechbar über das CancellationToken des Aufrufers.
- Eine fremde Änderung genau an einem eigenen Pfad innerhalb der Nachlaufzeit geht in der Zählung unter (das nächste
  Ereignis oder „Aktualisieren“ holt sie nach).

### Neutral
- Die Sperre ist nicht wiedereintrittsfähig: wer sie hält, darf weder scannen noch einen Datei-Batch derselben
  Bibliothek starten. Entpacken (Papierkorb, dann Scan), Sortierer (Batches, dann Ordner aufräumen) und
  „Ins Archiv…“ rufen den Scan erst nach dem Batch auf; der `FileOperationService` gibt die Sperre vor der Rückkehr frei.
- Undo belegt die Bibliotheken, unter deren Wurzeln die protokollierten Pfade liegen (die Katalogzeilen können seit dem
  Batch die Bibliothek gewechselt haben). Mehrere Bibliotheken werden aufsteigend nach Id belegt.
- Der Sortierer trägt gelöschte leere Quellordner ebenfalls in die `IOwnChangeRegistry` ein.

## Alternativen

- **Ereignisse während eines Batches pauschal ignorieren** → verworfen: verliert echte externe Änderungen und
  Teilfehler (Kopie am Ziel, Quelle nicht gelöscht).
- **Überwachung während eines Batches anhalten und danach immer scannen** → verworfen: genau der leere Scan, der
  vermieden werden soll.
- **Eigene Sperre im FileOperationService, Scan prüft sie** → verworfen: zwei Sperren mit Reihenfolgeregel statt einer.

## Referenzen

- `src/PhotoViewerV3.Services/Scanning/ScanGate.cs`, `src/PhotoViewerV3.Services/Watch/OwnChangeRegistry.cs`,
  `src/PhotoViewerV3.Services/Operations/FileOperationService.cs`, `src/PhotoViewerV3.Services/Watch/LibraryWatchService.cs`
- Tests: `tests/PhotoViewerV3.Tests/Operations/FileOperationScanGateTests.cs`,
  `tests/PhotoViewerV3.Tests/Watch/LibraryWatchServiceTests.OwnChanges.cs`, `tests/PhotoViewerV3.Tests/Watch/OwnChangeRegistryTests.cs`,
  `tests/PhotoViewerV3.Wpf.Tests/ArchiveTarget/ArchiveMoveServiceGateTests.cs`
- CHANGELOG 0.2.14 („Offen“, „Geprüft, nicht umgesetzt“) und 0.2.17

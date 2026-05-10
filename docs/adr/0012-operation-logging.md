# ADR-0012: Operation-Logging (Batches + Operations)

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5
- **Bezogen auf:** Plan-Ordner (V0.3), Move/Rename (V0.2)

## Kontext

Destruktive Dateioperationen (Verschieben, Löschen, Umbenennen, später Plan-Apply) müssen **Crash-tolerant** und **Block-Undo-fähig** sein. Zwischen Dateisystem (NTFS) und SQLite gibt es **keine echte gemeinsame Transaktion** — wenn App nach Move stirbt aber vor DB-Update, ist Inkonsistenz da. User muss Vertrauen haben, dass nichts magisch verschwindet.

## Entscheidung

**Two-Phase-Commit-light / Saga-Pattern** via zwei Tabellen:

- **`OperationBatches`** — Klammer um zusammengehörige Operationen (z. B. ein Plan-Apply mit 234 Moves = 1 Batch)
- **`FileOperations`** — Einzeloperationen mit Source/Dest/Status

**Ab V0.1 für Delete-to-Recycle. Ab V0.2 auch für Move/Rename. V0.3 für Plan-Apply.**

## Begründung

- **Crash-Recovery** beim App-Start: scan nach Batches mit `Status IN ('applying', 'undoing', 'failed')` → Recovery-Dialog ("Fortsetzen / Reparieren").
- **Block-Undo**: ein ganzer Plan-Apply als Einheit rückgängig machbar, nicht einzelne Move-Operationen.
- **Audit-Trail**: User kann sehen, was passiert ist.
- **`History.PlanId` allein wäre zu dünn** (GPT-5-Korrektur): Plan-Apply ist ein zusammengesetztes Ereignis.

## Schema

```sql
OperationBatches (
  Id INTEGER PRIMARY KEY,
  OperationType TEXT NOT NULL,   -- plan_apply | move | delete | rename | flatten
  Status TEXT NOT NULL,          -- pending | applying | applied | undoing | undone | failed
  CreatedAtUtc TEXT NOT NULL,
  CompletedAtUtc TEXT NULL,
  PlanId INTEGER NULL,
  Description TEXT NULL
);

FileOperations (
  Id INTEGER PRIMARY KEY,
  BatchId INTEGER NOT NULL,
  FileId INTEGER NULL,
  Action TEXT NOT NULL,          -- move | copy | recycle | rename | mkdir
  SourcePath TEXT NULL,
  DestPath TEXT NULL,
  Status TEXT NOT NULL,          -- pending | done | failed | undone
  Error TEXT NULL,
  ExecutedAtUtc TEXT NULL,
  UndoneAtUtc TEXT NULL,
  FOREIGN KEY (BatchId) REFERENCES OperationBatches(Id) ON DELETE CASCADE
);
```

Index: `CREATE INDEX IX_FileOperations_Batch_Status ON FileOperations(BatchId, Status);`

## Ablauf (Plan-Apply als Beispiel)

```
1. OperationBatch erstellen mit Status='pending'
2. Operationsliste (alle Moves) berechnen, Konflikte auflösen, als 'pending' speichern
3. Batch → 'applying'
4. Für jede Operation: Move ausführen → 'done' markieren → Files.FolderId/Path updaten
5. Batch → 'applied'
```

**Wichtig:** Komplette Operationsliste muss **vor dem ersten Move** persistiert sein. Sonst Recovery unmöglich.

## Recovery-UI

Bei App-Start mit unfertigen Batches:

```
Unvollständiger Dateivorgang gefunden
Plan „Urlaub Italien 2026" wurde nicht vollständig abgeschlossen.
Fortschritt: 187 von 234 Dateioperationen abgeschlossen, 47 offen, 0 fehlgeschlagen.
Empfehlung: Fortsetzen
[Fortsetzen] [Reparatur prüfen] [Nur Protokoll anzeigen]
```

7-Felder-Matrix für Reparatur (Source existiert ja/nein × Dest existiert ja/nein × Status pending/done) — siehe GPT-5-Antwort Runde 2.

## Konsequenzen

### Positiv
- App-Crash mitten in Plan-Apply ist recoverable
- Block-Undo möglich
- Audit-Trail für User-Vertrauen
- Saubere Grundlage für Move/Rename in V0.2 und Plan-Apply in V0.3

### Negativ
- Schema-Komplexität
- Bei jedem Batch zwei DB-Round-Trips (Insert Batch, Insert Operations) vor Ausführung
- Recovery-UI ist eigene Arbeit (V0.2 minimal, V0.3 voll)

### Neutral
- V0.1 nutzt es **nur für Delete-to-Recycle** (jede Datei-Löschung = ein Batch mit einer Recycle-Operation)
- Move/Rename in V0.2 nutzt dieselbe Infrastruktur
- Plan-Apply in V0.3 ist der Hauptkunde

## Alternativen

- **`History.PlanId` allein** → verworfen weil: zu dünn, kein Status pro Einzeloperation, kein Recovery.
- **Echte DB-Transaktion über alle Moves** → unmöglich, weil NTFS keine Transaktionen unterstützt.
- **Nur Logging in Text-Datei** → verworfen weil: kein Query, kein Recovery.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "OperationBatches"
- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "Crash-Recovery-UX" mit 7-Felder-Matrix

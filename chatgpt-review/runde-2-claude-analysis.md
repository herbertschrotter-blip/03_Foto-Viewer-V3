# Runde 2 — Claudes Stufe-A-Einschätzung

> Datum: 2026-05-10

## Kurzfassung

GPT-5 hat noch besser geliefert als Runde 1. Konkreter produktionsnaher Code, präzise Memory-Korrekturen, ehrlicher Widerspruch zu Claudes Schwächen. **Übernehmen ohne Vorbehalt.**

## Wo GPT-5 Claude (zu Recht) widerlegt

| Punkt | Claudes Vorschlag | GPT-5 | Bewertung |
|---|---|---|---|
| Persons/Faces in V0.1-Schema | "schon anlegen, leer" | "verfrühte Schema-Bindung" | ✅ GPT-5 hat recht — Embedding-Dimension offen, Modell unstabil. Migration ist eh nötig wenn Tags/Plans real sind. |
| Memory Dedupe-Dictionary | "25 MB für 500k" | "50-80 MB realistisch" | ✅ Korrigiert. Konsequenz: nicht alle 500k auf einmal enqueuen. |
| Animated Hover Default | implizit "an" | "default AUS" | ✅ Bei 500k absolut richtig. |
| DB→FS-Check bei jedem Start | "Konsistenz prüfen" | "Startzeit-Killer — nur Batch-Recovery" | ✅ Logisch. |

## Wertvollste neue Beiträge

1. **Vollständiger Multi-Channel-Service** (~250 Zeilen) — production-ready, nicht Pseudo
2. **Robuster WIC-Encoder** mit `FileShare.ReadWrite | FileShare.Delete` (Cloud-Sync-tolerant)
3. **Performance-Reality-Check Initial-Scan**: Naiv EF = 16-83 Min, Raw SQLite Prepared INSERT = <5 Min
4. **WorkBudgetService** mit FPS-Messung via `CompositionTarget.Rendering`
5. **WPF VirtualPanel-Realität**: 500k in ObservableCollection ist falsch → Paging Pflicht
6. **OperationBatches-Recovery** mit 7-Felder-Tabelle für alle Source/Dest-Kombinationen
7. **10 ADRs** als Architecture Decision Records

## User-Entscheidungen aus Stufe A

| Frage | Antwort | Konsequenz |
|---|---|---|
| V0.1 Scope | **Produktiv nutzbar** | FTS5 + Filter + Favorit Pflicht. V0.1 ersetzt PowerShell-Viewer. |
| Storage | **Gemischt — Bibliothek wandert** | App braucht Drive-Type-Detection (Win32 `GetDriveType`) und Storage-Profile-Switching. |
| MP4 in V0.1 | **Mit LibVLCSharp** | Spike vorziehen, V0.1 wird ambitionierter, aber komplette Lightbox. |
| Status-UI | **Alle 4 Indikatoren** | Scan-Fortschritt + Thumb-Queue-Status + Background-Aktivität + Recovery-Hinweise. Transparenz-first. |

## Konsequenzen für die Implementation

- **V0.1 wird größer als GPT-5s schlanker MVP** — realistischer Aufwand 8-10 Wochen statt 4
- **Storage-Detection** als eigene Komponente nötig (`StorageProfileService`)
- **LibVLCSharp-Spike vorziehen** — Risiko V0.1 nicht zu blockieren
- **Status-Service** mit Subscriber-Pattern für UI-Updates
- **`Thumbnails.Status` + `AttemptCount`** in V0.1-Schema
- **Persons/Faces NICHT in V0.1**

## Status

- ✅ Runde-2-Prompt erstellt → an GPT-5
- ✅ GPT-5-Antwort archiviert
- ✅ Stufe-A-Einschätzung von Claude
- ✅ User-Entscheidungen dokumentiert
- ❓ Nächster Schritt: Runde 3 (Aufwands-Reality-Check + finale Validierung) oder direkt finaler Plan?

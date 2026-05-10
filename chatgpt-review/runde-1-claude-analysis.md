# Runde 1 — Claudes Stufe-A-Einschätzung

> Datum: 2026-05-10

## Kurzfassung

GPT-5 hat sehr stark abgeliefert. Konkreter Code, präzise SQL-Schemas, kritische UX-Punkte. Ich gehe mit fast allem mit. Mehrere echte Verbesserungen gegenüber dem ursprünglichen Plan.

## Punkte, denen ich zustimme

| # | Punkt | Bewertung |
|---|---|---|
| 1 | WIC/WPF native statt ImageSharp | ✅ ImageSharp v3 hat Lizenzfrage. WIC ist Built-in, kleinerer Footprint. |
| 2 | OperationBatches + FileOperations | ✅ Two-Phase-Commit-light, Resume nach Crash. Block-Undo wird sauber. |
| 3 | PlanAssignments mit UNIQUE(PlanId, FileId) | ✅ 1:1 ist fachlich richtig. M:N öffnet Konflikt-Hölle. |
| 4 | Libraries-Tabelle als Anker | ✅ Verhindert späteren Refactor. |
| 5 | FileHashes-Tabelle separat | ✅ Saubere Trennung physisch/abgeleitet. |
| 6 | Thumbnails.Kind/Format/ErrorCode | ✅ Error-Tracking essentiell bei FFmpeg-Fails. |
| 7 | Multi-Channel Queue + Dedupe-Versioning | ✅ PriorityQueue allein zu schwach. |
| 8 | FFmpeg-Timeout-Cancel, Bilder fertiglaufen | ✅ Praxispunkt. |
| 9 | Animated Thumbs on-demand statt vorab | ✅ 7,5 GB Cache war zu aggressiv. |
| 10 | "Tiefster Ordner = leeres Grid" UX-Problem | ✅ Subfolder-Kacheln zeigen wenn keine direkten Medien. |
| 11 | MVP V0.1 brutal klein | ✅ Plan-Ordner V0.3, Bilder + Operations zuerst. |
| 12 | Lightbox als eigenes Window | ✅ Vollbild + Multi-Monitor + Video-Interop. |
| 13 | 5 technische Spikes vor Implementation | ✅ Sehr professionell. |

## Punkte mit kleiner Differenzierung

- **Phase 7 nicht später als V0.3** für Plan-Ordner (User-zentral)
- **Drag&Drop-Token-Editor** als Polish-Feature OK, MVP = Pattern-Textfeld + Live-Preview
- **`Persons`/`Faces`-Tabellen schon im V0.1-Schema** anlegen, auch wenn leer (verhindert Migration)

## User-Entscheidungen aus Stufe A

| Frage | Antwort |
|---|---|
| Korrekturen übernehmen? | **ChatGPT für Begründung in Runde 2 fragen** (Vertiefung der wichtigsten Architekturentscheidungen) |
| Bibliotheks-Größe | **50.000–500.000 Dateien** (groß!) — FTS5/pHash/Indizes Pflicht |
| HEIC/RAW? | **Nein** — nur JPG/PNG/MP4, WIC einfach |
| Plan-Apply-Modus | **Nur Verschieben für MVP** |

## Konsequenzen für Runde 2

- **50k–500k Constraint** verschärft Anforderungen: Memory, Indizes, Batch-Größen, Skalierung kritisch
- **Nur JPG/PNG/MP4** vereinfacht Codec-Strategie deutlich (kein RAW-Workflow)
- **Move-only Apply** vereinfacht Plan-Apply-Logik
- Vertiefung von 5 strittigen Architekturpunkten + GPT-5s offene Rückfragen mit Defaults beantworten

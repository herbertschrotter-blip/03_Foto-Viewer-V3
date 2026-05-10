# Runde 1 — User-Entscheidungen

> Datum: 2026-05-10

## Stufe-A-Antworten

| Frage | Antwort | Konsequenz für Runde 2 |
|---|---|---|
| Korrekturen aus Runde 1 übernehmen? | **ChatGPT für Begründung in Runde 2 fragen** | Vertiefte Begründung von 5 strittigen Architekturpunkten (WIC, Multi-Channel-Queue, OperationBatches-Recovery, Animated-Thumbs, MVP-Schnitt) |
| Bibliotheks-Größe? | **50.000–500.000 Dateien** (groß) | FTS5 Pflicht, pHash-Index, batched Enqueue, sorgfältige Indizes, Memory-Disziplin, evtl. Filter/Suche schon im V0.1 |
| HEIC/RAW-Material? | **Nein — nur JPG/PNG/MP4** | WIC ohne Codec-Pack reicht, Codec-Strategie vereinfacht |
| Plan-Apply-Modus? | **Nur Verschieben für MVP** | `Plans.Mode = 'move'`, Copy als spätere Erweiterung |

## Defaults für GPT-5s offene Rückfragen (von Claude vorgeschlagen, in Runde 2 zur Validierung)

| Rückfrage | Default |
|---|---|
| Mehrere Libraries verwalten? | Nein im MVP, aber `Libraries`-Tabelle und `LibraryId`-FKs schon im V0.1 |
| Apply: Move oder auch Copy? | Nur Move ✓ |
| Undo nach Papierkorb-Löschen | "Papierkorb öffnen"-Link, Auto-Restore in Phase 8 |
| HEIC/RAW | Nein ✓ |
| Cache global oder portable | Global pro User für MVP, portable Catalog später optional |
| Mehrere Monitore / Vollbild | Ja, supportet (Lightbox als eigenes Window) |
| Feature-Parität vs. Kern | Schlanker Foto-/Video-Kern wichtiger |

## Status

- ✅ Runde-1-Prompt erstellt → an GPT-5
- ✅ GPT-5-Antwort archiviert
- ✅ Stufe-A-Einschätzung von Claude
- ✅ User-Entscheidungen dokumentiert
- 🟡 Runde-2-Prompt erstellt → bereit zum Versenden an GPT-5

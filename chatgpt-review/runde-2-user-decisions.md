# Runde 2 — User-Entscheidungen

> Datum: 2026-05-10

## Stufe-A-Antworten

| Frage | Antwort | Konsequenz für Runde 3 |
|---|---|---|
| V0.1 Scope | **Produktiv nutzbar in 4–6 Wochen** | FTS5 + Filter + Favorit Pflicht. V0.1 ersetzt PowerShell-Viewer. |
| Storage | **Gemischt — Bibliothek wandert** | App braucht `StorageProfileService` mit Drive-Type-Detection und Profile-Switching. Cache-Strategie zu klären. |
| MP4 in V0.1 | **Mit LibVLCSharp** | Spike vorziehen, V0.1 hat komplette Lightbox. Risiko bei Video-Edge-Cases. |
| Status-UI | **Alle 4 Indikatoren** | Status-Service mit Subscriber-Pattern. Scan/Thumbs/Background/Recovery transparent. |

## Defaults für GPT-5s offene Rückfragen 4+5 (in Runde 3 zu validieren)

| Rückfrage | Default-Vorschlag |
|---|---|
| Favorit/Rating in V0.1? | Favorit ja, Rating später |
| Migration-Disziplin ab wann? | DB-Reset bis V0.1 abgeschlossen, ab V0.2 EF Core Migrations strict |

## Status

- ✅ Runde-1 abgeschlossen
- ✅ Runde-2 abgeschlossen
- ✅ User-Entscheidungen dokumentiert
- 🟡 Runde-3-Prompt erstellt → bereit zum Versenden an GPT-5
- ⏳ Nächster Schritt: Aufwands-Reality-Check, Storage-Profile-Details, finale ADRs, Tag-1-Plan

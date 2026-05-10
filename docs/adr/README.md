# Architecture Decision Records (ADRs)

Dieses Verzeichnis enthält die Architekturentscheidungen für den PhotoViewer V3 C#-Port.

## Was ist ein ADR?

Ein **Architecture Decision Record** dokumentiert eine wichtige Architekturentscheidung mit Kontext, Begründung und Konsequenzen. Format nach Michael Nygard (leicht erweitert).

## Lifecycle / Status

- **Proposed** — Diskussion läuft
- **Accepted** — Entschieden, gilt
- **Deprecated** — Nicht mehr verwendet, aber noch im Code vorhanden
- **Superseded by ADR-NNNN** — Durch neueres ADR ersetzt

Wenn eine ADR-Entscheidung revidiert wird: **neues ADR anlegen** + altes auf "Superseded by ADR-NNNN" setzen, **niemals altes ADR überschreiben** (Audit-Trail).

## Übersicht

| # | Titel | Status |
|---|---|---|
| [0001](0001-runtime-and-ui-stack.md) | Runtime und UI-Stack (.NET 10 + WPF + WPF-UI) | Accepted |
| [0002](0002-multi-project-solution.md) | Multi-Project Solution ab Tag 1 | Accepted |
| [0003](0003-catalog-modell.md) | Catalog-Modell (eine aktive Library, zentrale DB) | Accepted |
| [0004](0004-datenzugriff-ef-core-raw-sqlite.md) | Datenzugriff: EF Core + Raw SQLite | Accepted |
| [0005](0005-migration-disziplin.md) | Migration-Disziplin | Accepted |
| [0006](0006-bild-und-video-pipeline.md) | Bild- und Video-Pipeline (WIC + FFmpeg + LibVLC) | Accepted |
| [0007](0007-thumbnail-scheduling.md) | Thumbnail-Scheduling (Multi-Channel Queue) | Accepted |
| [0008](0008-storage-profile.md) | Storage-Profile (SSD/HDD/NAS) | Accepted |
| [0009](0009-cache-strategie.md) | Cache-Strategie (lokal in LocalAppData) | Accepted |
| [0010](0010-ui-datenmodell-paging.md) | UI-Datenmodell mit Paging | Accepted |
| [0011](0011-suche-und-filter-v01.md) | Suche und Filter in V0.1 | Accepted |
| [0012](0012-operation-logging.md) | Operation-Logging (Batches + Operations) | Accepted |
| [0013](0013-status-ui.md) | Status-UI mit 4 Indikatoren | Accepted |
| [0014](0014-animated-hover-not-v01.md) | Animated Hover NICHT in V0.1 | Accepted |
| [0015](0015-face-recognition-not-v01-schema.md) | Face Recognition NICHT im V0.1-Schema | Accepted |

## Quellen der Entscheidungen

Die ADRs wurden in 3 ChatGPT-Review-Runden mit GPT-5 validiert. Siehe [`chatgpt-review/`](../../chatgpt-review/) für Original-Diskussion.

## Template für neue ADRs

Siehe [TEMPLATE.md](TEMPLATE.md) als Vorlage für neue ADRs.

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
| [0016](0016-formatregister.md) | Formatregister statt fester Formatliste | Accepted |
| [0017](0017-modul-entkopplung-messenger.md) | Modul-Entkopplung über WeakReferenceMessenger und Modul-Registrierungen | Accepted |
| [0018](0018-datei-operationen-nur-ueber-service.md) | Datei-Operationen ausschließlich über IFileOperationService | Accepted |
| [0019](0019-incremental-scan.md) | Incremental-Scan: Identität Ordner+Name, Missing statt Löschen, Mass-Missing-Guard | Accepted |
| [0020](0020-bibliothek-mehrere-wurzeln-rollen.md) | Bibliothek mit mehreren Wurzeln und Rollen (Eingang / Archiv) | Accepted |
| [0021](0021-tags-eigene-tabellen.md) | Tags als eigene Tabellen mit Katalog-Zuweisung | Accepted |
| [0022](0022-archive-als-katalog-eintraege.md) | Archive als Katalog-Einträge, Entpacken über SharpCompress | Accepted |
| [0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md) | „Neu aufbauen“ übernimmt Favoriten, Bewertungen und Tags über den Pfad | Accepted |
| [0024](0024-sortierer-ziel-nur-unter-bibliothekswurzeln.md) | Sortierer und Flatten: Quelle und Ziel nur unter den Wurzeln der Bibliothek | Accepted |

## Quellen der Entscheidungen

ADR-0001 bis ADR-0015 wurden in 3 ChatGPT-Review-Runden mit GPT-5 validiert. Siehe [`chatgpt-review/`](../../chatgpt-review/) für Original-Diskussion.
ADR-0016 bis ADR-0019 (und ADR-0020 ab 0.2.5) dokumentieren die Entscheidungen der Umsetzung bis Release 0.2.0 (siehe [CHANGELOG.md](../../CHANGELOG.md)).

## Template für neue ADRs

Siehe [TEMPLATE.md](TEMPLATE.md) als Vorlage für neue ADRs.

# ADR-0003: Catalog-Modell (eine aktive Library, zentrale DB)

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5

## Kontext

Wie wird die Bibliothek-Verwaltung modelliert? Eine Library zur Zeit oder mehrere parallel? Wo liegt die DB-Datei — zentral pro Windows-User oder portable Catalog neben dem Foto-Root (Lightroom-Stil)?

## Entscheidung

**MVP nutzt genau eine aktive Library**, DB unter `%LocalAppData%\PhotoViewerV3\library.db`. **Schema enthält `Libraries`-Tabelle und `LibraryId`-FKs auf allen User-Daten** (Files, Folders, Plans, Settings), auch wenn V0.1 keine Multi-Library-UI hat.

## Begründung

- **Eine Library im MVP** vereinfacht UI und Konzept drastisch (keine Library-Switching-UI, keine Sync-Probleme).
- **Zentrale DB unter `%LocalAppData%`** vermeidet portable-Catalog-Komplexität (Pfad-Probleme bei Laufwerkswechsel, Schreibrechte auf Netzwerkshares, OneDrive-Indexing-Konflikte, Backup-Verhalten).
- **`Libraries` + `LibraryId`-FKs im Schema** trotzdem vorbereiten, damit portable Catalogs oder mehrere Bibliotheken später ohne Schema-Migration ergänzbar sind.

## Konsequenzen

### Positiv
- MVP-UI bleibt einfach (keine Library-Auswahl, kein Switching)
- Zentrale DB = Pro Windows-User isoliert, keine Schreibrechte-Probleme, kein Sync-Churn
- Schema zukunftssicher für Multi-Library und portable Catalogs später
- Backup-Strategie trivial (`%LocalAppData%` einmal sichern)

### Negativ
- Cache wandert nicht mit, wenn Bibliothek auf anderen PC zieht (akzeptabel im MVP)
- DB ist nicht "beim Foto-Ordner" → User sieht sie nicht direkt

### Neutral
- Spätere Erweiterung um portable Catalog möglich: zusätzliche Library mit `RootPath` auf Wechsel-Drive, DB-Speicherort konfigurierbar

## Alternativen

- **Mehrere Libraries direkt im MVP** → verworfen weil: UI-Komplexität (Switcher, Default-Library, leere States), V0.1 lieber fokussiert.
- **Lightroom-Stil portable Catalog neben Root-Ordner** (`<Root>\.photoviewerv3\library.db`) → verworfen weil: Pfad-/Laufwerks-/Rechteprobleme, OneDrive-Sync-Risiko, NAS-Schreibrechte-Probleme, Backup-Komplexität.
- **DB neben ausführbarer Datei** → verworfen weil: nicht pro Windows-User isoliert, Portable-Modus nicht intuitiv.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "Zentrale DB vs. Catalog neben Root"

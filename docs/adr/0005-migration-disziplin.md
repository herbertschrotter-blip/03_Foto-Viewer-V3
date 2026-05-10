# ADR-0005: Migration-Disziplin

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5
- **Bezogen auf:** [ADR-0004](0004-datenzugriff-ef-core-raw-sqlite.md)

## Kontext

Während V0.1-Entwicklung wird Schema sich häufig ändern (Schemaentwicklung im Frühstadium). Ab welchem Zeitpunkt muss strict mit EF Core Migrations gearbeitet werden, statt einfach DB zu löschen und neu zu erstellen?

## Entscheidung

- **Bis zur ersten produktiven Nutzung von V0.1**: DB-Reset (`EnsureDeleted` / `EnsureCreated`) erlaubt.
- **Ab erster produktiver Nutzung** (User hat reale Favoriten / Settings, möchte DB behalten): **strict `Database.MigrateAsync()`**, kein Reset mehr.
- **Spätestens ab V0.2** strict Migrations.
- Migration **automatisch beim App-Start** mit WAL-Checkpoint + versioniertem Backup + UI-Gate.

## Begründung

- V0.1-Entwicklung muss schemaflexibel bleiben → Reset-Freiheit produktiv.
- Sobald User reale Daten reinpflegt (Favoriten, später Tags/Plans/History), wäre Reset Datenverlust → strict Migrations.
- Grenze ist **erste produktive Nutzung**, nicht "letzter Tag der Entwicklung" — das ist die nachvollziehbare semantische Grenze.
- Auto-Migration mit Backup + UI-Gate: User ist kein Server-Admin, CLI-only zu unfreundlich.

## Konsequenzen

### Positiv
- V0.1-Entwicklung bleibt schnell und flexibel
- Datensicherheit ab erster realer Nutzung
- Versioniertes Backup vor jeder Migration (`library.db.backup-YYYYMMDD-HHMMSS-vX.Y-to-vX.Y+1`)
- WAL-Checkpoint vor Backup garantiert konsistente Sicherung

### Negativ
- Disziplin nötig: Entwickler muss wissen, ob er V0.1 noch reset darf
- Migration-Code muss von Anfang an in Migrations-Verzeichnis statt nur EF Core auto-generieren
- Fehlerbehandlung bei fehlgeschlagener Migration komplexer (Restore-Dialog, Lock-Konflikte)

### Neutral
- Backup wird nicht automatisch gelöscht (versioniert behalten)
- Entwickler-CLI `PhotoViewerV3.Tools migrate / verify-db / rebuild-fts` ist optional nice-to-have, nicht zwingend V0.1

## Alternativen

- **Sofort strict ab Tag 1** → verworfen weil: zu starr für Frühphase, jede Schemaänderung erfordert Migration auch für Wegwerf-Daten.
- **Reset bis V0.2** (ohne Grenze "produktive Nutzung") → verworfen weil: schwammig, User könnte Daten verlieren falls er V0.1 schon nutzt.
- **CLI-only Migrations** → verworfen weil: User ist kein Server-Admin.

## Fehlerbehandlung bei Migrationsfehler

```
Datenbank-Aktualisierung fehlgeschlagen
Die App konnte die Datenbank nicht aktualisieren.
Backup wurde erstellt: ...\library.db.backup-...
Fehler: <Message>
[Backup wiederherstellen] [App schließen] [Log öffnen]
```

**Niemals mit halb migrierter DB normal starten.**

## Referenzen

- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) "Migration-Disziplin"

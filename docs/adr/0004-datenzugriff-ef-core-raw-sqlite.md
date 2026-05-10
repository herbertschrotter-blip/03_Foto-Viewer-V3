# ADR-0004: Datenzugriff — EF Core + Raw SQLite

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5
- **Bezogen auf:** [ADR-0005](0005-migration-disziplin.md), [ADR-0007](0007-thumbnail-scheduling.md)

## Kontext

Datenzugriff für SQLite-DB mit 50k–500k Dateien. Welche Strategie — voller ORM, Micro-ORM, raw SQL? Initial-Scan für 500k Dateien naiv via EF Core `SaveChanges` dauert laut GPT-5-Reality-Check **16–83 Minuten**. Acceptable?

## Entscheidung

**EF Core 10 für CRUD und Migrations**, **Raw `Microsoft.Data.Sqlite` Prepared Commands in Transactions** für Bulk-Insert (Initial-Scan), FTS5-Indexing und performance-kritische Queries.

WAL-Mode + Single-Writer-Pattern (Channel oder `SemaphoreSlim`).

## Begründung

- **EF Core**: Tags, Plans, Albums, History, Favoriten = relationale Domäne mit klaren Migrations. LINQ-Queries sind hier produktiv.
- **Raw SQLite für Bulk**: 500k Inserts via EF `AddRange` mit Batches sind besser als naiv, aber **Raw prepared INSERT in einer Transaktion** ist 60–80 % schneller (laut GPT-5-Spike-Erfahrung: <5 min statt 16–83 min).
- **Raw SQL für FTS5**: FTS5 sind virtuelle Tabellen, EF Core unterstützt das nicht sauber → Raw Migration-SQL + Raw Update bei Scan.
- **WAL-Mode** erlaubt concurrent reads während schreiben, kritisch wenn UI parallel zum Scan liest.

## Konsequenzen

### Positiv
- Best-of-both: produktive Domäne mit EF, Bulk-Performance mit Raw
- Initial-Scan <5 min auf SSD machbar
- FTS5 sauber implementierbar
- UI bleibt während Scan responsive (WAL)

### Negativ
- Zwei Wege zur DB → Disziplin nötig (welcher Code geht welchen Weg)
- Raw SQL ist nicht typsicher, mehr Test-Bedarf
- Einrichtung von `DbContextFactory` + Raw `SqliteConnection` parallel

### Neutral
- `Microsoft.Data.Sqlite` ist bereits transitiv über EF Core dabei, wird trotzdem explizit referenziert
- Raw-SQL-Code lebt in `PhotoViewerV3.Data/RawSql/`

## Alternativen

- **Nur EF Core** → verworfen weil: Initial-Scan-Performance bei 500k inakzeptabel, FTS5 nicht sauber abbildbar.
- **Nur Dapper** → verworfen weil: Migrations manuell unschön, LINQ-Komfort fehlt für komplexere Queries (Tags, Plans, Joins).
- **Nur Raw `Microsoft.Data.Sqlite`** → verworfen weil: keine Migrations, kein Tracking, viel Boilerplate.
- **LiteDB (NoSQL embedded)** → verworfen weil: kein FTS5, weniger ausgereift, Schema-Diszplin schwerer.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "Initial-Scan-Performance"
- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) NuGet-Pakete + Raw SQL Beispiele

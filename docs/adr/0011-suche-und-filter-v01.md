# ADR-0011: Suche und Filter in V0.1

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5

## Kontext

User wünscht V0.1 als **produktiver Viewer** für 50k–500k Bibliothek. Ohne Suche und Filter wäre eine Bibliothek dieser Größe nicht navigierbar — User würde nur scrollen können. Frage: kommen Suche/Filter schon in V0.1, oder erst V0.2?

## Entscheidung

**V0.1-Pflicht** (nicht streichbar):

- **FTS5 minimal** für `Files.Name` und `Files.Extension`
- **Filter-Pills**: Alle / Bilder / Videos / Favoriten
- **Favorit** speichern und nutzen (Toggle pro Datei, in Filter)
- **Rating** kommt später (V0.2+)

## Begründung

- Bei 500k Files ist **Suche nicht Komfort, sondern Voraussetzung** für produktiven Einsatz.
- Ohne Filter "Nur Videos" / "Nur Favoriten" ist die App eine reine Scroll-App.
- **FTS5** für Filename allein ist klein (50–200 MB Index bei 500k) und schnell (<200ms Query).
- **Favorit ist billig** (eine Spalte `IsFavorite INTEGER` mit Index), als Filter hochgradig nützlich.
- **Rating kann warten** — 1-5-Sterne-UI plus DB-Spalte ist mehr Aufwand und in V0.1 nicht kritisch.

## Konsequenzen

### Positiv
- V0.1 ist wirklich **produktiv nutzbar**, nicht nur Developer-Preview
- Suchanfragen <200ms auch bei 500k
- Filter sind über DB-Indizes schnell

### Negativ
- FTS5-Setup als Pflicht-Spike (Spike #1 / #6 in Spike-Liste)
- FTS5 muss bei Scan + Move/Rename aktualisiert werden (Trigger oder expliziter Indexer-Update)
- Index-Größe ~50–200 MB bei 500k Files

### Neutral
- FTS5 in V0.1 **minimal**: nur `Files.Name + Extension`. Tags/EXIF/Volltext kommt später.
- Filter-Pills im Mockup `04-fluent-light.html` schon vorgesehen
- Pflicht-Indizes: `IX_Files_Library_MediaType`, `IX_Files_Library_Favorite`

## Schema-Eintrag

```sql
-- FTS5 virtual table für Suche
CREATE VIRTUAL TABLE FilesFts USING fts5(
  Name,
  Extension,
  content='Files',
  content_rowid='Id'
);

-- Update beim Scan/Rename via expliziter Indexer-Service
-- (keine Trigger-Magie im MVP, um Bulk-Insert nicht zu verlangsamen)
```

## Alternativen

- **Nur `LIKE '%query%'`** → verworfen weil: bei 500k zu langsam für interaktive Suche, kein Substring-Index.
- **Suche erst V0.2** → verworfen weil: V0.1 wäre für User unbenutzbar bei 500k.
- **Lucene.NET** → verworfen weil: separates Storage, Deployment-Komplexität, SQLite FTS5 reicht.
- **Volltextsuche über EXIF/Tags schon V0.1** → verworfen weil: keine Tags in V0.1 (siehe ADR-0015 Phase-9), Scope-Creep.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "MVP-Schnitt für 50k-500k"

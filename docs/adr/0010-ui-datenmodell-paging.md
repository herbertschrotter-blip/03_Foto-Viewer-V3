# ADR-0010: UI-Datenmodell mit Paging

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5

## Kontext

Bei 50k–500k Dateien wäre eine `ObservableCollection<File>` mit allen Items im UI-Grid ein Memory- und Performance-Killer. WPF virtualisiert zwar Visuals, aber **nicht CollectionView-Kosten, Sort/Filter-Kosten, Selection-State-Kosten und Scroll-Extent-Berechnung**. Was ist die UI-Datenstrategie?

## Entscheidung

**Paged virtualisiertes Grid** über **Query-DTOs**. UI lädt nur Teilmenge (200–1000 Items pro Page um den Viewport). **Keine `ObservableCollection<500.000 Items>`**. **`SelectionStore` separat** nach `FileId`, nicht an aktuelle Page gebunden.

## Begründung

- 500k Items in ObservableCollection = mehrere hundert MB Memory + sekundenlange Sort/Filter-Operationen.
- WPF-Virtualisierung verhindert nur Visual-Creation, nicht alle Collection-Operationen.
- **DTOs statt EF-Entities** im UI: kleine Records mit nur den Feldern, die das Grid braucht (FileId, Name, ThumbPath, MediaType, IsFavorite).
- **Paging über DB**: `SELECT ... FROM Files WHERE ... ORDER BY ... LIMIT 1000 OFFSET @offset`.
- **`SelectionStore` als Singleton-Service** mit `ObservableHashSet<long FileId>`: bleibt erhalten, wenn Items aus Viewport scrollen oder Filter sich ändert.

## Konsequenzen

### Positiv
- Memory stabil auch bei 500k Bibliothek
- UI-Responsiveness bleibt
- Schnelle Filter/Sort-Operationen (DB statt LINQ-in-Memory)
- Selection überlebt Filter-/Suche-Wechsel
- Architektur skaliert auf 1 M+ Dateien

### Negativ
- **Paging-Logik nötig** (Offset/Limit, Cursor, Cache von Pages um Viewport)
- Selection-Sync zwischen DB-Query-DTOs und SelectionStore
- Mehr Architektur-Boilerplate als naives Binding

### Neutral
- ViewModel hält IDs und kleine DTOs, **keine EF Entities, keine ImageSource-Referenzen**
- "Select All" ist über DB-Query, nicht über UI-Iteration

## Designregel (kritisch)

> UI hält IDs und kleine DTOs, keine EF Entities, keine großen Images, keine offenen Streams.

## Beispiel-Interface

```csharp
public interface IMediaQueryService
{
    Task<IReadOnlyList<MediaTileDto>> QueryPageAsync(
        MediaQuery query,
        int offset,
        int limit,
        CancellationToken ct);

    Task<int> CountAsync(MediaQuery query, CancellationToken ct);
}

public sealed record MediaTileDto(
    long FileId,
    string Name,
    string? ThumbPath,
    string MediaType,
    bool IsFavorite,
    int? DurationMs);
```

## Alternativen

- **`ObservableCollection<File>` mit allen Items** → verworfen weil: UI-Death bei 500k, Memory-Explosion.
- **`CollectionViewSource` mit Filter** → verworfen weil: filtert in Memory, nicht DB.
- **Lucene.NET / vergleichbarer Index** → verworfen weil: SQLite FTS5 reicht, weniger Deployment-Komplexität.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "WPF VirtualPanel-Limits"

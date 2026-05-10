# ADR-0013: Status-UI mit 4 Indikatoren

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter (explizit gewählt), Claude, GPT-5

## Kontext

Bei 50k–500k Dateien laufen viele Background-Jobs sichtbar lange (Initial-Scan kann Minuten bis Stunden dauern, Thumbnail-Queue Stunden, FTS-Indexing Minuten). Wenn User keine Statusanzeige sieht, denkt er, die App hängt. **Vertrauen bei langen Jobs** ist kritisch — User-Wunsch explizit: alle 4 Status-Typen.

## Entscheidung

**V0.1 zeigt 4 Statusindikatoren** in der Statusbar / als Toast / als InfoBar:

1. **Scan-Fortschritt** — z. B. "Scan 128.000 / 500.000 Dateien"
2. **Thumbnail-Queue-Status** — z. B. "Thumbs: 87 / 234 pending (🟠 Viewport)"
3. **Background-Aktivität** — subtiler Hinweis dass App im Hintergrund arbeitet (z. B. spinnender Dot in Statusbar)
4. **Recovery-Hinweise** — Warnung beim App-Start wenn `OperationBatches` mit Status `applying` / `failed` gefunden

**Darf technisch aussehen, muss aber zuverlässig sein.**

## Begründung

- User-Vertrauen bei langen Jobs ist kein Polish, sondern Produktanforderung.
- Bei 500k Files dauert Initial-Scan auch bei optimierter Pipeline mehrere Minuten — ohne Status denkt User, App ist eingefroren.
- 4 unterschiedliche Status-Typen, weil sie unabhängig laufen können (Scan + Thumbs + Recovery parallel möglich).
- "Technisch aussehen darf, muss zuverlässig sein" — keine UX-Polish-Pflicht, aber **muss korrekte Werte zeigen**.

## Umsetzung

**`StatusService`** als Singleton mit Subscriber-Pattern:

```csharp
public interface IStatusService
{
    IObservable<ScanProgress> ScanProgress { get; }
    IObservable<ThumbnailQueueState> ThumbQueueState { get; }
    IObservable<BackgroundActivity> BackgroundActivity { get; }
    IObservable<RecoveryWarning> RecoveryWarnings { get; }
}
```

Statusbar-ViewModel bindet an die 4 Observables, rendert in fester Position unten.

Recovery-Warning wird beim App-Start einmal als **InfoBar** über Content angezeigt, **bevor User navigieren kann**:

```
Unvollständiger Dateivorgang gefunden
Plan "Urlaub Italien" wurde nicht vollständig abgeschlossen.
[Fortsetzen] [Reparatur prüfen] [Schließen]
```

## Konsequenzen

### Positiv
- User-Vertrauen bei langen Background-Jobs
- App fühlt sich "lebendig" an, nicht eingefroren
- Recovery-Warnungen sind nicht übersehbar
- Status-Service ist wiederverwendbar für V0.2+ Features

### Negativ
- Extra Service + 4 Observable-Streams + ViewModel-Bindings
- Status-Updates müssen gedrosselt werden (nicht pro Datei, sondern alle 200-500 ms)
- Statusbar-Layout im Mockup vorgesehen, muss implementiert werden

### Neutral
- Format / Visualisierung darf technisch sein ("128k / 500k" statt schöner Progress-Ring), Hauptsache zuverlässig
- Updates throttled via Rx (`Sample(TimeSpan)`) oder eigenes Debouncing
- Tag-1-Plan beinhaltet noch keinen StatusService — kommt in vertikalen Slices ab Scan-Implementation

## Alternativen

- **Keine Status-UI** → verworfen weil: User-Wunsch, kritisch bei 500k.
- **Nur einen kombinierten Status** → verworfen weil: 4 Typen laufen unabhängig parallel.
- **Aufwendige animierte Progress-Visualisierung** → erst Polish-Phase, V0.1 darf technisch aussehen.

## Referenzen

- [chatgpt-review/runde-2-user-decisions.md](../../chatgpt-review/runde-2-user-decisions.md) — User-Wahl explizit
- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) Status-UI als "Vertrauen, nicht Polish"

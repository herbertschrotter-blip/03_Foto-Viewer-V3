# ADR-0007: Thumbnail-Scheduling (Multi-Channel Queue)

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5
- **Bezogen auf:** [ADR-0006](0006-bild-und-video-pipeline.md), [ADR-0008](0008-storage-profile.md)

## Kontext

Bei 50k–500k Dateien müssen Thumbnails priorisiert generiert werden. User-Wunsch: wenn er in anderen Ordner springt, soll laufende Generierung pausieren, neuer Ordner zuerst, dann Resume des alten Jobs. Scrollen ändert Viewport-Priorität dynamisch. Eine einfache `PriorityQueue<T,P>` reicht laut GPT-5-Review nicht.

## Entscheidung

**Multi-Channel-Queue** mit 4 separaten `System.Threading.Channels.Channel<ThumbJob>` (Critical / Viewport / CurrentFolder / Background), **`ConcurrentDictionary<ThumbKey, ThumbJobState>`** für Dedupe und Versioning, **Stale-Job-Detection** beim Dequeue, **`Thumbnails.Status` + `AttemptCount`** in DB für Crash-Recovery, **`.tmp`-Write + atomic `File.Move`** für halbfertige Thumbs.

## Begründung

### Warum kein `PriorityQueue<T,P>`
- Kann Items **nicht effizient entfernen oder repriorisieren** wenn schon drin.
- Bei 500k Files mit Scroll-Repriorisierung würde die Queue explodieren.

### Warum Multi-Channel
- 4 separate Channels → Scheduler liest priority-first ohne komplexe Datenstruktur.
- Items können in höhere Priorität gepromotet werden via neuer `Version`, alte werden beim Dequeue als stale verworfen.

### Warum Dedupe-Dictionary
- Verhindert Doppel-Enqueue (Background + Viewport für denselben Key).
- `PromoteIfNeeded` erlaubt Repriorisierung ohne Queue-Search.
- Memory: 500k Keys × ~100-160 Byte = **50–80 MB** (GPT-5-Korrektur, nicht 25 MB). Akzeptabel, aber **nicht alle 500k auf einmal enqueuen** → Background in 1000–5000er-Batches.

### Pause/Resume-Mechanik
- 1 Job = 1 Datei → sauberer Pause-Punkt zwischen Jobs.
- **Image-Jobs fertiglaufen lassen** (50–300 ms, kein Hard-Cancel).
- **FFmpeg-Jobs mit `CancellationTokenSource.CancelAfter(3 s / 8 s)`** für Timeout, weil kaputte Videos minutenlang hängen können.

### Status-Tabelle + Crash-Recovery
- `Thumbnails.Status`: `missing | pending | generated | failed | stale`
- Beim App-Start: `UPDATE Thumbnails SET Status='stale' WHERE Status='pending';`
- `.tmp`-Write + atomic `File.Move(tmp, final, overwrite: true)` verhindert halbfertige sichtbare Dateien.

## Konsequenzen

### Positiv
- UI bleibt responsive bei jeder Aktion (Scroll, Folder-Switch, Lightbox)
- Crash-tolerant (Status-Tabelle + .tmp)
- Memory-Footprint kontrollierbar via Batching
- FFmpeg-Hänger fangen die App nicht ein

### Negativ
- ~250 Zeilen Code (Service nicht trivial)
- Multi-Channel + Dedupe + Versioning braucht gründliche Tests
- Polling-Wait beim Dequeue (20 ms) statt event-driven (akzeptabel, vorhersehbar)

### Neutral
- Worker-Pool: Image-Workers `max(1, CPU-2)`, FFmpeg-Workers `max 1–2` (über `SemaphoreSlim`)
- Disk-Concurrency limitiert via `SemaphoreSlim` (gegen SSD-Saturation)
- Trigger: Scroll/Resize debounced 100 ms

## Alternativen

- **`PriorityQueue<T,P>`** → verworfen weil: kann nicht repriorisieren/entfernen.
- **Hangfire / Quartz.NET** → verworfen weil: Overkill für lokale Desktop-App, persistente Queue unnötig.
- **Eigenes Persistent-Queue** → verworfen weil: DB-Status reicht für Crash-Recovery, Queue-Persistierung unnötig.
- **Hard-Cancel aller Jobs bei Prio-Wechsel** → verworfen weil: Bilder regenerieren ist Verschwendung, FFmpeg-Restart wäre Thrashing.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "Multi-Channel Queue" (vollständiger Code)

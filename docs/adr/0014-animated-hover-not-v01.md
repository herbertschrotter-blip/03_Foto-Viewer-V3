# ADR-0014: Animated Hover-Thumbs NICHT in V0.1

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter (Wunsch für Feature), Claude, GPT-5 (Empfehlung Cut für V0.1)

## Kontext

User-Wunsch: animierte Multi-Frame Video-Hover-Thumbnails — z. B. 5 Frames aus einem Video bei 10/30/50/70/90 % der Dauer als animiertes GIF, bei Hover über Thumb spielt es ab (wie Eagle, Apple Photos, Plex). **Bei 50k–500k Bibliothek mit Vorab-Generierung** wären das massive Cache-Größen.

## Entscheidung

**Animated Hover-Thumbs sind NICHT in V0.1.**

Geplant für **Phase 8** mit:
- Default **OFF** (Opt-in mit User-Hinweis bei erstem Video-Hover)
- **On-demand-Generierung** beim ersten Hover (nicht vorab für alle Videos)
- **LRU-Cache** mit konfigurierbarem Größenlimit (Default 2 GB)
- **MinCacheAge** (24 h) — verhindert Eviction von gerade generierten Previews
- **MaxConcurrentJobs = 1** für Hover-Generierung
- Format: animiertes GIF (WPF nativ via `WpfAnimatedGif`)
- 5 Frames Default bei 15/35/55/75 % (90 % oft Abspann/schwarz)

## Begründung

### Warum nicht V0.1
- **Cache-Größe bei 500k Videos** mit Vorab-Generierung explodiert: 5 Frames × 200 KB GIF × 500k = **bis 7,5 GB** (User würde Cache-Limit massiv überschreiten).
- **Generierung kostet CPU**: ~2–5 Sek pro Video × 500k = sehr lang.
- V0.1 hat wichtigere Pflicht-Features (FTS5, Storage-Profile, LibVLC-Lightbox).
- Animated Hover ist **Komfortfeature, kein Kernfeature**.

### Warum on-demand statt vorab
- User schaut nicht alle 500k Videos an.
- On-demand bei Hover (mit 350 ms Delay) generiert nur was relevant ist.
- LRU + 2 GB Cap hält Storage im Griff.
- Bei langsamem Scrolling: kein Thrashing, weil Job nicht hart bei MouseLeave gecancelt wird (sondern UI-CTS canceln, Job darf fertiglaufen).

### Warum Default OFF
- User muss explizit aktivieren ("Vorschauen erzeugen und bis 2 GB Cache nutzen?").
- Verhindert ungewollte Cache-Explosion.

## Konsequenzen

### Positiv
- V0.1 fokussierter, kleinerer Scope
- Keine Cache-Explosion bei 500k
- Feature später sauberer designt (Erfahrung aus V0.1-Nutzung fließt ein)

### Negativ
- User muss in V0.1 ohne Hover-Animation auskommen
- Phase 8 ist späteres Epic

### Neutral
- Schema-Spalte `Thumbnails.Kind` ist schon V0.1 vorgesehen (`still | hover | poster`), damit später kein DB-Migration für animierte Variante nötig
- Settings sind im Schema vorbereitet

## Settings (für Phase 8)

```
AnimatedHover.Enabled            = false        // Default
AnimatedHover.GenerateOnDemand   = true
AnimatedHover.DelayMs            = 350
AnimatedHover.MaxCacheSizeGB     = 2
AnimatedHover.MaxConcurrentJobs  = 1
AnimatedHover.MinCacheAgeHours   = 24
AnimatedHover.FrameCount         = 5
AnimatedHover.FramesAt           = [15, 35, 55, 75]   // %, nicht 90 (oft Abspann)
AnimatedHover.AnimationFps       = 4-6
```

## Alternativen

- **Vorab-Generierung für alle Videos** → verworfen weil: Cache-Explosion bei 500k.
- **In V0.1 mit Default ON** → verworfen weil: User scrollt durch 1000 Videos, generiert 1000 Previews ungewollt.
- **WebP statt GIF** → später prüfen (kleiner, aber WPF-Support fehlerhaft).
- **MP4-Loop statt GIF** → viele Mini-Player im Grid wären Performance-Gift.
- **Kein animiertes Hover-Feature überhaupt** → verworfen weil: User-Wunsch, klar wertvoll.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "Animated Hover-Thumbs"

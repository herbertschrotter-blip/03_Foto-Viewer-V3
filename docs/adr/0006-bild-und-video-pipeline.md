# ADR-0006: Bild- und Video-Pipeline (WIC + FFmpeg + LibVLCSharp)

- **Status:** Accepted (mit Cutline für LibVLC)
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5

## Kontext

App muss Bild-Thumbnails (JPG/PNG) und Video-Thumbnails (MP4) generieren, und MP4 in einer Lightbox abspielen. Bild-Bibliothek-Wahl (WIC native vs. ImageSharp), FFmpeg-Wrapper (FFMpegCore vs. eigener) und Video-Player (MediaElement vs. LibVLCSharp vs. MPV.NET) sind zu klären. User-Material: nur JPG/PNG/MP4 (kein HEIC, kein RAW).

## Entscheidung

- **WIC native (`BitmapDecoder` + `JpegBitmapEncoder`) für JPG/PNG-Thumbnails** mit `.tmp`-Write + atomic `File.Move`.
- **Eigener `ProcessStartInfo`-Wrapper um FFmpeg** für volle Kontrolle über `CancelAfter`/`Process.Kill` (statt FFMpegCore).
- **LibVLCSharp 3.9.7.1 für MP4-Lightbox in V0.1** mit **Cutline:** wenn Spike nach 5 PT noch nicht stabil, fällt MP4-Lightbox aus V0.1 und wird durch "Extern öffnen" + V0.2-Nachzug ersetzt.

## Begründung

### WIC vs ImageSharp
- WIC ist Windows-Built-in, **lizenzfrei** (ImageSharp v3 hat Six Labors Split License — handhabbar, aber unnötige Komplexität).
- Performance-Faustwerte (GPT-5): 100 JPEGs à 5 MB → 320px: **WIC 3–8 s, ImageSharp 5–12 s** seriell. Bei 500k × 30 ms = 4,2 h CPU-Zeit seriell.
- Kein zusätzliches NuGet, kein zusätzlicher Memory-Footprint.
- ImageSharp bleibt als Option für Spezialfälle (WebP-Encoding, exotische Formate), aber nicht im Basispfad.

### Eigener FFmpeg-Wrapper statt FFMpegCore
- FFMpegCore ist aktiv gepflegt, aber `CancelAfter` + `Process.Kill` bei kaputten/großen Videos braucht volle Prozess-Kontrolle.
- Eigener Wrapper ~50 Zeilen Code, dafür präzise Timeout-Steuerung (3 s für Background-Thumbs, 8 s für Lightbox).

### LibVLCSharp vs MediaElement vs MPV.NET
- **MediaElement** hängt an Windows Media Foundation, versagt bei WMV3 / MPEG-2 / alten AVI-Codecs → für gemischten Codec-Mix unbrauchbar.
- **LibVLCSharp** liefert eigene Codecs (40 MB Deployment), bekanntes Verhalten bei "wildem" User-Material.
- **MPV.NET** technisch stark, aber LibVLCSharp hat größeres .NET-Ökosystem.
- **Cutline auf "Extern öffnen"**: falls Spike nach 5 PT zeigt, dass WPF-Interop (Dispose, Fullscreen, Focus, Multi-Monitor) instabil ist, ist V0.1 nicht blockiert.

## Konsequenzen

### Positiv
- Bild-Pipeline schnell, klein, kein Lizenzthema
- Eigener Wrapper = volle Kontrolle, kein Wait auf Library-Updates bei FFmpeg-Bugs
- LibVLCSharp deckt alle realistischen User-Codecs ab
- Cutline reduziert V0.1-Risiko

### Negativ
- WIC ist Windows-only (passt zum Projekt)
- 40 MB Deployment für LibVLC (akzeptiert)
- Eigener FFmpeg-Wrapper = mehr Test-Bedarf
- LibVLC-Spike kann scheitern → V0.1 ohne MP4-Lightbox

### Neutral
- WIC-Encoder muss `BitmapCacheOption.OnLoad` + `FileShare.ReadWrite | FileShare.Delete` setzen (Cloud-Sync-tolerant, Memory-Leak-Schutz)
- `.tmp`-Write + atomic `File.Move` verhindert halbfertige Thumbnails

## Alternativen

- **MediaElement als primärer Player** → verworfen weil: Codec-Probleme bei WMV3/MPEG-2.
- **ImageSharp als Default** → verworfen weil: langsamer + Lizenz-Komplexität + WIC reicht.
- **FFMpegCore** → verworfen weil: weniger Cancel-Kontrolle bei hängenden Videos.
- **MPV.NET** → verworfen weil: kleineres .NET-Ökosystem, weniger Erfahrungswerte.

## Cutline für V0.1

```
LibVLC-Spike grün (≤5 PT, stabil bei MP4 + Edge-Cases):
  → LibVLC-Lightbox in V0.1

LibVLC-Spike rot (>5 PT oder instabil):
  → V0.1 zeigt statisches Video-Thumbnail + "Extern öffnen"
  → LibVLC-Lightbox kommt in V0.2
```

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "WIC vs ImageSharp"
- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) "Aufwand-Reality-Check"

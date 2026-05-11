# Spike #05 — FFmpeg Cancel/Timeout MP4

- **Status:** 🚧 In Arbeit
- **Datum gestartet:** 2026-05-11
- **Bezogen auf:** [ADR-0006](../adr/0006-bild-und-video-pipeline.md), [PLAN.md Kap. 8 + 10 + 13](../../PLAN.md)
- **Blockiert (operativ, nicht V0.1-blockierend):** Video-Thumb-Pipeline

## Zielsetzung

Beweisen, dass der **eigene `ProcessStartInfo`-Wrapper** für FFmpeg-Aufrufe sich sauber via `CancellationToken.CancelAfter(timeout)` abbrechen lässt — auch wenn FFmpeg in einem kaputten Container hängt. Kein FFMpegCore-NuGet (laut Stack-Entscheidung in PLAN Kap. 2, ADR-0006).

## Messziele (PLAN Kap. 8)

| Bereich | Kriterium |
|---|---|
| Standbild aus gesundem MP4 (~52 s Sintel) | erfolgreich, < 1 s |
| Cancel via CancellationToken nach 3 s Timeout | Process exit innerhalb 200 ms nach Token-Cancel |
| Cancel via CancellationToken nach 8 s Timeout | exit zuverlässig |
| Cancel bei "kaputtem" MP4 (gekürzte Bytes) | exit innerhalb des Timeouts, kein Hängen |
| Keine zurückbleibenden ffmpeg.exe-Zombie-Prozesse nach Cancel | Pflicht |

## Aufbau

### Voraussetzung
- `ffmpeg.exe` im PATH (z.B. via `winget install Gyan.FFmpeg`)
- Sintel-Trailer-MP4 aus Spike #3 als gesundes Test-Asset
- Synthetic "broken MP4" durch Truncation des Sintel-MP4 erzeugen

### Strategie
- `ProcessStartInfo` mit `RedirectStandardError = true` (FFmpeg loggt nach stderr)
- `Process.Start()` + manuell `WaitForExitAsync(ct)` mit registriertem Cancel-Handler
- Cancel-Handler: `Process.Kill(entireProcessTree: true)` + ggf. `Process.WaitForExit(grace=2s)`
- `CancellationTokenSource.CancelAfter(timeout)` für hartes Timeout-Limit

### Komponenten
- **`FfmpegProcessRunner`** (Service-Klasse)
  - `Task<FfmpegResult> ExtractStillFrameAsync(string inputPath, string outputJpgPath, TimeSpan timeout, CancellationToken ct)`
  - kapselt Process-Lifecycle + Kill-on-Cancel
  - misst Run-Duration + Exit-Code
- **`Spike05FfmpegBenchmark`** (Runner)
  - 4 Test-Cases:
    1. Healthy MP4 — sollte funktionieren
    2. Healthy MP4 mit kurzem (1 s) Timeout — sollte fast immer canceln
    3. Truncated MP4 mit 3 s Timeout — soll canceln, kein Hang
    4. Non-existent file — soll sauber fehlschlagen

## Hardware-Profil

(identisch Spike #1–#4)

## Ergebnis

_(nach Run füllen)_

## Schlussfolgerung

_(nach Run)_

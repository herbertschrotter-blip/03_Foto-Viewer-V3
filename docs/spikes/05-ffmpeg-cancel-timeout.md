# Spike #05 — FFmpeg Cancel/Timeout MP4

- **Status:** ✅ Grün — alle 5 Tests passed
- **Datum gestartet:** 2026-05-11
- **Datum abgeschlossen:** 2026-05-11
- **Bezogen auf:** [ADR-0006](../adr/0006-bild-und-video-pipeline.md), [PLAN.md Kap. 8 + 10 + 13](../../PLAN.md)
- **Blockiert (jetzt frei):** Video-Thumb-Pipeline für V0.1

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

Run vom 2026-05-11 mit FFmpeg 8.1.1 (Gyan.FFmpeg via winget) auf i7-8700K + SATA-SSD, Sintel-Trailer 480p (4,2 MB) als Input.

| # | Test | Duration | Success | Cancelled | ExitCode | Ampel |
|---|---|---:|:---:|:---:|---:|:---:|
| 1 | Healthy Frame Extract, 5 s Timeout | **96 ms** | true | false | 0 | 🟢 |
| 2 | Slow Reencode (libx264 slower) + 500 ms Timeout | **539 ms** | false | **true** | -1 | 🟢 |
| 3 | Truncated MP4 (50 KB), 3 s Timeout | **55 ms** | false | false | -1094995529 (AVERROR_INVALIDDATA) | 🟢 |
| 4 | Non-existent Input, 3 s Timeout | **52 ms** | false | false | -2 (ENOENT) | 🟢 |
| 5 | Slow Reencode + External Cancel nach 300 ms | **325 ms** | false | **true** | -1 | 🟢 |

### Zielvergleich

| Messung | Ist | Ziel | Status |
|---|---|---|---|
| Standbild aus gesundem MP4 | 96 ms | < 1 s | 🟢 ~10× Reserve |
| `CancelAfter`-Timeout-Reaktion (Test 2) | Kill ~39 ms nach Timeout | < 200 ms | 🟢 ~5× Reserve |
| External CancellationToken-Reaktion (Test 5) | Kill ~25 ms nach Trigger | < 200 ms | 🟢 ~8× Reserve |
| Kaputter Container (Test 3) | sauberer Exit nach 55 ms | kein Hang | 🟢 |
| Non-existent File (Test 4) | fail-fast 52 ms | < 2 s | 🟢 ~38× Reserve |
| Process-Zombies nach Cancel | 0 (in `proc.WaitForExit(2s)`-Grace-Window beendet) | 0 | 🟢 |

## Schlussfolgerung

**Eigener `ProcessStartInfo`-Wrapper mit `Process.Kill(entireProcessTree:true)` funktioniert robust.** Sowohl `CancelAfter`-Timeout als auch externer `CancellationToken` schießen FFmpeg innerhalb **<50 ms** nach Trigger ab — deutlich unter dem 200-ms-Ziel.

Wichtige praktische Erkenntnis: **FFmpeg's eigene Fehlerbehandlung ist so schnell, dass Process.Kill bei kaputten Files gar nicht zum Einsatz kommt** — Test 3 zeigt 55 ms Exit ohne unsere Hilfe. Die Cancel-Mechanik ist eine Sicherheits-Reserve für pathologische Fälle, die im Spike nicht reproduzierbar waren.

Freigabe für:
- `FfmpegThumbnailService` in V0.1 als ProcessStartInfo-Wrapper (kein FFMpegCore-NuGet)
- `CancellationTokenSource.CancelAfter(3s)` für normale MP4-Thumbnail-Extracts
- `CancellationTokenSource.CancelAfter(8s)` für Worst-Case (große oder fragmentierte Files)
- Worker-Pool mit max 1–2 FFmpeg-Workern parallel (entspricht ADR-0008 `LocalSsd.MaxVideoWorkers`)

## Folge-Erkenntnisse

1. **FFmpeg-Lookup-Strategie funktioniert.** `TryLocateFfmpeg()` findet FFmpeg via `cmd.exe /c where ffmpeg.exe` und Fallback auf winget-Standard-Link-Pfad. In Production sollte der Pfad zusätzlich konfigurierbar sein (Settings.json), falls FFmpeg an einer Custom-Stelle liegt.

2. **First-Frame-Extract braucht nur 96 ms** für 4-MB-Sintel-MP4. Im Multi-Channel-Queue-Worker-Pool (PLAN Kap. 13) sind FFmpeg-Jobs damit nicht der Bottleneck — eher die Disk-I/O bei großen Source-Files.

3. **Process.Kill mit `entireProcessTree:true`** ist wichtig — FFmpeg startet sich theoretisch keine Child-Prozesse, aber Filter-Plugins oder hwaccel-Layers können das. Der Mehraufwand für `entireProcessTree:true` ist gering, die Robustheit dafür hoch.

4. **`ProcessStartInfo.RedirectStandardError`** ist Pflicht — FFmpeg loggt **alles** auf stderr, auch normale Status-Meldungen. Wir capturen nur die letzten 8 Zeilen für Debug-Output, sonst würde der Buffer bei langen Operations vollaufen.

5. **`proc.EnableRaisingEvents = true` + `BeginErrorReadLine()`** sind nötig, damit der StdErr-Reader asynchron läuft und der Process nicht blockiert. Klassischer Process-API-Stolperstein.

6. **`-loglevel error`** dämpft FFmpegs Logorrhoe, damit das StdErr-Tail im Erfolgsfall leer ist. Im Fehlerfall sind die FFmpeg-eigenen Fehlermeldungen sichtbar (siehe Test 3 + 4 Output).

7. **`-ss 1 -i input -frames:v 1`** ist ein bewährtes Pattern für schnelle Single-Frame-Extracts. Bei langen Videos kann man auch `-ss 30%` (relativ) verwenden oder einen IDR-Frame-Picker-Filter, aber für V0.1-Thumbnails ist 1-Sekunden-Offset OK.

## Reproduzierbarkeit

Voraussetzung: FFmpeg im PATH (z.B. `winget install Gyan.FFmpeg`) und Sintel-Sample aus Spike #3.

```powershell
cd "D:\Code\Lumora\repo"
dotnet run --project tests\Lumora.PerfTests -c Release -- spike05-ffmpeg-cancel
```

Optional:
- `--input PATH` für eigenes Test-MP4 (Default ist Sintel-Sample aus Spike #3)

## Code

- [tests/Lumora.PerfTests/Spikes/Ffmpeg/FfmpegProcessRunner.cs](../../tests/Lumora.PerfTests/Spikes/Ffmpeg/FfmpegProcessRunner.cs)
- [tests/Lumora.PerfTests/Spikes/Ffmpeg/Spike05FfmpegBenchmark.cs](../../tests/Lumora.PerfTests/Spikes/Ffmpeg/Spike05FfmpegBenchmark.cs)

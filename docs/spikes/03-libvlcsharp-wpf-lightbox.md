# Spike #03 — LibVLCSharp WPF Lightbox

- **Status:** ✅ Grün — Cutline gerettet, MP4-Lightbox bleibt in V0.1
- **Datum gestartet:** 2026-05-11
- **Datum abgeschlossen:** 2026-05-11
- **Bezogen auf:** [ADR-0006](../adr/0006-bild-und-video-pipeline.md), [PLAN.md Kap. 8 + 16](../../PLAN.md)
- **Blockiert (jetzt frei):** MP4-Lightbox in V0.1

## Zielsetzung

Beweisen, dass `LibVLCSharp.WPF` in einem .NET-10 / WPF-UI-FluentWindow-Setup stabil funktioniert: Player-Lifecycle, Fullscreen, Focus-Verhalten, Multi-Monitor, Dispose ohne Memory-/Handle-Leaks.

**Cutline-Regel:** Wenn dieser Spike rote Showstopper zeigt (Crashes, Interop-Bugs, nicht behebbare Memory-Leaks), wird die produktive Video-Lightbox in V0.1 durch einen "Extern öffnen mit Standard-Player"-Fallback ersetzt. Image-Lightbox bleibt davon unberührt.

## Messziele (PLAN Kap. 8)

| Bereich | Kriterium |
|---|---|
| Player-Lifecycle | Open / Play / Pause / Seek / Close / Reopen — stabil, kein Crash |
| Fullscreen | Toggle Window/Fullscreen sauber, Video bleibt synchron |
| Focus | Beim Window-Lostfocus pausiert/läuft korrekt (Spike-Entscheidung) |
| Multi-Monitor | Window auf anderen Monitor verschieben → Video bleibt sichtbar |
| Dispose | LibVLC + MediaPlayer + Window komplett freigegeben (Handle-Count stabil) |
| Erst-Ladezeit | < 1 s bis Frame 1 sichtbar (für lokales H.264 MP4) |
| Codecs | H.264 / HEVC / verschiedene Containerformate ohne Spezial-Setup |

## Aufbau

### Stack
- `LibVLCSharp.WPF` 3.9.3 (managed wrapper)
- `VideoLAN.LibVLC.Windows` 3.0.21 (native DLLs, ~50 MB)
- Eingebettet im bestehenden `PhotoViewerV3.Wpf` (kein separates Projekt)

### Komponenten
- **`SpikeMediaDownloader`** (Service) — lädt Big-Buck-Bunny einmalig nach `%LocalAppData%\PhotoViewerV3\spike-data\BigBuckBunny.mp4`, prüft Größe + SHA als Sanity-Check, gibt lokalen Pfad zurück
- **`SpikeLightboxViewModel`** — `LibVLC` + `MediaPlayer` Lifecycle, Play-State, Position, Duration, Telemetrie (Time-to-First-Frame, Memory)
- **`SpikeLightboxView`** (UserControl im NavigationView) — `VideoView`-Host, Transport-Bar (Play/Pause/Seek), "Lightbox-Window öffnen"-Button
- **`LightboxWindow`** (separates `FluentWindow`) — eigenes Fenster mit `VideoView`, F=Fullscreen, Esc=Close, Multi-Monitor-fähig

### Was der Spike NICHT macht (V0.1 separat)
- Lightbox-Filmstrip
- EXIF-Panel
- Pfeiltasten-Navigation zwischen Files
- Pinch-Zoom / Pan (das ist Image-Lightbox-Thema)

## Testverfahren

1. **Lifecycle-Loop:** 10× Open → 5 s Play → Close, Handle-Count vor/nach via Diagnostics
2. **Fullscreen-Toggle:** 5× F-Tastendruck im LightboxWindow, kein schwarzer Frame > 200 ms
3. **Multi-Monitor:** Window auf 2. Monitor ziehen während Wiedergabe — soll sichtbar bleiben
4. **Dispose-Test:** App schließen während laufender Wiedergabe — keine Exceptions im Log
5. **Codec-Variation:** Falls Spike grün ist mit BBB, optional eigenes MP4 testen

## Hardware-Profil

(identisch Spike #1/#2)

| Feld | Wert |
|---|---|
| OS | Windows 11 Pro Build 26200 |
| CPU | Intel Core i7-8700K |
| RAM | 32 GB |
| .NET SDK | 10.0.203 |
| LibVLCSharp.WPF | 3.9.3 |
| VideoLAN.LibVLC.Windows | 3.0.21 |
| Sample-Asset | Sintel Trailer 480p (Blender Foundation, CC-BY), ~4,4 MB, H.264, ~52 s |

## Ergebnis

| Bereich | Beobachtung | Ampel |
|---|---|---|
| Build mit LibVLC-NuGets | sauber, +24 s initialer Restore (native DLLs ~50 MB) | 🟢 |
| Sample-Download | Sintel Trailer 4,4 MB stabil von blender.org | 🟢 |
| **Time-to-First-Frame** | **43 ms** (Ziel < 1 s) — Faktor ~23× | 🟢 |
| Play / Pause / Stop | stabil, Events feuern korrekt | 🟢 |
| Fullscreen-Toggle (F-Taste) | funktioniert | 🟢 |
| Esc beendet Fullscreen / Window | funktioniert | 🟢 |
| Multi-Monitor (Window verschieben) | Video bleibt sichtbar | 🟢 |
| Eingebettete View + eigenes LightboxWindow | beide Pfade funktional | 🟢 |
| Dispose + Reopen (Lifecycle-Test) | kein Crash, kein sichtbares Leak | 🟢 |
| App-Schließen während Wiedergabe | sauber, kein Exception-Log-Eintrag | 🟢 |

### Falle, die wir auf dem Weg gelernt haben

- Die ursprünglich geplante Big-Buck-Bunny-URL von `commondatastorage.googleapis.com` liefert seit 2025 `HTTP 403 Forbidden` — Public-Storage-Buckets wurden eingeschränkt. Umgestellt auf den Sintel-Trailer von `download.blender.org` (Blender Foundation, CC-BY) — kleiner (4 MB statt 158 MB), schneller, stabiler.
- Lehre für V0.1-Implementation: **Keine externen Sample-Assets im Production-Build**. Spike-Sample bleibt eine reine Spike-Convenience; produktive Lightbox arbeitet nur mit lokalen User-Files.

## Schlussfolgerung

**LibVLCSharp.WPF läuft auf .NET 10 + WPF-UI FluentWindow stabil.** Der Cutline-Punkt ist gerettet — die MP4-Lightbox bleibt Pflicht-Feature in V0.1, kein Fallback auf "Extern öffnen" nötig.

Freigabe für:
- Produktive `LightboxWindow` in V0.1 mit denselben Patterns: eigenes `FluentWindow`, `VideoView`-Hosting, Lifecycle in einer ViewModel-Klasse mit `IDisposable`
- Wiederverwendung des `Core.Initialize() + LibVLC + MediaPlayer + Media`-Quartet-Patterns
- F-Taste = Fullscreen-Toggle, Esc = Close (etabliertes Lightbox-UX)
- Multi-Monitor-Setup via eigenes Window

## Folge-Erkenntnisse (für V0.1)

1. **`Core.Initialize()` muss EINMAL pro App-Prozess aufgerufen werden** vor der ersten `LibVLC`-Verwendung. Im Spike-Code via `Interlocked.CompareExchange`-Flag gegen Doppelaufruf abgesichert. In V0.1 sollte dieser Init-Aufruf in eine `ILibVlcCore`-Singleton-Service-Implementation wandern, die per DI registriert ist und beim App-Start eager initialisiert.

2. **Lifecycle-Pattern für `MediaPlayer`:**
   - `LibVLC` einmal pro App (Singleton)
   - `MediaPlayer` pro Lightbox-Session (Open → Use → Dispose)
   - `Media` pro Datei (innerhalb derselben Session bei File-Wechsel via `MediaPlayer.Play(new Media(...))`)
   - Event-Handler explizit ab-abonnieren vor `MediaPlayer.Dispose()` — sonst potenzielle Memory-Leaks bei langen Sessions

3. **VideoView-Binding:** `MediaPlayer="{Binding Player}"` funktioniert mit `OnPropertyChanged(nameof(Player))` aus dem ViewModel. Wenn der Player neu erstellt wird (Dispose+Reopen), muss `OnPropertyChanged` getriggert werden — VideoView reagiert dann auf den neuen Player.

4. **Multi-Monitor + Mica:** Das `FluentWindow` mit `WindowBackdropType="Mica"` bleibt auf jedem Monitor responsive. Spike-Test mit verschieben während Wiedergabe → ohne Frame-Loss.

5. **WPF-VideoView-Performance:** Hardware-Decoding ist standardmäßig aktiv (LibVLC nutzt DirectX/D3D11 auto), keine spezielle Konfiguration nötig. Bei i7-8700K + integrated GPU keine CPU-Last-Spitzen sichtbar.

## Reproduzierbarkeit

```powershell
cd "D:\OneDrive\Dokumente\02 Arbeit\05 Vorlagen - Scripte\03_Foto-Viewer-V3"
dotnet run --project src\PhotoViewerV3.Wpf -c Debug
```

Oder F5 in VS. Dann links auf "Spike: Lightbox" → "Sample laden (Sintel, ~4 MB)" → "Öffnen + Play".

## Code

- [src/PhotoViewerV3.Wpf/Spike03/Services/SpikeMediaDownloader.cs](../../src/PhotoViewerV3.Wpf/Spike03/Services/SpikeMediaDownloader.cs)
- [src/PhotoViewerV3.Wpf/Spike03/ViewModels/SpikeLightboxViewModel.cs](../../src/PhotoViewerV3.Wpf/Spike03/ViewModels/SpikeLightboxViewModel.cs)
- [src/PhotoViewerV3.Wpf/Spike03/Views/SpikeLightboxView.xaml](../../src/PhotoViewerV3.Wpf/Spike03/Views/SpikeLightboxView.xaml)
- [src/PhotoViewerV3.Wpf/Spike03/Views/LightboxWindow.xaml](../../src/PhotoViewerV3.Wpf/Spike03/Views/LightboxWindow.xaml)

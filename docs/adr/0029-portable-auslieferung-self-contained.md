# ADR-0029: Portable Auslieferung als self-contained Ordner (win-x64) mit Version aus dem CHANGELOG

- **Status:** Accepted
- **Datum:** 2026-10-20
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** [ADR-0001](0001-runtime-and-ui-stack.md) (Runtime- und UI-Stack), [ADR-0003](0003-catalog-modell.md) (zentrale DB unter `%LocalAppData%`), [ADR-0026](0026-speicherort-katalog-waehlbar.md) (Speicherort wählbar)

## Kontext

PLAN.md Kap. 1 sah von Anfang an einen „portablen Ordner via `dotnet publish`“ vor; bis 0.2.20 lief die App nur als
lokaler Debug-Build. Für die Ablösung von V2 braucht es ein Paket, das ohne Repo und ohne Entwicklungswerkzeuge auf
einem Windows-Rechner startet. Die App bringt native Abhängigkeiten mit: SQLite (`e_sqlite3.dll` aus SQLitePCLRaw)
und LibVLC (`libvlc\win-x64\` mit Plugins, Lua-Skripten und HRTFs, 422 Dateien); FFmpeg wird nur gesucht (PATH, winget-Links), nicht mitgeliefert.
Die App-Version stand fest als `0.2.0` in `PhotoViewerV3.Wpf.csproj`, während der CHANGELOG längst bei 0.2.2x war.

## Entscheidung

`build/publish.ps1` erzeugt per `dotnet publish` ein **self-contained** Paket für `win-x64` als **Ordner + ZIP** unter
`artifacts/` — **ohne Single-File, ohne Trimming, ohne ReadyToRun**. Die Version kommt aus dem obersten
CHANGELOG-Eintrag; `PhotoViewerV3.Wpf.csproj` liest sie beim Build aus derselben Quelle, das Skript prüft die
Produktversion der veröffentlichten EXE.

## Begründung

- **Self-contained** statt framework-dependent: Zielrechner brauchen keine installierte „.NET 10 Desktop Runtime“;
  Größe ist bei einer Foto-App zweitrangig. Framework-dependent bleibt als Schalter (`-FrameworkDependent`).
- **Kein Single-File:** Probe-Publish (`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`): eine 248-MB-EXE,
  daneben bleibt trotzdem ein Ordner `libvlc\win-x64` mit 97 von 422 Dateien (`lua\`, `hrtfs\`), während
  `libvlc.dll` und die Plugin-DLLs in die EXE wandern und beim Start in ein Temp-Verzeichnis entpackt würden — LibVLC
  sucht Plugins und Lua-Skripte aber relativ zu `libvlc.dll`. Dazu Warnung IL3000: `Assembly.Location` ist im
  Single-File leer (Einstellungen → „Über“ liest das Build-Datum daraus). Das Paket bliebe ein Ordner, nur fragiler.
- **Kein Trimming:** WPF, XAML-Bindings, EF Core und CommunityToolkit.Mvvm arbeiten mit Reflection; Trimming wird von
  WPF nicht unterstützt.
- **Version aus dem CHANGELOG:** Der CHANGELOG ist laut Commit-Profil ohnehin die Versionsquelle und wird bei jedem
  Commit gepflegt; eine zweite, von Hand nachgezogene Versionsnummer veraltet (0.2.0 vs. 0.2.20).
- **Daten bleiben unter `%LocalAppData%`** (ADR-0003/0026): Das Paket enthält keine Konfiguration; Formatregister sind
  Code. Ein neues Paket übernimmt Katalog und Vorschaubilder, der Paketordner muss nicht beschreibbar sein.

## Konsequenzen

### Positiv
- Ein Befehl (`pwsh -File build\publish.ps1`) liefert ein startfähiges Paket; `build/run-smoke.ps1 -Exe …` prüft es
  mit dem Integrations-Rauchtest.
- Versionsanzeige, Dateiversion und Paketname stimmen immer mit dem CHANGELOG überein.

### Negativ
- Paket 0.2.26: ~257 MB entpackt (780 Dateien), ~114 MB als ZIP — davon ~101 MB LibVLC, ~150 MB .NET/WPF-Runtime.
- FFmpeg muss weiter separat installiert werden (`winget install Gyan.FFmpeg`), sonst gibt es keine Video-Vorschaubilder.

### Neutral
- Das LibVLC-Paket kopiert bei AnyCPU auch `libvlc\win-x86`; das Skript schaltet das ab (`VlcWindowsX86Enabled=false`).
- Nur deutsche Satelliten-Ressourcen (`SatelliteResourceLanguages=de`).
- `BuildHost-net472/-netcore` (~4 MB, aus `Microsoft.EntityFrameworkCore.Design` über das Data-Projekt) landen mit im
  Paket, werden zur Laufzeit aber nicht gebraucht.
- Kein Code-Signing, kein Installer, kein Auto-Update.

## Alternativen

- **Framework-dependent als Standard:** 124,8 MB / 529 Dateien statt 256,6 MB / 780 Dateien → verworfen, weil die
  „.NET 10 Desktop Runtime“ auf dem Zielrechner fehlen kann; bleibt als Option (`-FrameworkDependent`).
- **Single-File:** siehe Begründung → verworfen.
- **MSIX/Installer:** braucht Signatur und Installation, widerspricht „portabel“ → verworfen (für später offen).
- **FFmpeg ins Paket legen:** ~100 MB mehr, eigene Lizenzpflichten (GPL/LGPL-Build), und der Finder sucht heute nicht
  neben der EXE → verworfen, offen für später.
- **Version in `Directory.Build.props` pflegen:** zweite Quelle neben dem CHANGELOG → verworfen.

## Referenzen

- `build/publish.ps1`, `build/run-smoke.ps1`, `src/PhotoViewerV3.Wpf/PhotoViewerV3.Wpf.csproj` (`Version` aus CHANGELOG)
- README.md, Abschnitt „Portable Version bauen“; CHANGELOG 0.2.26

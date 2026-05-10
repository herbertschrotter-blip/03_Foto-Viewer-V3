# ADR-0001: Runtime und UI-Stack

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter (User), Claude (Empfehlung), GPT-5 (Review)

## Kontext

PhotoViewer V3 wird als native Windows-Desktop-Anwendung neu gebaut (Ablösung der PowerShell-HTTP-Server-Notlösung). Constraints: Windows-only, Single-User, lokal, 50.000–500.000 Dateien, dateisystemnah, komplexes Grid + Drag & Drop + Multi-Window-Lightbox + Media-Interop. Welcher .NET-UI-Stack passt?

## Entscheidung

**.NET 10 (LTS) + WPF + WPF-UI (lepoco) v4 als isolierter UI-Layer + CommunityToolkit.Mvvm 8.4** für MVVM und Source-Generators.

## Begründung

- **WPF** ist seit 2006 mature, läuft seit .NET Core nativ auf modernem .NET, beherrscht komplexe virtualisierte Layouts, Drag & Drop, Keyboard-Shortcuts und Native-Interop (LibVLC, FFmpeg) besser als jeder neuere Stack.
- **.NET 10** ist LTS (Support bis November 2028), C# 14, weiterhin gepflegt für WPF.
- **WPF-UI (lepoco)** liefert Fluent-/WinUI 3-Look (Mica, Segoe Fluent Icons, NavigationView) ohne harte Abhängigkeit von WinUI 3. Wird über Interfaces isoliert, ViewModels kennen es nicht direkt → austauschbar.
- **CommunityToolkit.Mvvm** ist Microsoft-offiziell, leicht, Source-Generators für `ObservableProperty`/`RelayCommand`.

## Konsequenzen

### Positiv
- Mature Ecosystem mit langjährigen Lösungen für virtualisiertes Grid, Drag & Drop, Multi-Window
- LTS-Support bis 2028
- WPF-UI gibt Windows-11-Look ohne WinUI-3-Migrations-Risiko
- Source-Generators reduzieren Boilerplate

### Negativ
- WPF ist nicht "neueste Technologie" (kein Reason to Care für Außenstehende)
- Windows-only (akzeptiert)

### Neutral
- WPF-UI wird über Service-Interfaces isoliert (`IUserNotificationService` etc.), damit es theoretisch austauschbar bleibt

## Alternativen

- **WinUI 3** → verworfen weil: kleineres Ecosystem, Drag & Drop und virtualisiertes Grid bei 500k weniger ausgereift, Media-Interop schwieriger. Keine Vorteile außer "moderner Look".
- **Avalonia** → verworfen weil: Cross-Platform-Ziel besteht nicht. Native Media-/Shell-Integration wäre Abstraktionsverlust.
- **MAUI** → verworfen weil: für mobile/cross-platform optimiert, für schwere Windows-Desktop-Medienverwaltung sperrig.
- **WinForms** → verworfen weil: zu alt für komplexes Grid und modernes Theme.

## Referenzen

- [chatgpt-review/runde-1-chatgpt-response.md](../../chatgpt-review/runde-1-chatgpt-response.md)
- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md)

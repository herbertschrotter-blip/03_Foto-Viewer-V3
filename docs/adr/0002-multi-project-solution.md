# ADR-0002: Multi-Project Solution ab Tag 1

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5

## Kontext

Scope für V0.1 ist groß (8–10 Wochen, ~70–110 PT): SQLite + EF Core + Raw SQL + FTS5 + Multi-Channel Thumbnail-Queue + WIC + FFmpeg + LibVLC + WPF + adaptive Worker. Single-Project oder Multi-Project Solution?

## Entscheidung

**Multi-Project Solution ab Tag 1**: `PhotoViewerV3.Domain`, `PhotoViewerV3.Data`, `PhotoViewerV3.Services`, `PhotoViewerV3.Wpf`, `PhotoViewerV3.Tools` (optional), Tests separat.

## Begründung

- Bei diesem Scope wird Single-Project schnell unübersichtlich (Raw SQLite + EF + WPF + LibVLC + Workers in einem Projekt).
- Spike-Tests brauchen Domain/Data/Services ohne WPF-Start (Console-Harness, BenchmarkDotNet).
- V0.1 ist groß genug, dass spätere Trennung schmerzhafter wäre als Setup-Aufwand am Anfang.
- Saubere Abhängigkeitsregeln: Domain → keine, Data → Domain, Services → Domain+Data, Wpf → alle.

## Konsequenzen

### Positiv
- Klare Schichtentrennung erzwungen
- Tests einfacher gegen Domain/Services
- Spike-Console-Apps gegen Services möglich ohne WPF
- Späterer Refactor billiger
- Domain bleibt frei von WPF/EF-Referenzen

### Negativ
- Mehr Setup-Aufwand am Anfang (1 PT vs. 0,5 PT)
- 5 Projekte statt 1 in der Solution

### Neutral
- Central Package Management via `Directory.Packages.props` empfohlen (gemeinsame NuGet-Versionen)

## Alternativen

- **Single-Project** → verworfen weil: bei 70–110 PT Scope wird's unübersichtlich, später Refactor-Schmerz größer als Setup-Aufwand jetzt.
- **Clean Architecture mit noch mehr Projekten** (Application/Infrastructure separat) → verworfen weil: für Solo-Projekt Overkill.

## Referenzen

- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) Kapitel "Solution Layout"

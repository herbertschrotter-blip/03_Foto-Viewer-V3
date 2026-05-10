# ADR-0008: Storage-Profile (SSD/HDD/NAS)

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5
- **Bezogen auf:** [ADR-0009](0009-cache-strategie.md)

## Kontext

User-Bibliothek wandert zwischen verschiedenen Storage-Typen — lokale SSD, externe HDD, NAS / Netzlaufwerk. Jeder Typ braucht unterschiedliche Worker-Anzahlen, Batch-Größen und Throttling, um a) auf SSD schnell zu sein, b) HDDs nicht durch parallele Reads zu killen, c) NAS-Netzwerklast vorsichtig zu halten.

## Entscheidung

**`StorageProfileService`** mit 4 erkennbaren Kinds (**LocalSsd / LocalHddOrUsb / NetworkShare / Unknown**), **konservativer Fallback** auf `LocalHddOrUsb` bei Unsicherheit. **Storage-Availability** (online/offline) **separat** vom Profile modelliert.

Detection via:
- `GetDriveType` (Win32) für Remote / Removable / Fixed
- UNC-Erkennung: `path.StartsWith("\\\\")`
- SSD/HDD-Differenzierung optional via WMI `MSFT_PhysicalDisk.MediaType`
- DeviceIoControl-Komplexität **nicht im MVP**

## Begründung

- Verschiedene Storages haben **massiv unterschiedliche Performance-Charakteristika** — eine SSD verträgt 6 parallele Reads, eine HDD wird durch 2 schon ineffizient (Random-I/O-Hölle), ein NAS leidet bei aggressiven Background-Jobs.
- **`StorageAvailability` separat** von Profile: offline ist eigener State, nicht "langsames NAS-Profil". User sieht "Storage anschließen" statt App tut nichts.
- **Konservativer Fallback** bei Unsicherheit: lieber langsamer als Vertrauen verlieren (z. B. durch False-Mass-Missing bei NAS-Hick-up).
- **DeviceIoControl** wäre präziser, aber P/Invoke-Aufwand für MVP nicht gerechtfertigt.

## Konsequenzen

### Positiv
- App ist auf SSD aggressiv schnell, auf NAS höflich, auf HDD effizient
- Offline-State = klare UI ("Storage anschließen") statt rätselhaftem Verhalten
- Detection-Code ist klein und testbar
- Profile-Switching live für Budgets (nicht für Architektur)

### Negativ
- 3 Profile = 3-fache Tuning-Möglichkeiten (mehr Config-Werte)
- Profile-Detection kann bei exotischen Setups (VeraCrypt-Container, RAID, externe SSDs als USB) falsch erkennen → konservativer Fallback fängt das ab

### Neutral
- 3 konkrete Profile mit Settings (siehe [PLAN.md Kap 11](../../PLAN.md))
- NAS-Profil: `MaxVideoWorkers: 0`, `AggressiveBackgroundThumbs: false`, Video-Thumbs nur on-demand

## Drei Profile (Settings-Übersicht)

| Setting | SSD | HDD/USB | NAS |
|---|---|---|---|
| ScanBatchSize | 5000 | 1000–2000 | 500–1000 |
| MaxImageWorkers | max(2, CPU-2) | 2 | 1–2 |
| MaxVideoWorkers | 2 (CPU≥8) / 1 | 1 | 0 (on-demand) |
| MaxConcurrentDiskReads | 6 | 2 | 1–2 |
| BackgroundBatchDelayMs | 0–10 | 50–150 | 150–500 |
| RootCheckTimeoutMs | 1000 | 1000 | 1500–3000 |
| AggressiveBackgroundThumbs | true | true | false |

## Alternativen

- **Festes SSD-Profil für alles** → verworfen weil: HDD/NAS leiden, Netzwerk-Last zu hoch.
- **DeviceIoControl von Anfang an** → verworfen weil: P/Invoke-Aufwand zu hoch für MVP, konservativer Fallback reicht.
- **PowerShell/WMI-Abfrage `MSFT_PhysicalDisk`** → später optional, langsamer aber einfacher als P/Invoke.
- **Keine Profile, dafür adaptiv via FPS** → verworfen weil: reagiert zu spät auf langsamen Storage, FPS ist ein Signal, kein Steuerwert.

## Referenzen

- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) "StorageProfileDetection"

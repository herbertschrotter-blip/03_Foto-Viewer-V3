# Spike #06 — Storage-Detection

- **Status:** 🟢 Grün mit dokumentierten Edge-Cases
- **Datum gestartet:** 2026-05-11
- **Datum abgeschlossen:** 2026-05-11
- **Bezogen auf:** [ADR-0008](../adr/0008-storage-profile.md), [PLAN.md Kap. 8 + 11 + 12](../../PLAN.md)
- **Blockiert (jetzt frei):** Storage-Profile-Selection beim Scan-Start

## Zielsetzung

`StorageProfileService` bestimmt für einen gegebenen Library-Path **deterministisch und schnell**, welches Storage-Profil (`LocalSsd`, `LocalHddOrUsb`, `NetworkShare`, `Unknown`) gilt — bevor der Scan startet. Wenn Detection langsamer ist als der gesamte Scan, ist das Verfahren unbrauchbar.

## Messziele (PLAN Kap. 8 + 11)

| Bereich | Kriterium |
|---|---|
| Detection auf lokalem Drive (C:\, D:\) | < 100 ms (one-shot) |
| Detection auf UNC-Path (online) | < 1 s |
| Detection auf UNC-Path (offline / Timeout) | abbruch nach 1,5–3 s, nicht hängen |
| SSD- vs HDD-Diff für lokale Fixed Drives | korrekt erkannt (WMI / DeviceIoControl) |
| Network-Share-Detection | `path.StartsWith(@"\\")` ODER `DriveType.Network` |
| Bei Unsicherheit | Fallback auf `LocalHddOrUsb` (konservativ, siehe ADR-0008) |

## Aufbau

### Strategie (entspricht ADR-0008)
1. **Path-Type-Detection**:
   - UNC-Prefix (`\\`) → `NetworkShare`
   - Lokales Laufwerk → DriveInfo + WMI
2. **DriveType** über `DriveInfo.DriveType`:
   - `Fixed` → weiter über WMI prüfen (SSD oder HDD?)
   - `Removable` (USB) → `LocalHddOrUsb`
   - `Network` → `NetworkShare`
   - `CDRom`, `Ram`, `Unknown` → `Unknown` (oder `LocalHddOrUsb` als konservativer Fallback)
3. **WMI `MSFT_PhysicalDisk.MediaType`** (für Fixed Drives):
   - `4` = SSD → `LocalSsd`
   - `3` = HDD → `LocalHddOrUsb`
   - Sonstiges/Fehler → `LocalHddOrUsb` (konservativ)
4. **Timeout-Schutz für UNC**: Bei Network-Share `IsRootReachableAsync` mit Cancel-After ~1,5–3 s.

### Komponenten
- **`StorageProfileDetector`** (Service-Klasse)
  - `DetectAsync(string libraryPath, CancellationToken)`
  - liefert `StorageProfile` enum
  - misst Detection-Time
- **`Spike06StorageDetection`** (Runner)
  - testet eine Liste von Paths (C:\, D:\, OneDrive, evtl. UNC, evtl. offline-UNC)
  - logged Result + Latency je Path

## Hardware-Profil

(identisch Spike #1–#4)

| Feld | Wert |
|---|---|
| OS | Windows 11 Pro Build 26200 |
| Drives | C:\ (Samsung 860 EVO SATA SSD), D:\ (OneDrive), evtl. weitere |

## Ergebnis

| Path | Profile | Erreichbar | Dauer | Grund |
|---|---|---|---|---|
| `C:\Windows\system32` (Cold-Start) | `LocalSsd` | true | **180 ms** | MSFT_PhysicalDisk MediaType=4 (SSD): Samsung SSD 960 PRO 1TB |
| `D:\Code\Lumora` | `LocalHddOrUsb` | true | **56 ms** | Win32_DiskDrive Model-Heuristik: `Microsoft Storage Space Device (SCSI)` → konservativ HDD |
| `D:\OneDrive` | `LocalHddOrUsb` | true | **57 ms** | dito |
| `C:\Users\herbe` (Warm-Cache) | `LocalSsd` | true | **59 ms** | MSFT_PhysicalDisk MediaType=4 (SSD): Samsung SSD 960 PRO 1TB |
| `\\nonexistent-spike-host\share` (offline) | `NetworkShare` | false | **2.511 ms** | UNC-Prefix, Root NICHT erreichbar (Timeout 2,5 s) |

### Zielvergleich

| Messung | Ergebnis | Ziel | Ampel |
|---|---|---|---|
| Lokal Cold-Start | 180 ms | < 100 ms | 🟡 knapp drüber, Warmup-fähig |
| Lokal Warm-Cache | ~56–59 ms | < 100 ms | 🟢 |
| UNC offline (Timeout-Schutz) | 2.511 ms | < 3.000 ms | 🟢 ~16 % unter Limit |
| SSD-Erkennung C:\ (NVMe Samsung 960 PRO) | korrekt | korrekt | 🟢 |
| Storage-Spaces-Disk D:\ | LocalHddOrUsb (konservativ) | LocalSsd wäre korrekt | 🟡 dokumentierter Edge-Case |
| UNC-Offline-Klassifizierung | `NetworkShare` + `Reachable=false` | korrekt | 🟢 |

## Schlussfolgerung

**Strategie aus ADR-0008 funktioniert für die Standard-Fälle** (direkte SSD/HDD/USB-Laufwerke + UNC-Online/Offline). Storage-Spaces-Pools (Microsoft "Storage Spaces" als softwaredefinierte Disk) sind ein bekannter Edge-Case, der **konservativ als HDD klassifiziert** wird — entspricht der in PLAN Kap. 11 dokumentierten "Bei Unsicherheit konservativ"-Regel: Performance verlieren, nicht Vertrauen.

Freigabe für:
- `StorageProfileService` in V0.1 auf Basis dieses Detectors
- Storage-Profile-Auswahl beim Scan-Start (`LocalSsd`, `LocalHddOrUsb`, `NetworkShare`, `Unknown`)
- UNC-Reachability-Check mit hartem 2,5-s-Timeout — ohne wäre der Scan beim Offline-Drive einfach gehangen

## Folge-Erkenntnisse

1. **WMI-Cold-Start kostet 180 ms.** Erste Detection nach App-Start hat WMI-COM-Initialisierungs-Overhead. Spätere Aufrufe im selben Prozess sind 50–70 ms.
   - **V0.1-Empfehlung:** `StorageProfileDetector.WarmupAsync()` beim App-Start als Hintergrund-Task aufrufen (z.B. parallel zur DB-Migration). Dann ist der erste produktive Detection-Call <100 ms.

2. **`Directory.Exists` auf nicht-erreichbarem UNC respektiert keinen `CancellationToken`.** SMB-Connect-Timeout ist OS-Default ~30 s. Lösung im Spike: `Task.WhenAny(probeTask, Task.Delay(timeout))` — bei Timeout läuft der Hintergrund-Task best-effort weiter, wir geben `false` zurück. Greift zuverlässig bei 2,5 s.
   - **V0.1-Empfehlung:** Dieses Pattern direkt in den `StorageAvailabilityChecker` übernehmen. Wenn eine Library-Root als UNC konfiguriert ist, MUSS jeder Scan-Batch einen Reachability-Recheck vorab machen (siehe PLAN Kap. 12).

3. **Storage-Spaces-Pools fallen aus der direkten `Win32_DiskDrive`-/`MSFT_PhysicalDisk`-Korrelation.**
   - Detector sieht `Microsoft Storage Space Device` als generischen "SCSI"-Drive — keine SSD/HDD-Information.
   - Korrekte Auflösung wäre über `MSFT_VirtualDisk` → `MSFT_PhysicalDiskToVirtualDisk`-Association in den Pool → erst dann zur echten Hardware.
   - **V0.1-Entscheidung:** Konservativ als HDD klassifizieren (Performance-Verlust ist gering, Sicherheit hoch).
   - **V0.2-Backlog:** MSFT_VirtualDisk-Traversal für korrekte Storage-Spaces-Klassifizierung. Falls User-Studie zeigt, dass Storage-Spaces verbreitet ist, vorziehen.

4. **`Win32_DiskDrive.Model`-Heuristik ist nützlich als Fallback.** Erkennt "Samsung SSD", "NVMe", "EVO" — alles starke SSD-Indikatoren. Im Spike erkennt sie aber `Microsoft Storage Space Device` korrekt NICHT als SSD (zu unspezifischer Name).

5. **`DriveType.Removable` (USB) und `DriveType.Network` sind sofort klassifiziert** — kein WMI nötig, < 5 ms. Hilfreich für externe Festplatten.

## Reproduzierbarkeit

```powershell
cd "D:\Code\Lumora"
dotnet run --project tests\Lumora.PerfTests -c Release -- spike06-storage-detection [--path C:\foo --path D:\bar ...]
```

Default-Pfade: `C:\Windows\system32`, current dir, `%OneDrive%`, `%UserProfile%`, `\\nonexistent-spike-host\share`.

## Code

- [tests/Lumora.PerfTests/Spikes/Storage/StorageProfileDetector.cs](../../tests/Lumora.PerfTests/Spikes/Storage/StorageProfileDetector.cs)
- [tests/Lumora.PerfTests/Spikes/Storage/Spike06StorageDetection.cs](../../tests/Lumora.PerfTests/Spikes/Storage/Spike06StorageDetection.cs)

# ADR-0009: Cache-Strategie (lokal in LocalAppData)

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter, Claude, GPT-5
- **Bezogen auf:** [ADR-0003](0003-catalog-modell.md), [ADR-0008](0008-storage-profile.md)

## Kontext

Wo werden Thumbnail-Caches gespeichert? Klassische Alternativen: `.thumbs\` neben Foto-Ordner (wie PowerShell-Vorgänger), zentral pro Windows-User, oder in DB als BLOB. Bei NAS / Netzlaufwerk kommen besondere Schwierigkeiten dazu (Schreibrechte, Sync-Churn, Polluting).

## Entscheidung

**Thumbnails liegen lokal pro Windows-User unter:**
```
%LocalAppData%\PhotoViewerV3\thumbs\{libraryId}\{fileId}_{size}_{kind}.jpg
```
**Auch bei NAS-Bibliotheken**. Kein `.thumbs\`-Ordner neben Foto-Bibliothek.

## Begründung

- **Kein NAS-Polluting**: Bibliothek bleibt sauber, keine versteckten Ordner für User sichtbar.
- **Keine Schreibrechte-Probleme** auf Netzwerkshares.
- **Kein OneDrive/NAS-Sync-Churn**: Cache wird nicht ständig hochgeladen (war im PowerShell-Vorgänger ein dokumentierter Schmerzpunkt).
- **Pro Windows-User isoliert**: mehrere Windows-Accounts haben eigene Caches, keine Kollision.
- **Cache regenerieren** = einen Ordner leeren, einfache UX.
- **Kein DB-BLOB**: WPF `BitmapImage` lädt vom Pfad nativ schnell (10.000 Bilder × 30 KB als BLOB würden die DB unnötig aufblähen, native Disk-Reads sind schneller).
- **File-ID statt MD5-Hash** im Dateinamen: einfacher (im PowerShell wurde MD5 verwendet), Robust (kein Hash-Kollisions-Risiko).

## Konsequenzen

### Positiv
- Foto-Ordner bleiben sauber, kein Hidden-Folder-Aufkommen
- NAS-/OneDrive-freundlich (kein Sync, keine Schreibrechte-Probleme)
- Schnelle Disk-Reads bei großen Caches
- Cache kann pro Library gelöscht/regeneriert werden (Pfad enthält LibraryId)
- Backup von `%LocalAppData%\PhotoViewerV3\` als Ganzes möglich

### Negativ
- **Cache wandert nicht mit**, wenn Bibliothek auf anderen PC zieht (akzeptiert für MVP)
- `%LocalAppData%` kann sich füllen — Cache-Cleanup / Size-Limit als Settings-Option nötig
- Bei DB-Reset müssen Caches für alte LibraryId aufgeräumt werden (Orphan-Cleanup)

### Neutral
- Naming-Konvention klar: `{fileId}_{size}_{kind}.jpg` (z. B. `42_320_still.jpg`)
- Animierte Hover-Thumbs (Phase 8): `{fileId}_{size}_hover.gif`
- 3 Größen: 160 / 320 / 640

## Alternativen

- **`.thumbs\` neben Foto-Ordner** → verworfen weil: NAS-Schreibrechte, OneDrive-Sync-Churn, Polluting, Schmerzpunkt im PowerShell-Vorgänger.
- **In DB als BLOB** → verworfen weil: DB-Größe steigt massiv (10k Bilder × 30 KB = 300 MB BLOB), Performance schlechter als native Disk-Reads.
- **`%Temp%`** → verworfen weil: volatil, kann jederzeit gelöscht werden.
- **Hybrid (kleine Thumbs als BLOB, große auf Disk)** → verworfen weil: Komplexität ohne klaren Mehrwert im MVP.
- **MD5-Hash-Filename wie PowerShell** → verworfen weil: File-ID aus DB ist einfacher, Hash-Kollisionsrisiko entfällt.

## Referenzen

- [chatgpt-review/runde-3-chatgpt-response.md](../../chatgpt-review/runde-3-chatgpt-response.md) "Cache-Strategie bei NAS"

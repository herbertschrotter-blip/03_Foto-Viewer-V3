# Changelog

Alle nennenswerten Änderungen an PhotoViewer V3. Format: `[Version] — Datum`, oberster Eintrag = aktuelle Version
(Versionsquelle für das Commit-Profil in `CLAUDE.md`).

## [0.1.6] — 2026-10-18

### Hinzugefügt
- `IMediaFormatRegistry` / `MediaFormatRegistry` (Services/Media): gängige Foto- und Videoformate statt fester jpg/png/mp4-Liste; Scanner klassifiziert darüber.
- Schnittstellen-Verträge für die nächsten Slices: `ILibraryService`, `IFileOperationService`, `ICatalogUpdateService`, Nachrichten-Records in `Services/Messaging` (WeakReferenceMessenger), `ILightboxService` (Wpf).
- `ScanMode.ClearAndRescan` und `ScanMode.Incremental` (Implementierung folgt).
- `IThumbnailService.PendingCount` für die Statusleiste.
- `CLAUDE.md` mit Skill- und Code-Profil, dieses Changelog.

## [0.1.5] — 2026-10-18

### Hinzugefügt
- Thumbnail-Pipeline: `ThumbnailService` mit getrennten Worker-Pools (WIC für Bilder, FFmpeg für Videos), DB-Status-Lifecycle, atomic `.tmp`+Move; echte Vorschaubilder im Gallery-Grid.

## [0.1.4] — 2026-06-30
- Gallery arbeitet auf dem produktiven `LibraryContext` statt der Spike-DB.

## [0.1.3] — 2026-05-11
- Doku: Real-Welt-Scan-Validation (770 Dateien, 17 GB).

## [0.1.2] — 2026-05-11
- Fix: FTS-Count via SELECT; CLI-Test `spike-v01-scan`.

## [0.1.1] — 2026-05-11
- App-Start nutzt `AddPhotoViewerServices` + `DatabaseInitializer`.

## [0.1.0] — 2026-05-11
- Scanner-Slice: `FolderEnumerator` + `BulkScanWriter` + `FtsIndexer`.

## [0.0.8] — 2026-05-11
- Entities, `LibraryContext`, Migration `InitialV01` (V0.1-Schema).

## [0.0.2] – [0.0.7] — 2026-05-11
- Sechs Spikes grün: SQLite-Bulk/FTS5 (500k), Virtual Grid, LibVLCSharp-Lightbox, WIC + File-Locks, FFmpeg-Cancel, Storage-Detection.

## [0.0.1] — 2026-05-10
- Initial: Plan, 15 ADRs, Mockups, Tag-1-Skeleton.

## Offen
- Incremental-Re-Scan mit Offline-vs-Missing-Guard (PLAN Kap. 12).
- Portierung des Pattern-File-Sorters und von Flatten & Move aus V2.
- NuGet-Sicherheitswarnungen (SQLitePCLRaw 2.1.11, System.Security.Cryptography.Xml 10.0.7) per Versions-Bump beheben.

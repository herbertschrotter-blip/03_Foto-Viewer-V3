# ADR-0022: Archive als Katalog-Einträge, Entpacken über SharpCompress

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0016 (Formatregister), ADR-0017 (Nachrichten), ADR-0018 (Datei-Operationen nur über Service), ADR-0019 (Incremental-Scan), PLAN Kap. 1 und 10 (Auto-Extract, SharpCompress)

## Kontext

Foto-Sammlungen kommen oft als ZIP/RAR/7z an (Kamera-Export, Download, Weitergabe). V2 entpackte solche Archive
beim Scan automatisch über 7-Zip/WinRAR als externe Programme, mit einem Runden-System für verschachtelte Archive
(`Lib_ArchiveExtractor.ps1`). V3 ist eine portable App ohne externe Werkzeuge; Archive lagen bisher unsichtbar im
Bibliotheksordner (das Register kannte nur Bild- und Videoendungen), und der Benutzer musste sie außerhalb der App
entpacken und danach scannen. Offen war außerdem, ob ein Archiv ein Katalog-Eintrag ist oder nur ein Scan-Nebenprodukt.

## Entscheidung

1. **Archive sind Katalog-Einträge** mit `MediaType.Archive = 2`. Das Formatregister (`IMediaFormatRegistry`) führt
   `.zip .rar .7z .tar .gz .tgz .bz2 .xz` als dritte Liste; der Scanner nimmt sie wie jede andere Datei auf (Files-Zeile,
   Ordnerkette, FTS). `IsSupported` heißt „im Katalog erfasst“, neu `IsMedia` heißt „Foto oder Video“ — Sortierer und
   Flatten arbeiten mit `IsMedia`, damit Archive dort nicht als Mediendateien gelten.
2. **Keine Thumbnails für Archive:** der `ThumbnailService` reiht sie gar nicht erst ein (keine Thumbnails-Zeile,
   kein Renderer); die Kachel zeigt Icon, Endung und Größe.
3. **Entpacken in der App über `IArchiveService`** (Services/Archive) mit **SharpCompress 0.50.6** (pure C#, ZIP/RAR/7z/TAR
   und GZ/BZ2/XZ-Streams) — ZIP zusätzlich mit System.IO.Compression als Fallback, falls SharpCompress ein ZIP nicht
   lesen kann. Solide RAR und 7z laufen sequenziell über `ExtractAllEntries()` (SharpCompress erlaubt das nur dort),
   alle anderen Eintrag für Eintrag; TAR-Verwaltungseinträge (PAX-Header, `@LongLink`, `pax_global_header`) werden
   übersprungen, falls ein Leser sie als Dateien liefert. Regeln: Standardziel ist ein Unterordner mit dem Archivnamen neben dem Archiv; vorhandene Dateien
   überspringen/überschreiben/umbenennen; nur Medien oder alles; verschachtelte Archive in Runden bis Tiefe 3 (V2-Vorbild),
   die dabei entstandenen Zwischen-Archive werden danach gelöscht (sie sind Produkte dieses Laufs, keine Benutzerdateien);
   Zip-Slip-Schutz (kein Laufwerk, kein `..`, Ziel muss unterhalb des Zielordners liegen, ungültige Zeichen werden ersetzt);
   Dateien entstehen als `.pv3part` und werden erst fertig umbenannt; Abbruch und Fortschritt (Dateien/Bytes).
   Passwortgeschützte Archive werden mit `ArchiveEncryptedException` abgelehnt — kein Passwort-Dialog.
4. **Nach dem Entpacken** stößt der Service den Incremental-Scan der Bibliothek an (`IScanService`, ADR-0019), damit
   die Dateien im Katalog erscheinen, und verschiebt das Archiv auf Wunsch über `IFileOperationService.RecycleAsync`
   in den Papierkorb (protokolliert, ADR-0018) — nur, wenn der Lauf fehlerfrei war.
5. **Oberfläche:** Filter „Archive“, Aktion „Entpacken…“ (Aktionsleiste und Kontextmenü), Entpacken-Dialog
   (`Wpf/Gallery/Archives`, Muster `AppDialogWindow`), Inhaltsansicht in der Lightbox statt Bild (Liste der Einträge,
   „Entpacken…“, „Im Explorer anzeigen“). Die Lightbox fordert das Entpacken über `ExtractArchiveRequestedMessage`
   an; die Galerie öffnet den Dialog (ADR-0017, kein Modulquerbezug).

## Begründung

- Als Katalog-Eintrag ist ein Archiv sichtbar, suchbar, sortierbar und über die bestehenden Datei-Operationen
  (Verschieben, Papierkorb, Undo) handhabbar — ohne eigene Tabelle. Die ursprünglich geplante Tabelle `ExtractedArchives`
  (PLAN Kap. 4) ist damit nicht nötig; das Protokoll des Papierkorbs liegt in `OperationBatches`.
- SharpCompress braucht keine installierten Programme (portable App), liest RAR und 7z und ist im PLAN seit dem ersten
  Entwurf vorgesehen. System.IO.Compression bleibt als zweiter Leser für ZIP, dem häufigsten Format.
- Manuelles Entpacken zuerst: der Benutzer sieht, was im Archiv liegt, und entscheidet über Ziel und Optionen.
  Automatisches Entpacken beim Scan (V2-Verhalten, PLAN „Auto-Extract“) bleibt als Einstellung offen und setzt auf
  demselben Service auf.
- Zip-Slip ist bei fremden Archiven ein echtes Risiko (Pfade wie `..\..\Windows\…`); die Prüfung gehört in den Service,
  nicht in die Oberfläche.

## Konsequenzen

### Positiv
- Archive erscheinen nach dem Scan in der Galerie; Entpacken, Katalog-Update und Papierkorb sind ein Klick.
- Verschachtelte Archive (ZIP im ZIP) werden ohne Zutun aufgelöst, wie in V2.
- Fehler je Eintrag statt Abbruch; passwortgeschützte und beschädigte Archive liefern eine klare Meldung.

### Negativ
- Ein weiteres NuGet-Paket (SharpCompress) im Services-Projekt.
- RAR/7z lassen sich im Test nicht erzeugen — die Tests erzeugen ZIP (inkl. handgebautem verschlüsseltem ZIP), TAR
  und TAR.GZ selbst; RAR (normal, solide, passwortgeschützt, mit verschlüsselten Köpfen) und 7z liegen als Mini-Archive
  unter `tests/PhotoViewerV3.Tests/Archive/TestData/` (erzeugt mit WinRAR bzw. Windows-bsdtar).
- Versionswahl: SharpCompress 1.0.0 ist auf NuGet zurückgezogen (unlisted) und wertete PAX-Header in TAR nicht aus;
  verwendet wird die neueste gelistete Version 0.50.6 (API `ArchiveFactory.OpenArchive`/`ReaderFactory.OpenReader`).
  Ein Test mit 140 Zeichen langem TAR-Namen sichert das ab.
- Archive zählen in „Gesamt“ und in den Ordnerzählern mit; wer nur Fotos sehen will, nutzt den Filter.

### Neutral
- `MediaType` ist eine DB-Spalte ohne Enum-Constraint; der Wert 2 braucht keine Migration.
- Bestehende Kataloge nehmen Archive beim nächsten „Aktualisieren“ (Incremental) auf.
- Gzip-Einzeldateien (`bild.jpg.gz`) landen in einem Unterordner `bild.jpg\bild.jpg` — selten, deshalb keine Sonderregel.

## Alternativen

- **7-Zip/WinRAR als externe Prozesse (V2):** verworfen — portable App ohne Installationsvoraussetzung; Fehler- und
  Fortschrittsmeldung über Konsolenausgaben ist fragil.
- **Nur System.IO.Compression:** verworfen — kein RAR/7z, und die liegen in der Praxis am häufigsten neben ZIP.
- **Archive beim Scan automatisch entpacken:** zurückgestellt — zuerst der sichtbare, manuelle Weg; die Einstellung
  „Archive: automatisch entpacken beim Scan“ (Standard aus) kann auf `IArchiveService` aufsetzen.
- **Eigene Tabelle `ExtractedArchives`:** verworfen — Archiv ist Files-Zeile, Papierkorb ist OperationBatch; mehr
  Protokoll braucht niemand.

## Referenzen

- `src/PhotoViewerV3.Services/Archive/` (IArchiveService, ArchiveService, Modelle, Nachricht),
  `src/PhotoViewerV3.Wpf/Gallery/Archives/` (Entpacken-Dialog), `src/PhotoViewerV3.Wpf/Lightbox/ArchiveContentView.xaml`
- Tests: `tests/PhotoViewerV3.Tests/Archive/`, `tests/PhotoViewerV3.Wpf.Tests/Archive/`, Rauchtest-Schritt „Archiv“
- V2-Vorbild: `../03_Foto-Viewer-V2/Lib/Utils/Lib_ArchiveExtractor.ps1`
- [PLAN.md](../../PLAN.md) Kap. 1 (Auto-Extract), Kap. 10 (SharpCompress)

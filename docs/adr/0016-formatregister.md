# ADR-0016: Formatregister statt fester Formatliste

- **Status:** Accepted
- **Datum:** 2026-10-19
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** ADR-0006 (Bild- und Video-Pipeline), ADR-0011 (Filter Bilder/Videos)

## Kontext

V0.1 kannte laut PLAN Kap. 1 und ADR-0006 nur JPG, PNG und MP4; die Endungen standen als feste Listen im Scanner, in
den Thumbnail-Renderern und auf der Einstellungsseite. Reale Bibliotheken enthalten daneben HEIC/HEIF vom iPhone,
WebP, GIF/BMP/TIFF, Kamera-RAW sowie MOV, MKV, AVI, WMV, MTS und weitere Container. Jede neue Endung hätte an drei
Stellen gepflegt werden müssen, und die drei Listen drifteten bereits auseinander (der Scanner fand Dateien, für die
kein Renderer zuständig war). Gleichzeitig gilt ADR-0006: Bild-Decoding über WIC, Videos über FFmpeg und LibVLC — WIC
decodiert WebP/HEIC/AVIF/RAW nur, wenn das passende Windows-Codec-Paket installiert ist.

## Entscheidung

Es gibt genau eine Quelle für „welche Endung ist welcher Medientyp“: `IMediaFormatRegistry` in `Services/Media` mit
der Standard-Implementierung `MediaFormatRegistry` (gängige Foto-Formate inkl. HEIC/HEIF/AVIF/WebP und Kamera-RAW,
gängige Video-Container). Scanner (`FolderEnumerator`), Thumbnail-Pipeline und Einstellungsseite arbeiten
ausschließlich über dieses Register. Bilder decodiert WIC (nativ JPG/PNG/GIF/BMP/TIFF, die übrigen mit Codec-Paket);
scheitert WIC mit `wic_decode_failed`, rendert FFmpeg das Vorschaubild als Fallback (`IFallbackImageThumbnailRenderer`).
Videos: Vorschaubild über FFmpeg, Wiedergabe über LibVLC. Das Register wird per Factory registriert
(`TryAddSingleton<IMediaFormatRegistry>(_ => new MediaFormatRegistry())`), weil Microsoft.DI sonst den Konstruktor
mit zwei leeren `IEnumerable<string>` wählt.

## Begründung

- Eine Liste statt drei: Scanner, Renderer und Anzeige bleiben konsistent, eine neue Endung ist eine Zeile.
- Die Klassifikation nach Endung ist beim Scan kostenlos; Inhalts-Sniffing (Magic Bytes) würde bei 500k Dateien jede
  Datei öffnen und den Scan um ein Vielfaches verlangsamen.
- WIC + Codec-Pakete decken die Alltagsformate ohne zusätzliche Bibliothek ab (ADR-0006); FFmpeg ist ohnehin für Videos
  nötig und fängt die Fälle ohne Codec.
- Der Benutzer sieht auf der Einstellungsseite, was die App kennt — Endungen, die fehlen, sind so sofort erklärbar.

## Konsequenzen

### Positiv
- Reale Bibliotheken werden vollständig katalogisiert, nicht nur JPG/PNG/MP4.
- Formate ohne WIC-Codec bekommen trotzdem Vorschaubilder (FFmpeg).
- Ein Testfall (`MediaFormatRegistryTests`) sichert Normalisierung (Kleinschreibung, führender Punkt) und Klassifikation.

### Negativ
- Für WebP/HEIC/AVIF/RAW ohne Codec-Paket zeigt die Lightbox nur einen Platzhalter — die Vorschau im Grid täuscht
  Anzeigbarkeit vor. Die README nennt die Store-Pakete.
- Video-Vorschaubilder hängen an einer lokalen FFmpeg-Installation (`winget install Gyan.FFmpeg`).

### Neutral
- Das Register ist statisch (keine Benutzerkonfiguration); ein Settings-Key für zusätzliche Endungen wäre eine kleine
  Erweiterung ohne Vertragsänderung.
- Nicht erkannte Dateien (z. B. `.txt`) werden beim Scan ignoriert und tauchen nirgends auf.

## Alternativen

- **Feste Liste JPG/PNG/MP4 behalten** → verworfen weil: reale Bibliotheken bestehen zu großen Teilen aus HEIC/MOV.
- **Inhaltsbasierte Erkennung (Magic Bytes) beim Scan** → verworfen weil: öffnet jede Datei, bei NAS und 500k Dateien
  nicht vertretbar; Endung reicht für die Zuordnung, der Renderer meldet kaputte Dateien ohnehin als Fehler.
- **ImageSharp für exotische Formate** → verworfen weil: zusätzliche Abhängigkeit und Lizenzthema (siehe ADR-0006);
  FFmpeg-Fallback deckt dieselben Fälle ab.

## Referenzen

- [src/PhotoViewerV3.Services/Media/IMediaFormatRegistry.cs](../../src/PhotoViewerV3.Services/Media/IMediaFormatRegistry.cs)
- [src/PhotoViewerV3.Services/Media/MediaFormatRegistry.cs](../../src/PhotoViewerV3.Services/Media/MediaFormatRegistry.cs)
- [src/PhotoViewerV3.Services/Thumbnails/ThumbnailService.cs](../../src/PhotoViewerV3.Services/Thumbnails/ThumbnailService.cs) (FFmpeg-Fallback bei `wic_decode_failed`)
- [ADR-0006](0006-bild-und-video-pipeline.md), [CHANGELOG 0.2.0](../../CHANGELOG.md)

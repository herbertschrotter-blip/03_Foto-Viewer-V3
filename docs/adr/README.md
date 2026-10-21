# Architecture Decision Records (ADRs)

Dieses Verzeichnis enthält die Architekturentscheidungen für den PhotoViewer V3 C#-Port.

## Was ist ein ADR?

Ein **Architecture Decision Record** dokumentiert eine wichtige Architekturentscheidung mit Kontext, Begründung und Konsequenzen. Format nach Michael Nygard (leicht erweitert).

## Lifecycle / Status

- **Proposed** — Diskussion läuft
- **Accepted** — Entschieden, gilt
- **Deprecated** — Nicht mehr verwendet, aber noch im Code vorhanden
- **Superseded by ADR-NNNN** — Durch neueres ADR ersetzt

Wenn eine ADR-Entscheidung revidiert wird: **neues ADR anlegen** + altes auf "Superseded by ADR-NNNN" setzen, **niemals altes ADR überschreiben** (Audit-Trail).

## Übersicht

| # | Titel | Status |
|---|---|---|
| [0001](0001-runtime-and-ui-stack.md) | Runtime und UI-Stack (.NET 10 + WPF + WPF-UI) | Accepted |
| [0002](0002-multi-project-solution.md) | Multi-Project Solution ab Tag 1 | Accepted |
| [0003](0003-catalog-modell.md) | Catalog-Modell (eine aktive Library, zentrale DB) | Accepted |
| [0004](0004-datenzugriff-ef-core-raw-sqlite.md) | Datenzugriff: EF Core + Raw SQLite | Accepted |
| [0005](0005-migration-disziplin.md) | Migration-Disziplin | Accepted |
| [0006](0006-bild-und-video-pipeline.md) | Bild- und Video-Pipeline (WIC + FFmpeg + LibVLC) | Accepted |
| [0007](0007-thumbnail-scheduling.md) | Thumbnail-Scheduling (Multi-Channel Queue) | Accepted |
| [0008](0008-storage-profile.md) | Storage-Profile (SSD/HDD/NAS) | Accepted |
| [0009](0009-cache-strategie.md) | Cache-Strategie (lokal in LocalAppData) | Accepted (Größen „160 / 320 / 640“ teilweise ersetzt durch 0043) |
| [0010](0010-ui-datenmodell-paging.md) | UI-Datenmodell mit Paging | Accepted |
| [0011](0011-suche-und-filter-v01.md) | Suche und Filter in V0.1 | Accepted (Filter-Pills ersetzt durch 0041) |
| [0012](0012-operation-logging.md) | Operation-Logging (Batches + Operations) | Accepted |
| [0013](0013-status-ui.md) | Status-UI mit 4 Indikatoren | Accepted |
| [0014](0014-animated-hover-not-v01.md) | Animated Hover NICHT in V0.1 | Accepted |
| [0015](0015-face-recognition-not-v01-schema.md) | Face Recognition NICHT im V0.1-Schema | Accepted (umgesetzt durch 0035) |
| [0016](0016-formatregister.md) | Formatregister statt fester Formatliste | Accepted |
| [0017](0017-modul-entkopplung-messenger.md) | Modul-Entkopplung über WeakReferenceMessenger und Modul-Registrierungen | Accepted |
| [0018](0018-datei-operationen-nur-ueber-service.md) | Datei-Operationen ausschließlich über IFileOperationService | Accepted |
| [0019](0019-incremental-scan.md) | Incremental-Scan: Identität Ordner+Name, Missing statt Löschen, Mass-Missing-Guard | Accepted |
| [0020](0020-bibliothek-mehrere-wurzeln-rollen.md) | Bibliothek mit mehreren Wurzeln und Rollen (Eingang / Archiv) | Accepted |
| [0021](0021-tags-eigene-tabellen.md) | Tags als eigene Tabellen mit Katalog-Zuweisung | Accepted |
| [0022](0022-archive-als-katalog-eintraege.md) | Archive als Katalog-Einträge, Entpacken über SharpCompress | Accepted |
| [0023](0023-neu-aufbauen-uebernimmt-benutzerdaten.md) | „Neu aufbauen“ übernimmt Favoriten, Bewertungen und Tags über den Pfad | Accepted (Ablauf abgelöst durch 0028) |
| [0024](0024-sortierer-ziel-nur-unter-bibliothekswurzeln.md) | Sortierer und Flatten: Quelle und Ziel nur unter den Wurzeln der Bibliothek | Accepted |
| [0025](0025-scan-sperre-fuer-datei-operationen.md) | Scan und Datei-Batch einer Bibliothek schließen sich aus; eigene Schreibvorgänge zählen nicht für die Ordnerüberwachung | Accepted |
| [0026](0026-speicherort-katalog-waehlbar.md) | Speicherort von Katalog-DB und Thumbnail-Cache ist wählbar; Umzug per geprüfter Kopie und Neustart | Accepted |
| [0027](0027-kategorien-mit-festen-werten.md) | Frei konfigurierbare Kategorien mit festen Werten: ein Wert je Datei, Filter und Listenspalte | Accepted (Nachtrag 0.2.23: Strg+K, Lightbox, Suche per Join, serverseitige Sortierung; Ausführung der Sortierung abgelöst durch 0031) |
| [0028](0028-neu-aufbauen-in-einer-transaktion.md) | „Neu aufbauen“ ersetzt den Katalog in einer Transaktion nach der Enumeration — kein Verlust von Katalog und Benutzerdaten bei Abbruch oder Absturz | Accepted |
| [0029](0029-portable-auslieferung-self-contained.md) | Portable Auslieferung als self-contained Ordner (win-x64) mit Version aus dem CHANGELOG | Accepted (Nachtrag 0.2.31: ohne BuildHost) |
| [0030](0030-schreiber-warten-auf-gesperrten-katalog.md) | Schreiber warten asynchron auf einen gesperrten Katalog (zentrale Wiederholung, FileIds nach „Neu aufbauen“ umgesetzt) — keine verlorene Benutzeraktion | Accepted |
| [0031](0031-kategorie-sortierung-segmentweise.md) | Kategorie-Sortierung segmentweise je Wert über einen abdeckenden Index; Zählung bis zur nächsten Änderung zwischengespeichert | Accepted |
| [0032](0032-archive-automatisch-entpacken.md) | Neue Archive in Eingangsordnern optional automatisch entpacken (nach dem Scan, Standard aus) | Accepted (Einstellung global abgelöst durch 0033) |
| [0033](0033-auto-entpacken-je-bibliothek.md) | Automatisches Entpacken je Bibliothek (Übernahme der globalen Vorgabe), Schalter für die aktive Bibliothek, vorhandene Archive nachholen | Accepted |
| [0034](0034-ordner-uebernahme-mit-leerketten-zusammenzug.md) | Ordner in die Bibliothek übernehmen: Struktur bleibt, leere Zwischenordner werden zusammengezogen, ein AdoptFolder-Batch mit Rückgängig; Ordner bewerten; Begriff „Bibliotheksordner“ | Accepted |
| [0035](0035-gesichtserkennung-buffalo-l-directml.md) | Gesichtserkennung mit InsightFace buffalo_l (SCRFD + ArcFace), Modell per Benutzer-Download mit Prüfsumme, DirectML mit CPU-Rückfall, Hintergrund-Index; Gesichter überstehen „Neu aufbauen“ | Accepted (Teil 1) |
| [0036](0036-gesichter-lernen-und-gruppieren.md) | Gesichter lernen nur aus bestätigten Beispielen (k-NN k = 3 + linearer Klassifikator je Person, Distanzprüfung, Ablehnungen, Ausreißer-Prüfung); strenge Gruppen-Vorschläge per Average Linkage | Accepted |
| [0037](0037-gesichter-zuordnen-personen-seite.md) | Gesichter zuordnen: ein Zuordnungsdienst (Modell je aktiver Bibliothek, Neu-Lernen nach jeder Korrektur, Vorschläge per bedingtem UPDATE, Ablehnen → sofort nächstbester), Gruppen-Cache über unbekannte Gesichter, Gesichts-Ausschnitte im Thumbnail-Ordner, Personen-Seite mit abgewählten Ausreißern | Accepted |
| [0038](0038-personen-in-galerie-und-lightbox.md) | Personen in Galerie und Lightbox: Filter/Suche/Spalte direkt in SQL über abdeckende Indizes (`AddFacePersonIndex`), nur bestätigte Gesichter zählen; Rahmen per Taste F; „Gesichtsdaten löschen“ je Bibliothek | Accepted |
| [0040](0040-ignorierte-gesichter-und-mindestgroesse.md) | Ignorierte Gesichter: Ansicht mit „Zurückholen“, ganze Gruppe ignorieren; Mindestgröße (Standard 24 px) als automatisch ignoriert (Zustand 3 + Markierung in `SuggestionScore`, keine Migration), angewandt beim Einfügen durch den Index und einmal je Schwellenänderung durch den Zuordnungsdienst | Accepted |
| [0041](0041-galerie-filter-unabhaengig-kombinierbar.md) | Galerie-Filter unabhängig kombinierbar: Dateiart, nur Favoriten, Ablage und Mindest-Bewertung als eigene Teile (`GalleryFilterState`), mit Tags/Personen/Kategorien/Suche/Ordner per UND; „Es gilt“-Zeile, Filter zurücksetzen (Ordnerauswahl bleibt), Sortierung Feld + Richtung getrennt | Accepted |
| [0042](0042-navigation-und-einstellungen-mit-reitern.md) | Schlankere Navigation (Bibliotheks-Umschalter, Galerie, Ordner, Personen, Sortieren), Einstellungen mit Reitern (Bibliotheken & Eingänge, Tags, Kategorien als eingebettete Seiten; Reiter-Tags im `INavigationService`), „Eingang hinzufügen…“ auf der Ordner-Seite, Info mit Version = oberster CHANGELOG-Eintrag | Accepted |
| [0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md) | Vorschaubilder nach kurzer Kante 400 px (lange ≤ 1200, Q82), in der Galerie passend zu Kachel × Bildschirm-Skalierung dekodiert; alte Größe bis zum Ersatz gezeigt, Pflege im Hintergrund; Bildmaße (nach EXIF-Drehung) in `Files.Width/Height` | Accepted |
| [0044](0044-collage-ansicht-justified-layout.md) | Collage-Ansicht: eigenes virtualisierendes `JustifiedTilePanel` in derselben Galerie-ListBox (Panel/Vorlage je Ansicht als lokale Werte), Umbruch vorab für die ganze Seite (nur verkleinern, letzte Reihe nicht gestreckt, Seitenverhältnis 0,4…6), Seitenverhältnis aus `Files.Width/Height` bzw. Vorschaubild, Dekodierung auf Reihenhöhe × Skalierung | Accepted |
| [0045](0045-personen-details-und-beziehungen.md) | Personen mit Vor-/Nachname, Rufname, eigener Beziehung (Tabelle `PersonRelations`, Freitext mit Vorschlägen, verwaltbar), Geburtsdatum auch ungefähr, „Das bin ich“ (eindeutiger Teilindex); `Persons.Name` bleibt synchroner Anzeigename; Filter „Beziehung“ in der Galerie | Accepted |
| [0046](0046-aufnahmedatum.md) | Aufnahmedatum im Katalog (`Files.CapturedAt/-Source/-Precision/-Offset`, Ausdrucks-Index `COALESCE(CapturedAt, ModifiedAtUtc)`): verwalteter Leser (EXIF in JPEG/TIFF/RAW/HEIC/PNG/WebP, MP4/MOV, Dateiname) im Hintergrund nach dem Scan statt im Scan, Sortierung danach als Standard, „Datum setzen…“ genau/ungefähr/verschieben/zurücksetzen nur im Katalog mit Protokoll und Rückgängig, manuelle Daten überleben „Neu aufbauen“ | Accepted |
| [0047](0047-produktname-lumora.md) | Produktname **Lumora** (Fenster, Taskleiste, Info, Texte; Programmdatei `Lumora.exe`), Schriftzug `LumoraWordmark` im Verlauf je Farbschema (Linse als „o“), App-Symbol als Vektor + reproduzierbar erzeugte `Lumora.ico`; bewusst unverändert: `PhotoViewerV3.*`, `%LocalAppData%\PhotoViewerV3`, `PHOTOVIEWERV3_*`, `photoviewer-*.log` | Accepted |
| [0049](0049-versteckte-bibliotheken.md) | Versteckte Bibliotheken im verschlüsselten Container: eigener Katalog in `<Bibliothek>\.lumora`, Katalogwechsel zur Laufzeit als neuer DI-Container (`ICatalogHostSwitcher`, `ICatalogContext`/`ICatalogStoragePaths`), nur mit Startschalter `_hidden`, Lumora-Kennwort (PBKDF2-SHA256, 600 000), Zurücksetzen per `.lumora\reset-password.txt`, Zusperren nach Untätigkeit, Aushängen (VeraCrypt/VHDX), Protokolle je Sitzung, keine Spuren außerhalb | Accepted |
| [0051](0051-orte-weltkarte-mit-foto-gruppen.md) | Seite „Orte“: eigene Kachelkarte (`MapControl`, OSM-Kacheln nach Nutzungsregeln, Platten-Cache 500 MB LRU über `ICatalogStoragePaths`, abschaltbar) statt Mapsui/WebView2; Gruppen als hierarchisches Morton-Raster (Präfixsummen + Segmentbaum, nahe Marker zusammengelegt; 500 000 Fotos ≈ 0,3 s, Abfrage &lt; 1 ms); Start = Ausdehnung + Rand; Verorten aus „Ohne Ort“ über `IGeoTagService`; „In der Galerie zeigen“ per `ShowFilesOnMapSelectionMessage` | Accepted |

## Quellen der Entscheidungen

ADR-0001 bis ADR-0015 wurden in 3 ChatGPT-Review-Runden mit GPT-5 validiert. Siehe [`chatgpt-review/`](../../chatgpt-review/) für Original-Diskussion.
ADR-0016 bis ADR-0019 (und ADR-0020 ab 0.2.5) dokumentieren die Entscheidungen der Umsetzung bis Release 0.2.0 (siehe [CHANGELOG.md](../../CHANGELOG.md)).

## Template für neue ADRs

Siehe [TEMPLATE.md](TEMPLATE.md) als Vorlage für neue ADRs.

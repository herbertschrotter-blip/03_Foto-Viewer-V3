# PhotoViewer V3

Native Windows-Desktop-App (C# / .NET 10 / WPF mit WPF-UI 4, SQLite) zum Ansehen, Bewerten, Sortieren und Verschieben
großer Foto- und Video-Bibliotheken (50 000–500 000 Dateien). Nachfolger der PowerShell-Web-App V2
(`../03_Foto-Viewer-V2`), die bis zur Ablösung im Alltag weiterläuft. Stand: **0.2.0** (siehe [CHANGELOG.md](CHANGELOG.md)).

## Features (0.2.0)

- **Bibliothek**: Foto-Ordner als Bibliothek anlegen, Katalog in SQLite aufbauen (Fresh-Scan), später „Aktualisieren“
  (Incremental-Scan mit stabilen Datei-Ids — Favoriten, Bewertungen und Vorschaubilder bleiben) oder „Neu aufbauen“.
  Fehlende Dateien werden markiert, nicht gelöscht; ist das Laufwerk nicht erreichbar, wird nichts markiert
  (Offline-Guard), bei ungewöhnlich vielen fehlenden Dateien fragt die App nach (Mass-Missing-Guard).
- **Galerie**: seitenweises, virtualisiertes Kachel-Grid (200/500/1000 pro Seite) mit echten Vorschaubildern (WIC,
  EXIF-Orientierung; Videos und Formate ohne Windows-Codec über FFmpeg), Sortierung nach Datum/Name/Größe, Volltextsuche
  über Dateiname und Endung (FTS5), Filter Alle/Bilder/Videos/Favoriten, Ordnerfilter, Unterordner-Kacheln für Ordner
  ohne direkte Dateien, Favorit und 1–5 Sterne je Kachel, Mehrfachauswahl mit Aktionsleiste (Favorit, Bewertung,
  Tags, Verschieben nach…, Papierkorb) und Rückgängig. Umschaltbar auf eine **Listenansicht** mit Spalten.
- **Tags**: frei definierbare, farbige Schlagworte (Seite „Tags“), Zuweisen für die Auswahl (Aktionsleiste,
  Kontextmenü, Strg+T), Tag-Punkte auf der Kachel, Filter nach Tags (ODER/UND/ohne Tag), Suche findet Tag-Namen.
- **Lightbox**: eigenes Fenster (auch auf einem zweiten Monitor) für Bilder mit Zoom/Pan und Info-Panel sowie Videos
  über LibVLC mit Transportleiste; Favorit, Bewertung, Papierkorb, „Im Explorer anzeigen“, „Mit Standardprogramm
  öffnen“.
- **Archive** (0.2.8): ZIP, RAR, 7z, TAR, GZ/TGZ, BZ2 und XZ erscheinen als Kacheln im Katalog (Icon, Endung, Größe;
  Filter „Archive“); die Lightbox zeigt ihren Inhalt, „Entpacken…“ legt die Dateien neben das Archiv und nimmt sie
  sofort in den Katalog auf (siehe [Archive](#archive)).
- **Ordner**: Ordnerbaum der Bibliothek mit Dateizählern, rechts die Galerie des gewählten Ordners (mit oder ohne
  Unterordner).
- **Sortieren**: Pattern-basierter File Sorter (Regex-Profile, Multi-Level, Vorschau, Import/Export der V2-Profile)
  und Flatten & Move — jede Verschiebung läuft protokolliert über die Datei-Operationen und ist rückgängig machbar.
- **Einstellungen**: Speicherorte (Katalog samt Vorschaubildern umziehbar), Thumbnail-Cache, FFmpeg-Status, unterstützte Formate, Farbschema (Hell/Dunkel/System),
  Version.
- **Statusleiste**: Scan-Fortschritt und -Ergebnis, Thumbnail-Warteschlange, Hintergrund-Aktivität, Hinweise
  (Bibliothek offline, unvollständige Dateivorgänge, Datei-Operation wartet auf laufenden Scan — mit **Abbrechen**).
- **Sicherheit der Daten**: Löschen geht immer in den Windows-Papierkorb (nie still hart löschen); Verschieben,
  Umbenennen und Löschen werden in `OperationBatches`/`FileOperations` protokolliert; Move/Rename sind rückgängig
  machbar.

## Voraussetzungen

| | |
|---|---|
| Betriebssystem | Windows 11 (Mica-Hintergrund, WPF-UI 4) |
| Entwicklung | [.NET 10 SDK](https://dotnet.microsoft.com/download) (Solution-Format `.slnx`); Visual Studio mit .NET-10-Workload oder die `dotnet`-CLI |
| Video-Vorschaubilder | FFmpeg im `PATH`: `winget install Gyan.FFmpeg` (danach App neu starten). Ohne FFmpeg laufen Bilder normal, Videos bekommen keine Vorschaubilder; die Seite „Einstellungen“ zeigt den Status. |
| Video-Wiedergabe | LibVLC kommt als NuGet-Paket (`VideoLAN.LibVLC.Windows`) mit — keine Installation nötig. |
| Optional | Windows-Codec-Pakete aus dem Microsoft Store für HEIC/HEIF („HEIF-Bilderweiterungen“ + „HEVC-Videoerweiterungen“), WebP („Webp-Bilderweiterungen“) und Kamera-RAW („Raw-Bilderweiterung“). Ohne Codec erzeugt FFmpeg die Vorschaubilder, die Lightbox kann solche Bilder aber nicht anzeigen. |

## Build & Start

```powershell
cd "D:\OneDrive\Dokumente\02 Arbeit\05 Vorlagen - Scripte\03_Foto-Viewer-V3"
dotnet build PhotoViewerV3.slnx -c Debug --nologo
dotnet run --project src/PhotoViewerV3.Wpf -c Debug
```

Oder in Visual Studio: `PhotoViewerV3.slnx` öffnen, `PhotoViewerV3.Wpf` als Startprojekt, **F5**.
Beim ersten Start legt die App die Datenbank an (EF-Core-Migration) und zeigt die Seite „Bibliothek“.

Eine portable Auslieferung (`dotnet publish`) gibt es noch nicht; die App läuft als lokaler Debug-Build.

## Bedienung

### Bibliothek
- **Neue Bibliothek…** → Foto-Ordner wählen, Namen vergeben, auf Nachfrage sofort scannen.
- Je Bibliothek: **Scannen** (leerer Katalog) bzw. **Aktualisieren** (Incremental: neue, geänderte, fehlende und wieder
  vorhandene Dateien; Ids, Favoriten, Bewertungen, Vorschaubilder bleiben), **Neu aufbauen** (Katalog komplett neu mit
  neuen Ids und Vorschaubildern; Favoriten, Bewertungen und Tags werden für Dateien übernommen, die noch am selben Ort
  liegen — gelöschte, verschobene oder umbenannte Dateien verlieren sie), **Aktiv setzen**, Umbenennen, Entfernen (löscht keine Dateien auf der
  Platte).
- Während eines Scans: Fortschrittspanel (Ordner, Dateien, Größe, Pfad, Dauer) mit **Abbrechen**; danach eine
  Zusammenfassung. Läuft gerade ein anderer Scan derselben Bibliothek (z. B. die automatische Aktualisierung) oder eine
  Datei-Operation (Verschieben, Sortierer …), zeigt das Panel bis zum Start „Wartet auf laufenden Scan…“ bzw. „Wartet auf
  laufende Datei-Operation…“; **Abbrechen** beendet das Warten, der Katalog bleibt unverändert. Bei nicht erreichbarem Ordner: Dialog „Bibliothek offline“ ([Erneut prüfen] [Andere Bibliothek
  wählen]); bei ungewöhnlich vielen fehlenden Dateien (> 10 000 oder > 20 %): Dialog ([Erneut prüfen]
  [Als fehlend markieren] [Abbrechen]).
- Je Bibliothek die Liste ihrer Wurzelordner mit Rolle (siehe „Eingang und Archiv“): **Eingang hinzufügen…**,
  Archiv-Symbol = diesen Ordner zur Archiv-Wurzel machen, Umbenennen (Anzeigename), Entfernen (nur Eingänge, nur aus
  dem Katalog). Scannen/Aktualisieren läuft über alle Wurzeln.

### Eingang und Archiv
Eine Bibliothek hat genau einen **Archiv-Ordner** (der beim Anlegen gewählte Ordner) und beliebig viele
**Eingangs-Ordner** (Kamera-Import, Handy-Upload, Downloads). Dateien unter einem Eingang gelten als „nicht archiviert“.
- **Bibliothek → Eingang hinzufügen…**: Ordner wählen (darf nicht in oder über einem anderen Ordner der Bibliothek
  liegen), danach „Jetzt aktualisieren“ nimmt die Dateien in den Katalog auf.
- **Galerie**: Dropdown **Status** mit **Nicht archiviert** (alles in Eingängen) und **Archiviert**; Kacheln im Eingang tragen das
  Badge „Eingang“. Der Ordnerbaum zeigt die Wurzeln als oberste Knoten mit Rolle.
- **Ins Archiv…** (Aktionsleiste, **Strg+M**, Kontextmenü der Kachel, in der Lightbox **Strg+M**): Dialog mit dem
  Ordnerbaum des Archivs (auch Ordner, die nur auf der Platte existieren), Suchfeld, **Neuer Ordner…**,
  **Umbenennen…**, Vorschlag „Jahr\Jahr-Monat“ aus Aufnahme- bzw. Dateidatum; der zuletzt benutzte Zielordner ist
  vorgewählt. Verschoben wird protokolliert (Rückgängig wie bei „Verschieben nach…“); Favoriten, Bewertungen und
  Vorschaubilder bleiben. Die Ansicht bleibt beim Filter und zeigt die restlichen Dateien.
- Einen Eingang zu entfernen löscht nur seine Katalogdaten — die Dateien auf der Platte bleiben.

### Galerie
Toolbar: Filter-Pills als Segmentgruppe (Alle / Bilder / Videos / Favoriten / Archive), Dropdown **Status** (Nicht archiviert /
Archiviert), Tag-Filter, Suchfeld (Dateiname, Endung oder Tag-Name; Wörter werden
als Präfix gesucht, Sonderzeichen sind erlaubt; ✕ leert die Suche), Sortierung, Seitengröße, **Vorschaugröße** (Regler neben
der Ansichtsumschaltung, nur in der Kachelansicht: Kachelbreite 120–320 px in 20er-Stufen, wird gemerkt), Pager, Aktualisieren. In schmalen
Fenstern (bzw. rechts auf der Ordner-Seite) rutscht die Suche in eine eigene Zeile, statt gequetscht zu werden. Ein Ordnerfilter erscheint als
Chip und lässt sich dort wieder aufheben. Kacheln: Herz = Favorit, Sterne = Bewertung, Badge „fehlt“ bei Dateien, die
beim letzten Scan nicht mehr auf der Platte lagen. Ordner ohne direkte Dateien zeigen ihre Unterordner als Kacheln.
Bei einer Auswahl erscheint unten die Aktionsleiste (Favorit setzen/entfernen, Bewertung, Verschieben nach…,
Ins Archiv…, Papierkorb, Auswahl aufheben); **Rückgängig** nimmt die letzte Verschiebung/Umbenennung (auch eine
Ordner-Umbenennung) zurück.

| Taste | Wirkung |
|---|---|
| Klick, Strg+Klick, Umschalt+Klick | Auswahl, Mehrfachauswahl |
| Enter, Doppelklick | Lightbox öffnen |
| Strg+A | Alles auf der Seite auswählen |
| Strg+Mausrad, Strg+Plus / Strg+Minus | Vorschaugröße größer / kleiner (Kachelansicht) |
| Esc | Auswahl aufheben (im Suchfeld: Suche leeren) |
| Entf | Auswahl in den Papierkorb (mit Bestätigung) |
| Strg+← / Strg+→ | Vorherige / nächste Seite |
| Strg+Pos1 / Strg+Ende | Erste / letzte Seite |
| F5 | Aktualisieren |
| Strg+M | Auswahl ins Archiv… |
| Strg+T | Tags der Auswahl bearbeiten (Tag-Auswahl) |
| Rechtsklick | Tag-Menü der Auswahl (nicht ausgewählte Kachel/Zeile wird zuerst gewählt) |
| Enter im Suchfeld | Suchen |

#### Tags
Tags legt man auf der Seite **Tags** an (Name, Farbe aus einer Palette mit zwölf Farben, Reihenfolge, Umbenennen,
Löschen mit Bestätigung und Anzahl der betroffenen Dateien) oder direkt in der Tag-Auswahl („Neues Tag…“). Zuweisen:
Knopf **Tags** in der Aktionsleiste oder Rechtsklick — Häkchen = alle ausgewählten Dateien haben das Tag, „(teilweise)“
= nur ein Teil; Klick setzt das Tag bei allen bzw. entfernt es. **Strg+T** öffnet die Tag-Auswahl mit dreistufigen
Häkchen für eine gemischte Auswahl. Die Kachel zeigt je Tag einen farbigen Punkt (Hover = Namen). Das Dropdown **Tags**
neben den Filter-Pills filtert nach einem oder mehreren Tags — „mindestens eines“ (ODER), „alle“ (UND) oder „nur
Dateien ohne Tag“. Die Suche findet neben Dateinamen auch Dateien, deren Tag-Name den Suchtext enthält. Tags hängen am
Katalog-Eintrag: „Aktualisieren“ und „Neu aufbauen“ (über den Pfad) behalten sie, der Papierkorb entfernt die Zuweisungen.

#### Kategorien
Eigene Kategorien mit festen Werten (z. B. „Projekt“: Nord, Süd) legt man auf der Seite **Kategorien** an (ADR-0027).
Jede Datei hat je Kategorie höchstens einen Wert. Zuweisen: Rechtsklick auf Kachel oder Zeile → Untermenü der Kategorie
→ Wert (Häkchen = alle ausgewählten Dateien haben ihn, „(teilweise)“ = nur ein Teil; **Kein Wert** entfernt ihn). Das
Dropdown **Kategorien** neben dem Tag-Filter (erscheint, sobald es Kategorien gibt) filtert je Kategorie nach einem
oder mehreren Werten und/oder „ohne Wert“ — innerhalb einer Kategorie ODER, zwischen Kategorien UND. In der
Listenansicht ist jede Kategorie eine eigene Spalte. Werte hängen wie Tags am Katalog-Eintrag („Neu aufbauen“ behält
sie, der Papierkorb entfernt sie).

#### Listenansicht
Der Umschalter rechts in der Kopfzeile wechselt zwischen Kachel-Grid und Liste (die Wahl bleibt gespeichert). Die
Liste zeigt dieselbe Seite mit den Spalten Name (mit Vorschaubild), Ordner, Geändert, Größe, Typ, Bewertung (Sterne
klickbar), Favorit (Herz klickbar), Tags, Status und je Kategorie eine Spalte (neue Kategorien erscheinen am Ende).
Rechtsklick auf eine Spaltenüberschrift blendet Spalten ein/aus,
Ziehen ändert die Reihenfolge — beides bleibt gespeichert. Klick auf Name, Geändert oder Größe sortiert die ganze
Trefferliste (wie die Sortier-Auswahl), die übrigen Spalten sortieren nur die geladene Seite. Auswahl, Aktionsleiste,
Doppelklick/Enter (Lightbox) und Tastenkürzel wie im Grid.

### Lightbox
Rahmenloses Fenster, Toolbar und Titelleiste blenden sich nach kurzer Zeit aus (Mausbewegung oder Taste blendet sie
ein). Bilder: Mausrad = Zoom am Cursor, Ziehen = Verschieben, Doppelklick = Einpassen ↔ 100 %. Videos: Transportleiste
mit Position, Wiedergabe/Pause, Lautstärke, Stumm. Das Info-Panel zeigt Datei- und Medieninfos (Auflösung, Dauer,
Codec).

| Taste | Wirkung |
|---|---|
| ← / → | Vorheriges / nächstes Medium |
| Pos1 / Ende | Erstes / letztes Medium |
| Bild↑ / Bild↓ | 10 Medien zurück / vor |
| F, F11 | Vollbild ein/aus |
| Esc | Vollbild verlassen, sonst Lightbox schließen |
| I | Info-Panel ein/aus |
| + / − | Vergrößern / verkleinern (Bild) |
| 1 | 100 % (Bild) |
| 0 | Einpassen (Bild) |
| Leertaste | Wiedergabe / Pause (Video) |
| M | Stumm (Video) |
| Strg+D | Favorit umschalten |
| Strg+1 … Strg+5 | Bewertung 1–5 Sterne |
| Strg+0 | Bewertung löschen |
| Strg+M | Ins Archiv… |
| Strg+T | Tags der Datei bearbeiten (die Titelleiste zeigt die Tags) |
| Entf | In den Papierkorb (mit Bestätigung) |

### Archive
Archive (`.zip .rar .7z .tar .gz .tgz .bz2 .xz`) nimmt der Scan wie Fotos in den Katalog auf; sie bekommen kein
Vorschaubild, die Kachel zeigt ein Archiv-Symbol mit Endung und Größe. Die Filter-Pill **Archive** zeigt nur sie.
- **Inhalt ansehen:** Enter/Doppelklick (oder Kontextmenü „Inhalt anzeigen“) öffnet das Archiv in der Lightbox als Liste
  der Einträge mit Größe, komprimierter Größe und Datum; Fotos/Videos und Archive im Archiv sind markiert. Knöpfe
  **Entpacken…** und **Im Explorer anzeigen**.
- **Entpacken…** (Aktionsleiste für alle ausgewählten Archive, Kontextmenü der Kachel oder Lightbox) öffnet einen Dialog:
  Ziel (Standard: Unterordner mit dem Archivnamen neben dem Archiv, oder ein eigener Ordner), vorhandene Dateien
  überspringen / überschreiben / umbenennen („Name (2).jpg“), nur Fotos und Videos (Standard) oder alles, Archive im
  Archiv mitentpacken (bis 3 Ebenen, die Zwischen-Archive werden danach entfernt), Archiv danach in den Papierkorb
  (nur wenn fehlerfrei; protokolliert wie jedes Löschen). Fortschritt mit Dateien und Bytes, **Abbrechen** jederzeit;
  am Ende Zusammenfassung und Fehlerliste. Danach aktualisiert die App die Bibliothek (Incremental-Scan), die entpackten
  Dateien erscheinen sofort in der Galerie.
- Passwortgeschützte Archive werden angezeigt, aber mit klarer Meldung nicht entpackt (kein Passwort-Dialog); Einträge
  mit Pfaden außerhalb des Zielordners („Zip-Slip“, `..\..\`) werden übersprungen und als Fehler gemeldet.
- Entpackt wird in der App über SharpCompress (keine Installation von 7-Zip/WinRAR nötig, ADR-0022). Automatisches
  Entpacken beim Scan gibt es noch nicht.

### Ordner
Links der Ordnerbaum der aktiven Bibliothek (Dateizähler direkt / gesamt), rechts die Galerie. Umschalter „mit
Unterordnern“; „Gesamte Bibliothek“ hebt den Ordnerfilter auf. Nach Scans und Datei-Operationen lädt der Baum neu und
behält Aufklapp-Zustand und Auswahl. Oberste Knoten sind die Wurzeln mit Rolle (Archiv, Eingang). Kontextmenü:
**Neuer Ordner…** (Ordner auf der Platte + Katalogzeile) und **Umbenennen…** (nicht für Wurzeln; Ordner mit Dateien
werden protokolliert umbenannt und lassen sich mit Rückgängig zurücknehmen).

### Sortieren
- **Quelle und Ziel** (beide Bereiche): je eine Auswahl der Wurzeln der aktiven Bibliothek mit Rolle („Eingang“ /
  „Archiv“) und Pfad, darunter ein frei wählbarer Ordner. Vorbelegt ist der erste erreichbare Eingang als Quelle und
  die Archiv-Wurzel als Ziel — der Normalfall „aus dem Eingang ins Archiv sortieren“. Als Ziel geht auch
  „Quellordner“ (die Ordner entstehen an Ort und Stelle). Offline-Wurzeln sind ausgegraut; Ziele außerhalb aller
  Wurzeln sind gesperrt, weil die Dateien sonst aus dem Katalog fielen und Bewertung, Favorit und Tags verlören
  (ADR-0024). Die Auswahl folgt Bibliothekswechseln und geänderten Wurzeln. Verschobene Dateien behalten ihre FileId
  und wechseln im Katalog zur Zielwurzel (Galerie-Filter „Nicht archiviert“ / „Archiviert“).
- **Sortieren**: Quellordner unter einer Wurzel der Bibliothek wählen, Patterns (Regex auf den Dateinamen ohne Endung, nach
  Priorität) und optional Sub-Level-Patterns (auf den vollen Dateinamen) pflegen — Profile liegen in der Datenbank,
  Import/Export im V2-JSON-Format. **Analysieren** zeigt die Gruppen (Zielordner änderbar, Gruppen zusammenführbar),
  **Vorschau** die Quelle → Ziel-Liste, **Sortieren** führt aus; Dateien ohne Treffer landen in `_unsorted`.
  **Rückgängig (letzter Lauf)** nimmt alle Verschiebungen des letzten Laufs zurück. Die eingebaute Hilfe erklärt die
  Regex-Grundlagen.
- **Flatten & Move**: Mediendateien aus Unterordnern beliebiger Tiefe nach `Ziel\Ebenenname\Datei` verschieben
  (Ebenenname aus den Pfad-Ebenen, Duplikate entfernt, Separator wählbar; Dateien in der Quellwurzel → `_root`;
  Namenskonflikte → `_1`, `_2`, …), optional direkt in die Zielwurzel und leere Quellordner löschen. Vorschau, Ausführen,
  Rückgängig.

### Tags
Verwaltung der Tags: neues Tag anlegen (Enter), je Tag Farbe aus der Palette, nach oben/unten (Reihenfolge in Menüs,
Chips und Filter), Umbenennen, Löschen (fragt nach und nennt die Zahl der Dateien; die Dateien selbst bleiben
unverändert). Neben dem Namen steht, wie vielen Dateien das Tag zugewiesen ist.

### Kategorien
Verwaltung der Kategorien (ADR-0027): neue Kategorie mit Namen und optional gleich ihren Werten („N, NN“) anlegen
(Enter); je Kategorie eine Karte mit Umbenennen, Löschen (fragt nach und nennt die Zahl der Dateien) und Reihenfolge
(Filter, Spalten, Kontextmenü). Werte: hinzufügen (mehrere durch Komma), umbenennen, nach oben/unten, löschen (betroffene
Dateien haben danach keinen Wert in dieser Kategorie). Neben jedem Wert steht, wie vielen Dateien er zugeordnet ist.

### Einstellungen
Farbschema (Hell/Dunkel/System folgt Windows live), Speicherorte mit „Ordner öffnen“ und **Ändern…** (siehe
„Speicherort des Katalogs“), Thumbnail-Cache (Belegung,
**Cache leeren** — Vorschaubilder werden bei Bedarf neu erzeugt), FFmpeg-Status mit kopierbarem winget-Befehl,
unterstützte Bild- und Videoformate, Ordnerüberwachung (Schalter, Ruhezeit), Version und Build-Datum.

#### Speicherort des Katalogs
**Einstellungen → Speicherorte → Datenbank → Ändern…** zieht Katalog (`library.db`) und Vorschaubilder (`thumbs\`) in
einen anderen Ordner (ADR-0026); **Standardort** holt sie nach `%LocalAppData%\PhotoViewerV3` zurück.
- Der Ordner muss auf einem lokalen Laufwerk liegen (interne Platte, SSD, USB) — Netzlaufwerke und `\\Server\…` werden
  abgelehnt, ebenso Ordner in einer Bibliothekswurzel (die Vorschaubilder würden sonst als Fotos eingelesen). Er muss
  beschreibbar sein und genug Platz bieten (Katalog + Vorschaubilder + 10 % + 50 MB). OneDrive-Ordner gehen, aber mit
  Warnung: die Synchronisierung kann den laufenden Katalog beschädigen.
- Liegt im Ziel schon ein Katalog: **Übernehmen** (PhotoViewer arbeitet danach mit diesem; der bisherige bleibt
  unverändert liegen) oder **Ersetzen** (der vorhandene wird beiseitegelegt und beim nächsten Start gelöscht).
- Ablauf: laufende Scans und Datei-Operationen werden abgewartet (höchstens 30 s), Ordnerüberwachung und Vorschaubilder
  angehalten, der Katalog per SQLite-Backup kopiert und geprüft (Integrität, Zeilenzahl jeder Tabelle), dann die
  Vorschaubilder kopiert. Erst danach gilt der neue Ort; PhotoViewer startet neu und löscht beim Start die alten Dateien.
  Während des Umzugs ist das Fenster gesperrt.
- Geht etwas schief (Platte voll, Prüfung fehlgeschlagen, Scan läuft noch), wird das Ziel aufgeräumt und PhotoViewer
  arbeitet mit dem bisherigen Ort weiter; ein Dialog nennt den Grund.
- Ist der gewählte Ordner beim Start nicht erreichbar (USB-Laufwerk abgezogen), startet PhotoViewer nicht und nennt die
  Datei `database-location.json`, deren Löschen zum Standardort zurückführt.

### Ordnerüberwachung
Die Bibliotheksordner der aktiven Bibliothek werden im Hintergrund überwacht (`FileSystemWatcher` je Wurzel, rekursiv).
Neue, gelöschte, umbenannte oder geänderte Fotos und Videos (nur Formate aus dem Formatregister; `.tmp`/`.part`,
`~`-Dateien, `Thumbs.db`, `.thumbnails`, `@eaDir` und der eigene Thumbnail-Cache werden ignoriert) starten eine
**Ruhezeit** (Standard 3 s, einstellbar 1–60 s). Erst wenn in dieser Zeit nichts mehr passiert, läuft automatisch
„Aktualisieren“ (Incremental-Scan, stabile FileIds) — die Statusleiste zeigt vorher „Änderungen erkannt —
Aktualisierung in n s“, danach „Automatisch aktualisiert: …“. Regeln:

- Nie zwei Scans derselben Bibliothek gleichzeitig — egal ob „Aktualisieren“, „Neu aufbauen“, Entpacken oder Automatik:
  der spätere wartet, bis der laufende fertig ist. Änderungen, die vor dem Start eines manuellen Scans passiert sind,
  erledigt dieser gleich mit (kein zweiter automatischer Scan danach); was während des Scans passiert, prüft die Automatik
  danach erneut. Wird der manuelle Scan abgebrochen, gelten die gesammelten Änderungen wieder.
- Auch Ordner zählen (angelegt, hinein-/hinausverschoben, umbenannt, gelöscht), mit Punkt im Namen wie `2024.06 Urlaub`.
  Einzige Lücke: ein *gelöschter* Ordner, dessen Name wie eine Dateiendung endet (`Fotos.alt`), ist von einer gelöschten
  Nicht-Mediendatei nicht zu unterscheiden und löst keinen Scan aus.
- Wechselt die aktive Bibliothek während eines automatischen Scans, wird dessen Ergebnis (auch eine Sperre) verworfen und
  nicht der neuen Bibliothek zugeordnet.
- Eigene Datei-Vorgänge der App (Verschieben, „Ins Archiv…“, Papierkorb) buchen den Katalog selbst; die Überwachung sieht
  ihre Dateiereignisse trotzdem und aktualisiert danach einmal automatisch (Ergebnis „keine Änderungen“). Entpacken
  aktualisiert am Ende selbst — die Ereignisse davor sind damit erledigt, ein zweiter automatischer Scan entfällt in der
  Regel.
- Meldet der automatische Scan **viele fehlende Dateien** (Mass-Missing-Guard) oder eine **Bibliothek offline**, wird nicht
  automatisch wiederholt; die Statusleiste zeigt „Automatische Aktualisierung angehalten: …“, bis ein manuelles
  „Aktualisieren“ auf der Bibliotheksseite erfolgreich war (oder die Bibliothek gewechselt bzw. die Überwachung
  umgeschaltet wird).
- Nicht erreichbare Wurzeln (USB/NAS) werden alle 30 s erneut versucht; kommt die Wurzel zurück, folgt ein Scan nach der
  Ruhezeit. Ein Pufferüberlauf des Watchers startet ihn neu und plant ebenfalls einen Scan ein.
- Einstellungen „Bibliotheksordner überwachen“ (Standard: an) und „Ruhezeit“ liegen in der Settings-Tabelle
  (`watch.enabled`, `watch.quietSeconds`) und wirken sofort; schnelle Änderungen (Pfeile der Ruhezeit) werden der Reihe
  nach gespeichert, der zuletzt eingestellte Wert gilt.
- Beim Kopieren Tausender Dateien verschiebt jedes Ereignis den Countdown; die Statusleiste bekommt davon höchstens zwei
  Meldungen je Sekunde (gebündelt, Background-Priorität).

### Statusleiste
Links: aktive Bibliothek, Scan-Fortschritt bzw. -Ergebnis, Vorschaubilder in der Warteschlange und — solange eine
Datei-Operation (Verschieben, Papierkorb, Ins Archiv, Umbenennen, Ordner anlegen/umbenennen, Rückgängig, Sortierer-Lauf)
auf einen laufenden Scan derselben Bibliothek wartet — „Wartet auf laufenden Scan…“ mit **Abbrechen**: die Operation
wird dann verworfen, bevor sie etwas ändert (beim Sortierer entfallen die restlichen Ziele; schon verschobene Dateien
bleiben verschoben und sind rückgängig machbar). Rechts: Hinweise
(Ordnerüberwachung: Countdown, laufende/abgeschlossene automatische Aktualisierung, angehaltene Automatik;
unvollständige oder fehlgeschlagene Dateivorgänge mit Details im Tooltip; Bibliothek offline) und die
Hintergrund-Aktivität („Bereit“, wenn nichts läuft).

## Datenablage

Alles liegt unter `%LocalAppData%\PhotoViewerV3` (Fotos und Videos bleiben an Ort und Stelle). Katalog und
Vorschaubilder können in den Einstellungen in einen anderen Ordner umziehen; dann liegen `library.db` und `thumbs\`
dort, Protokolle und `database-location.json` bleiben hier:

| Pfad | Inhalt |
|---|---|
| `database-location.json` | Nur nach einem Umzug: gewählter Ordner für Katalog und Vorschaubilder, alte Dateien zum Löschen beim nächsten Start |
| `library.db` (+ `-wal`, `-shm`) | SQLite-Katalog: Bibliotheken, Ordner, Dateien, Thumbnails-Status, OperationBatches/FileOperations, Settings, FTS5-Index |
| `thumbs\{libraryId}\{fileId}_320_still.jpg` | Vorschaubilder (320 px, FileId-basiert, atomar geschrieben) |
| `logs\photoviewer-YYYYMMDD.log` | Serilog-Protokoll, täglich rollierend, 14 Tage |

Die Datenbank wird beim Start per EF-Core-Migration auf den aktuellen Stand gebracht; unterbrochene Thumbnail-Jobs
werden dabei zurückgesetzt und unfertige Dateivorgänge gemeldet.

## Umgebungsvariablen

| Variable | Wirkung |
|---|---|
| `PHOTOVIEWERV3_APPDATA` | Anderer AppData-Root (DB, Thumbnails, Logs, `database-location.json`) — z. B. für Tests mit frischer Datenbank, ohne den eigenen Katalog anzufassen |
| `PHOTOVIEWERV3_SMOKE_ROOT` | Startet nach dem Laden des Hauptfensters den Rauchtest gegen diesen Bibliotheksordner. **Dateien darin werden verschoben und in den Papierkorb gelegt** — nur mit einer Kopie verwenden. |
| `PHOTOVIEWERV3_SMOKE_SHOTS` | Ordner für PNG-Screenshots der Hauptansichten (optional) |
| `PHOTOVIEWERV3_SMOKE_EXIT` | `1` → App nach dem Rauchtest beenden; ExitCode 0 = alle Schritte OK, 2 = mindestens ein Schritt FAIL |
| `PHOTOVIEWERV3_SMOKE_CLEANUP` | `1` → die Testbibliothek am Ende wieder aus dem Katalog entfernen |

Das Ergebnis des Rauchtests steht in `<AppData>\smoke-result.txt` und im Log (Zeilen „Rauchtest …“).

## Tests

```powershell
dotnet test tests/PhotoViewerV3.Tests --nologo        # Services + Data (xUnit, FluentAssertions; echte SQLite-Migration in Temp)
dotnet test tests/PhotoViewerV3.Wpf.Tests --nologo    # WPF-gebundener Code (WIC-Renderer, GalleryQuery, Converter, Tags, Listenansicht)
```

Integrations-Rauchtest (steuert die ganze App über ihre ViewModels, prüft Dateisystem und Datenbank, macht Screenshots):

```powershell
$lib = "$env:TEMP\pv3-smoke-lib"; Copy-Item -Recurse <Testbibliothek> $lib
$env:PHOTOVIEWERV3_APPDATA     = "$env:TEMP\pv3-smoke-appdata"
$env:PHOTOVIEWERV3_SMOKE_ROOT  = $lib
$env:PHOTOVIEWERV3_SMOKE_SHOTS = "$env:TEMP\pv3-smoke-shots"
$env:PHOTOVIEWERV3_SMOKE_EXIT  = '1'
dotnet run --project src/PhotoViewerV3.Wpf -c Debug
Get-Content "$env:PHOTOVIEWERV3_APPDATA\smoke-result.txt"   # Erwartung: 16 OK, 0 FAIL
```

Die Testbibliothek sollte Bilder (JPG/PNG), Videos (MP4/MOV), einen Ordner ohne direkte Dateien mit zwei
Unterordnern („Leer\Sub A“, „Leer\Sub B“), einen Ordner „Unsortiert“ mit mehreren JPGs sowie die Ordner
„2024\Familie“ und „2025\Screenshots“ enthalten (das Szenario prüft diese Namen) — insgesamt mindestens sechs
Mediendateien, damit der Mass-Missing-Guard beim Incremental-Schritt nicht greift. Der Schritt legt
`Unsortiert\smoke-neu.jpg` an (bleibt liegen, wird beim nächsten Lauf vorab entfernt); der Archiv-Schritt legt
`Unsortiert\smoke-archiv.zip` an, entpackt es nach `Unsortiert\smoke-archiv\` und verschiebt das ZIP in den
Windows-Papierkorb (der Ordner wird beim nächsten Lauf vorab entfernt). Die Ordnerüberwachung ist während des Laufs
ausgeschaltet und wird erst im letzten Schritt (Ruhezeit 1 s, `Unsortiert\smoke-watch.jpg` kopieren und löschen)
eingeschaltet. Der Schritt „Eingang → Archiv“
legt den Geschwisterordner `<Bibliothek>-Eingang` als Eingangs-Wurzel an (wird am Ende wieder entfernt) und hinterlässt
in der Bibliothek die leeren Ordner `2026\Smoke-Archiv` und `2026\Smoke-Umbenannt` (beim nächsten Lauf vorab entfernt)
— deshalb immer gegen eine Kopie laufen lassen.

Spike- und Benchmark-Harness: `dotnet run --project tests/PhotoViewerV3.PerfTests -c Release -- <spike>` (siehe
[docs/spikes/](docs/spikes/)).

## Projektstruktur

```
PhotoViewerV3.slnx                 # Solution (.slnx)
Directory.Packages.props           # Central Package Management inkl. transitiver Security-Pins
src/
  PhotoViewerV3.Domain/            # Entities, Enums — keine Abhängigkeiten
  PhotoViewerV3.Data/              # LibraryContext (EF Core), EntitiesMapping, Migrations,
                                   # RawSql (BulkScanWriter, FtsIndexer, FtsQueryBuilder)
  PhotoViewerV3.Services/          # WPF-frei: Catalog, Database, Infrastructure (AppDataPaths), Library, Media
                                   # (Formatregister), Messaging, Operations, Scanning, Sorter, Tags, Thumbnails, Archive;
                                   # AddPhotoViewerServices() + AddXyz() je Modul
  PhotoViewerV3.Wpf/               # App.xaml.cs (Host + DI), MainWindow (NavigationView);
                                   # Module Folders, Gallery, Library, Lightbox, Settings, Shell (+ Smoke), Sorter,
                                   # Thumbnails (WIC-Renderer)
tests/
  PhotoViewerV3.Tests/             # xUnit: Services + Data (256 Tests)
  PhotoViewerV3.Wpf.Tests/         # xUnit: WPF-gebundener Code (97 Tests)
  PhotoViewerV3.PerfTests/         # Konsolen-Harness der Spikes (SQLite-Bulk, WIC, FFmpeg, Storage, V0.1-Scan)
docs/adr/                          # Architekturentscheidungen ADR-0001 … ADR-0021
docs/spikes/                       # Messungen der sechs Spikes und der Real-Welt-Scan-Validation
mockups/                           # HTML-Design-Vorlagen (Fluent hell/dunkel, Lightbox)
chatgpt-review/                    # Review-Runden zum Plan
PLAN.md · CHANGELOG.md · CLAUDE.md # Implementierungsplan, Änderungsprotokoll, Projektprofil für Claude
```

Schichten: Domain ← Data ← Services ← Wpf; keine WPF-Referenz in Domain/Data/Services. Module sprechen über
Services-Interfaces und Nachrichten (`WeakReferenceMessenger`), nie über fremde ViewModels (ADR-0017).

## Weiterführende Dokumentation

- [PLAN.md](PLAN.md) — Vision, Stack, Schema, Scope und Roadmap (mit Status 0.2.0)
- [docs/adr/README.md](docs/adr/README.md) — Architekturentscheidungen (ADR-0001 … ADR-0019)
- [docs/spikes/](docs/spikes/) — Spike-Ergebnisse (SQLite-Bulk/FTS5, Virtual Grid, LibVLC, WIC, FFmpeg, Storage)
- [CHANGELOG.md](CHANGELOG.md) — Änderungen je Version und offene Punkte
- [CLAUDE.md](CLAUDE.md) — Projektprofil und Repo-Regeln für die Arbeit mit Claude

## Lizenz

Privates Projekt von Herbert Schrotter — nicht zur Weitergabe bestimmt, alle Rechte vorbehalten. Verwendete
Open-Source-Komponenten (WPF-UI, CommunityToolkit.Mvvm, EF Core, LibVLCSharp/libVLC, Serilog, xUnit, FluentAssertions)
unterliegen ihren jeweiligen Lizenzen; FFmpeg wird nicht mitgeliefert, sondern lokal installiert.

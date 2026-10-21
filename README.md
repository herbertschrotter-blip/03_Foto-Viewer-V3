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
- **Kategorien**: eigene Kategorien mit festen Werten (Seite „Kategorien“), ein Wert je Datei, Zuweisen per
  Kontextmenü oder Strg+K (auch in der Lightbox), Filter, Suche nach Werten, je Kategorie eine Listenspalte, die die
  ganze Trefferliste sortiert.
- **Lightbox**: eigenes Fenster (auch auf einem zweiten Monitor) für Bilder mit Zoom/Pan und Info-Panel sowie Videos
  über LibVLC mit Transportleiste; Favorit, Bewertung, Papierkorb, „Im Explorer anzeigen“, „Mit Standardprogramm
  öffnen“.
- **Archive** (0.2.8): ZIP, RAR, 7z, TAR, GZ/TGZ, BZ2 und XZ erscheinen als Kacheln im Katalog (Icon, Endung, Größe;
  Filter „Archive“); die Lightbox zeigt ihren Inhalt, „Entpacken…“ legt die Dateien neben das Archiv und nimmt sie
  sofort in den Katalog auf (siehe [Archive](#archive)).
- **Ordner**: Ordnerbaum der Bibliothek mit Dateizählern, rechts die Galerie des gewählten Ordners (mit oder ohne
  Unterordner); ganze Ordner bewerten, als Favorit markieren, kategorisieren oder taggen.
- **Eingang und Bibliotheksordner** (0.2.32): unsortierte Quellordner als Eingang aufnehmen, ansehen und bewerten, dann
  per **In die Bibliothek übernehmen…** samt Unterordner-Struktur in den Bibliotheksordner verschieben — leere
  Zwischenordner werden zusammengezogen, Bewertungen wandern mit, alles mit einem Rückgängig umkehrbar.
- **Sortieren**: Pattern-basierter File Sorter (Regex-Profile, Multi-Level, Vorschau, Import/Export der V2-Profile)
  und Flatten & Move — jede Verschiebung läuft protokolliert über die Datei-Operationen und ist rückgängig machbar.
- **Einstellungen**: Speicherorte (Katalog samt Vorschaubildern umziehbar), Thumbnail-Cache, FFmpeg-Status, unterstützte Formate, Farbschema (Hell/Dunkel/System),
  Version.
- **Statusleiste**: Scan-Fortschritt und -Ergebnis, Thumbnail-Warteschlange, Hintergrund-Aktivität, Hinweise
  (Bibliothek offline, unvollständige Dateivorgänge, Datei-Operation wartet auf laufenden Scan — mit **Abbrechen**).
- **Sicherheit der Daten**: Löschen geht immer in den Windows-Papierkorb (nie still hart löschen); Verschieben,
  Umbenennen und Löschen werden in `OperationBatches`/`FileOperations` protokolliert; Move/Rename und Ordner-Übernahmen sind
  rückgängig machbar.

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

Die App-Version (Einstellungen → „Über“, Dateiversion der EXE) kommt aus dem obersten Eintrag in
[CHANGELOG.md](CHANGELOG.md); `PhotoViewerV3.Wpf.csproj` liest sie beim Build aus.

## Portable Version bauen

```powershell
pwsh -File build\publish.ps1
```

Erzeugt per `dotnet publish` (Release, `win-x64`, **self-contained**, kein Single-File, kein Trimming) den Ordner
`artifacts\PhotoViewerV3-<Version>-win-x64\` und daneben dieselbe Version als ZIP. Ordner bzw. ZIP an einen beliebigen
Ort kopieren und `PhotoViewerV3.exe` starten — ein installiertes .NET ist nicht nötig. Das Skript bricht ab, wenn die
Produktversion der EXE nicht zum CHANGELOG passt, SQLite (`e_sqlite3.dll`) bzw. LibVLC (`libvlc\win-x64\`) fehlen oder
Werkzeuge für `dotnet ef` (`BuildHost-*`, `Microsoft.EntityFrameworkCore.Design.dll`) im Paket liegen (seit 0.2.31 nicht
mehr: 734 Dateien, ~253 MB, ZIP ~112 MB).
`artifacts\` ist per `.gitignore` ausgenommen; mit `-OutputRoot <Ordner>` landet das Paket außerhalb des Repos (z. B.
außerhalb von OneDrive), `-NoZip` lässt das ZIP weg, `-FrameworkDependent` baut ein kleineres Paket, das die
„.NET 10 Desktop Runtime“ (x64) voraussetzt.

- **Daten** liegen auch bei der portablen Version unter `%LocalAppData%\PhotoViewerV3` (bzw. im Ordner aus
  `database-location.json`), nicht im Paketordner — ein neues Paket übernimmt Katalog und Vorschaubilder. Die
  Formatregister sind fest im Code, es gibt keine Konfigurationsdateien neben der EXE.
- **FFmpeg** ist nicht im Paket; es wird wie beim Debug-Build im `PATH` bzw. unter den winget-Links gesucht
  (`winget install Gyan.FFmpeg`).
- **Pfadlänge:** Die LibVLC-Plugins liegen bis zu ~120 Zeichen tief im Paket (`PhotoViewerV3-…\libvlc\win-x64\…`);
  Paket bzw. `-OutputRoot` deshalb nicht tiefer als ~130 Zeichen ablegen, sonst scheitern Kopieren/Entpacken am
  260-Zeichen-Limit von Windows.
- **Rauchtest gegen das Paket** (Testbibliothek siehe [Tests](#tests)):
  `pwsh -File build\run-smoke.ps1 -Exe artifacts\PhotoViewerV3-<Version>-win-x64\PhotoViewerV3.exe -TestLib <Testbibliothek>`

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
- Je Bibliothek die Liste ihrer Ordner mit Rolle (siehe „Eingang und Bibliotheksordner“): **Eingang hinzufügen…**,
  je Eingang **In die Bibliothek übernehmen…**, Archiv-Symbol = diesen Ordner zum Bibliotheksordner machen, Umbenennen
  (Anzeigename), Entfernen (nur Eingänge, nur aus dem Katalog). Scannen/Aktualisieren läuft über alle Ordner. Ein
  Kasten „So funktioniert's“ erklärt den Ablauf; die Datenbank enthält nur Verweise und Bewertungen, keine Bilder.

### Eingang und Bibliotheksordner
Eine Bibliothek hat genau einen **Bibliotheksordner** (der beim Anlegen gewählte Ordner — dein sortiertes Fotoarchiv;
im Code „Archiv-Wurzel“) und beliebig viele **Eingänge** (unsortierte Quellordner: Kamera-Import, Handy-Upload,
Downloads, alte Ordnersammlungen). Die Datenbank (Katalog) enthält nur Verweise auf die Dateien samt Bewertungen,
Favoriten, Tags und Kategorien — keine Bilder.

**Ablauf:** Eingang hinzufügen → ansehen, bewerten, kategorisieren (einzelne Dateien oder ganze Ordner) → Ordner in die
Bibliothek übernehmen.
- **Bibliothek → Eingang hinzufügen…**: Ordner wählen (darf nicht in oder über einem anderen Ordner der Bibliothek
  liegen), danach „Jetzt aktualisieren“ nimmt die Dateien in den Katalog auf.
- **Galerie**: Dropdown **Status** mit **Noch im Eingang** (alles in Eingängen) und **In der Bibliothek**; Kacheln im
  Eingang tragen das Badge „Eingang“, im Bibliotheksordner „Bibliothek“. Der Ordnerbaum zeigt die Wurzeln als oberste
  Knoten mit Rolle.
- **Ganze Ordner bewerten**: Rechtsklick auf einen Ordner im Ordnerbaum → **Bewertung für alle Dateien** (1–5 Sterne
  oder entfernen), **Favorit für alle**, **Kategorie für alle…** (Kategorie-Auswahl wie Strg+K), **Tags für alle…**
  (Tag-Auswahl wie Strg+T). Wirkt auf alle Dateien darunter (mit Unterordnern), nach einer Bestätigung mit Anzahl.
- **In die Bibliothek übernehmen…** (Rechtsklick auf einen Ordner unter einem Eingang oder auf den Eingang selbst; auf
  der Bibliotheksseite je Eingang): Dialog mit dem Ordnerbaum des Bibliotheksordners (Suche, **Neuer Ordner**,
  zuletzt benutzter Zielordner vorgewählt) und einer **Vorschau** des Ergebnisbaums (alt → neu, Dateien je Ordner,
  Hinweise auf zusammengezogene, zusammengeführte und umbenannte Einträge). Der Ordner landet samt Unterordner-Struktur
  im gewählten Zielordner. Optionen:
  - **Leere Zwischenordner zusammenziehen** (Standard an): ein Ordner ohne Dateien mit genau einem Unterordner entfällt,
    der nächste rückt nach, bis zum ersten Ordner mit Dateien oder mehreren Unterordnern — dessen Name bleibt.
    Beispiel `Fotos\leer\leer\leer\inhalt` → `Fotos\inhalt`; auch der übernommene Ordner selbst (`Import\leer\inhalt` →
    `<Ziel>\inhalt`). Verzweigungen bleiben vollständig erhalten; leere Ordner werden nicht angelegt.
  - **Quellordner danach in den Papierkorb** (Standard an): nur, wenn danach keine Datei mehr darin liegt — sonst bleibt
    er und ein Bericht nennt die Reste. Systemordner wie `.thumbs`, `.cache`, `@eaDir` und `Thumbs.db` werden nicht
    übernommen und gehen mit in den Papierkorb.
  - **Eingang danach austragen** (nur bei einem ganzen Eingang, Standard an; nur aus dem Katalog). Ohne Austragen
    bleibt der Eingangsordner leer bestehen, nur sein Inhalt kommt in den Papierkorb.
  Vorhandene Zielordner werden zusammengeführt, gleichnamige Dateien bekommen „_1“, „_2“ …. Übernommen werden alle
  Dateien, auch .xmp oder .txt. Bewertungen, Favoriten, Tags, Kategorien und Vorschaubilder wandern mit (gleiche
  FileId). **Rückgängig** (Galerie) holt alles zurück: Dateien an die alten Pfade, die Ordner entstehen neu, leere
  Zielordner verschwinden, ein ausgetragener Eingang wird wieder eingetragen (Name = Ordnername).
- **In die Bibliothek…** für einzelne Dateien (Aktionsleiste, **Strg+M**, Kontextmenü der Kachel, in der Lightbox
  **Strg+M**): Dialog mit dem Ordnerbaum des Bibliotheksordners (auch Ordner, die nur auf der Platte existieren),
  Suchfeld, **Neuer Ordner…**, **Umbenennen…**, Vorschlag „Jahr\Jahr-Monat“ aus Aufnahme- bzw. Dateidatum; der zuletzt
  benutzte Zielordner ist vorgewählt. Die Dateien landen flach im gewählten Ordner. Verschoben wird protokolliert
  (Rückgängig wie bei „Verschieben nach…“); Favoriten, Bewertungen und Vorschaubilder bleiben. Die Ansicht bleibt beim
  Filter und zeigt die restlichen Dateien.
- Einen Eingang zu entfernen löscht nur seine Katalogdaten — die Dateien auf der Platte bleiben.
- Nicht verwechseln: **Archive** (ZIP/RAR/7z — Filter „Archive“, „Entpacken…“, automatisches Entpacken) sind Dateien,
  der Bibliotheksordner ist ein Ordner.

### Galerie
Toolbar: Filter-Pills als Segmentgruppe (Alle / Bilder / Videos / Favoriten / Archive), Dropdown **Status** (Noch im Eingang /
In der Bibliothek), Tag-Filter, Suchfeld (Dateiname, Endung, Tag-Name, Kategorie-Wert oder Kategorie-Name; Wörter werden
als Präfix gesucht, Sonderzeichen sind erlaubt; ✕ leert die Suche), Sortierung, Seitengröße, **Vorschaugröße** (Regler neben
der Ansichtsumschaltung, nur in der Kachelansicht: Kachelbreite 120–320 px in 20er-Stufen, wird gemerkt), Pager, Aktualisieren. In schmalen
Fenstern (bzw. rechts auf der Ordner-Seite) rutscht die Suche in eine eigene Zeile, statt gequetscht zu werden. Ein Ordnerfilter erscheint als
Chip und lässt sich dort wieder aufheben. Kacheln: Herz = Favorit, Sterne = Bewertung, Badge „fehlt“ bei Dateien, die
beim letzten Scan nicht mehr auf der Platte lagen. Ordner ohne direkte Dateien zeigen ihre Unterordner als Kacheln.
**Alle auswählen** (Knopf rechts in der Kopfzeile neben „Rückgängig“, wie Strg+A) wählt alle Dateien der aktuellen
Seite. Bei einer Auswahl erscheint unten die Aktionsleiste (Favorit setzen/entfernen, Bewertung, Verschieben nach…,
In die Bibliothek…, Papierkorb, Alle auswählen, Auswahl aufheben); **Rückgängig** nimmt die letzte Verschiebung/Umbenennung (auch eine
Ordner-Umbenennung oder eine Ordner-Übernahme) zurück.

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
| Strg+M | Auswahl in die Bibliothek… |
| Strg+T | Tags der Auswahl bearbeiten (Tag-Auswahl) |
| Strg+K | Kategorie-Werte der Auswahl zuweisen (Kategorie-Auswahl) |
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

**Strg+K** (oder Rechtsklick → „Kategorien zuweisen…“) öffnet die Kategorie-Auswahl für die ausgewählten Dateien: je
Kategorie eine Auswahlliste mit den Werten und „Kein Wert“; haben die Dateien unterschiedliche Werte, steht dort
„(gemischt — nicht ändern)“, und die Kategorie bleibt unverändert, solange man nichts anderes wählt. Ohne Maus: Tab
wechselt die Kategorie, ↑/↓ den Wert, Enter übernimmt, Esc bricht ab. Die **Suche** findet auch Dateien, deren
Kategorie-Wert den Suchtext enthält (wie bei Tag-Namen, ohne Groß-/Kleinschreibung); trifft der Suchtext den Namen
einer Kategorie (z. B. „Projekt“), findet sie alle Dateien, die in dieser Kategorie irgendeinen Wert haben.

#### Listenansicht
Der Umschalter rechts in der Kopfzeile wechselt zwischen Kachel-Grid und Liste (die Wahl bleibt gespeichert). Die
Liste zeigt dieselbe Seite mit den Spalten Name (mit Vorschaubild), Ordner, Geändert, Größe, Typ, Bewertung (Sterne
klickbar), Favorit (Herz klickbar), Tags, Status und je Kategorie eine Spalte (neue Kategorien erscheinen am Ende).
Rechtsklick auf eine Spaltenüberschrift blendet Spalten ein/aus,
Ziehen ändert die Reihenfolge — beides bleibt gespeichert. Klick auf Name, Geändert oder Größe sortiert die ganze
Trefferliste (wie die Sortier-Auswahl). Klick auf eine Kategorie-Spalte sortiert ebenfalls die ganze Trefferliste:
Werte in ihrer Reihenfolge von der Seite **Kategorien**, erneuter Klick kehrt um; Dateien ohne Wert stehen immer am
Ende, gleiche Werte nach Datum neu → alt. Die Sortier-Auswahl zeigt das als „… (Kategorie) ↑/↓“; eine andere
Sortierung dort hebt es wieder auf. Die übrigen Spalten sortieren nur die geladene Seite. Auswahl, Aktionsleiste,
Doppelklick/Enter (Lightbox) und Tastenkürzel wie im Grid.

### Lightbox
Rahmenloses Fenster, Toolbar und Titelleiste blenden sich nach kurzer Zeit aus (Mausbewegung oder Taste blendet sie
ein). Bilder: Mausrad = Zoom am Cursor, Ziehen = Verschieben, Doppelklick = Einpassen ↔ 100 %. Videos: Transportleiste
mit Position, Wiedergabe/Pause, Lautstärke, Stumm. Das Info-Panel zeigt Datei- und Medieninfos (Auflösung, Dauer,
Codec) und, sobald es Kategorien gibt, je Kategorie den Wert der Datei („—“ ohne Wert) mit dem Knopf **Zuweisen…**.
Gesetzte Kategorie-Werte stehen außerdem in der Titelleiste (z. B. „Projekt: Nord · XX: N“).

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
| Strg+M | In die Bibliothek… |
| Strg+T | Tags der Datei bearbeiten (die Titelleiste zeigt die Tags) |
| Strg+K | Kategorie-Werte der Datei zuweisen (dieselbe Auswahl wie in der Galerie) |
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
- Entpackt wird in der App über SharpCompress (keine Installation von 7-Zip/WinRAR nötig, ADR-0022).
- **Automatisch entpacken** (0.2.25, ADR-0032; **je Bibliothek** seit 0.2.31, ADR-0033; Standard aus): Einstellungen →
  **Archive** → „Neue Archive in Eingangsordnern automatisch entpacken“, optional „Archiv nach fehlerfreiem Entpacken in
  den Papierkorb verschieben“ — beide Schalter gelten für die **aktive Bibliothek** (der Abschnitt nennt sie); für eine
  andere Bibliothek diese zuerst auf der Bibliotheksseite aktivieren. Eine bis 0.2.30 global eingeschaltete Einstellung
  übernehmen beim ersten Start alle bestehenden Bibliotheken; neue Bibliotheken starten mit „aus“.
  Findet „Aktualisieren“ oder die Ordnerüberwachung ein **neues** Archiv in einem **Eingangsordner**, entpackt die App es
  nach dem Scan vollständig (alle Dateien, verschachtelte Archive bis 3 Ebenen) in einen Ordner mit dem Archivnamen
  daneben und aktualisiert die Bibliothek erneut. Unberührt bleiben: Archive im Archivordner, Archive, die schon vor dem
  Einschalten im Katalog standen, Archive in bereits entpackten Ordnern und Archive, deren Zielordner schon existiert.
  Passwortgeschützte oder beschädigte Archive bleiben liegen; die Statusleiste zeigt Fortschritt und Ergebnis, gemeldete
  Archive mit Grund im Tooltip.
- **Vorhandene Archive in Eingängen jetzt entpacken…** (Knopf im selben Abschnitt, nur bei eingeschaltetem Schalter):
  holt das Entpacken für Archive nach, die schon vor dem Einschalten im Katalog standen — nennt die Anzahl, fragt nach
  und entpackt wie die Automatik (Zielordner neben dem Archiv, Papierkorb nach Einstellung, danach ein Aktualisieren).
  Archive, deren Zielordner schon existiert (z. B. früher manuell entpackt), zählen nicht.

### Ordner
Links der Ordnerbaum der aktiven Bibliothek (Dateizähler direkt / gesamt), rechts die Galerie. Umschalter „mit
Unterordnern“; „Gesamte Bibliothek“ hebt den Ordnerfilter auf. Nach Scans und Datei-Operationen lädt der Baum neu und
behält Aufklapp-Zustand und Auswahl. Oberste Knoten sind die Wurzeln mit Rolle (Bibliotheksordner, Eingang). Kontextmenü:
**Neuer Ordner…** (Ordner auf der Platte + Katalogzeile), **Umbenennen…** (nicht für Wurzeln; Ordner mit Dateien
werden protokolliert umbenannt und lassen sich mit Rückgängig zurücknehmen), **In die Bibliothek übernehmen…** (nur
unter einem Eingang, siehe „Eingang und Bibliotheksordner“) sowie **Bewertung für alle Dateien** (1–5 Sterne oder
entfernen), **Favorit für alle**, **Kategorie für alle…** und **Tags für alle…** — wirken auf alle Dateien des Ordners
samt Unterordnern, nach einer Bestätigung mit Anzahl. Das Ergebnis steht über dem Baum (✕ blendet es aus).

### Sortieren
- **Quelle und Ziel** (beide Bereiche): je eine Auswahl der Wurzeln der aktiven Bibliothek mit Rolle („Eingang“ /
  „Bibliotheksordner“) und Pfad, darunter ein frei wählbarer Ordner. Vorbelegt ist der erste erreichbare Eingang als Quelle und
  den Bibliotheksordner als Ziel — der Normalfall „aus dem Eingang in die Bibliothek sortieren“. Als Ziel geht auch
  „Quellordner“ (die Ordner entstehen an Ort und Stelle). Offline-Wurzeln sind ausgegraut; Ziele außerhalb aller
  Wurzeln sind gesperrt, weil die Dateien sonst aus dem Katalog fielen und Bewertung, Favorit und Tags verlören
  (ADR-0024). Die Auswahl folgt Bibliothekswechseln und geänderten Wurzeln. Verschobene Dateien behalten ihre FileId
  und wechseln im Katalog zur Zielwurzel (Galerie-Filter „Noch im Eingang“ / „In der Bibliothek“).
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

### Gesichtserkennung (Teil 1)
Erkennt im Hintergrund Gesichter in den Fotos der aktiven Bibliothek und speichert je Gesicht Position, Qualität und
einen Fingerabdruck (512 Werte) im Katalog (ADR-0035) — vollständig auf diesem PC. Personen benennen, Vorschläge und
Filter nach Personen folgen in Teil 2.
- **Einstellungen → Gesichtserkennung**: „Gesichter im Hintergrund erkennen“ (Standard **aus**), „Grafikkarte
  verwenden“ (Standard an; DirectML, bei Problemen automatisch Prozessor), Modellstatus, **Index neu aufbauen**
  (analysiert alle Fotos erneut; bestätigte Zuordnungen bleiben).
- **Modell**: InsightFace *buffalo_l* (Erkennung SCRFD `det_10g.onnx`, Fingerabdruck ArcFace `w600k_r50.onnx`). Fehlt
  es, steht dort **„Modell herunterladen… (275 MB, InsightFace buffalo_l, nur nicht-kommerzielle Nutzung)“**: nach
  Bestätigung lädt PhotoViewer das Paket von GitHub (InsightFace Release v0.7), prüft die SHA-256-Prüfsumme und entpackt
  nur die zwei benötigten Dateien nach `%LocalAppData%\PhotoViewerV3\models\buffalo_l\` (Fortschritt, abbrechbar). Ohne
  Klick wird nichts geladen. Die Modelle sind nur für die nicht-kommerzielle Nutzung freigegeben und liegen nie im Repo
  oder im portablen Paket.
- **Statusleiste**: „Gesichter: 1.234 / 19.556 · 37 ms/Foto“ mit **Pause**/**Fortsetzen** und **Abbrechen** (bis zum
  nächsten Scan oder Neustart). Der Index startet 10 s nach dem App-Start, nach jedem Scan und bei neuen Dateien,
  arbeitet die neuesten Fotos zuerst ab und läuft mit niedriger Priorität.
- Videos werden noch nicht analysiert; Fotos, die WIC nicht dekodieren kann (z. B. HEIC ohne Codec), werden als
  „nicht dekodierbar“ vermerkt. „Neu aufbauen“ der Bibliothek behält alle Gesichter (kein erneutes Analysieren).

### Personen
Gefundene Gesichter benennen; PhotoViewer lernt daraus und schlägt die Personen auf weiteren Fotos vor (ADR-0036/0037).
Gelernt wird **nur aus dem, was du bestätigst** — eigene Vorschläge lernen nie mit.
1. **Einschalten:** Einstellungen → Gesichtserkennung → „Gesichter im Hintergrund erkennen“ (einmalig das Modell
   herunterladen). Die Statusleiste zeigt den Fortschritt; die Seite **Personen** zeigt oben, wie viele Fotos schon
   analysiert sind, und weist darauf hin, wenn die Erkennung aus ist („Einstellungen öffnen“).
2. **Gruppen benennen:** Unter „Unbenannte Gruppen“ stehen Gesichter, die vermutlich dieselbe Person zeigen (größte
   zuerst). Eine Gruppe öffnen: die Gesichter sind nach Ähnlichkeit sortiert; **zweifelhafte stehen am Ende, sind rot
   markiert („Ausreißer?“) und abgewählt**. Klick wählt ein Gesicht ab oder an, Doppelklick öffnet das Foto.
   - **Als neue Person benennen…** — Name eingeben; nur die ausgewählten Gesichter werden bestätigt.
   - **Zu Person hinzufügen…** — Person aus der Liste wählen (ähnelt die Gruppe einer bekannten Person, ist sie
     vorgewählt).
   - **Kein Gesicht / ignorieren** — Fehlerkennungen; sie erscheinen nirgends mehr.
   Passt ein ausgewähltes Gesicht nicht gut zur Person (bzw. ähnelt es einer anderen bekannten Person), fragt
   PhotoViewer nach: **Ohne diese bestätigen** / **Trotzdem alle bestätigen** / **Abbrechen**.
3. **Vorschläge prüfen:** Nach dem Benennen lernt PhotoViewer im Hintergrund (wenige Sekunden) und schlägt die Person
   auf weiteren Fotos vor. Person öffnen → Reiter **Vorschläge**: je Gesicht ✓ (bestätigen) oder ✗ (ablehnen), oder
   mehrere auswählen bzw. **Alle sichtbaren bestätigen/ablehnen** (die gerade auf dem Bildschirm stehen). Abgelehnt
   heißt „das ist nicht diese Person“ — sie wird für dieses Gesicht nie wieder vorgeschlagen, der nächstbeste Kandidat
   rückt sofort nach.
4. **Korrigieren:** Reiter **Bestätigt** → falsche Gesichter auswählen → **Aus Person entfernen** (danach wird ohne sie
   neu gelernt), **Als Titelbild**. Oben: **Umbenennen…**, **Zusammenführen mit…** (z. B. zwei Einträge derselben
   Person), **Ausblenden** (aus der Übersicht; „Ausgeblendete zeigen“ holt sie zurück), **Löschen…** (die Gesichter
   bleiben und sind wieder unbenannt).
- Personen gelten für alle Bibliotheken, Zähler und Vorschläge für die aktive. Neue Gesichter aus dem laufenden Index
  erscheinen in den Gruppen nach **Aktualisieren**.
- Die Gesichts-Ausschnitte (128 × 128) liegen im Vorschaubild-Ordner (`thumbs\<Bibliothek>\faces\`) und werden nur für
  sichtbare Kacheln erzeugt.

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
- Eigene Datei-Vorgänge der App (Verschieben, „In die Bibliothek…“, Ordner übernehmen, Papierkorb) buchen den Katalog selbst; die Überwachung sieht
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
Datei-Operation (Verschieben, Papierkorb, In die Bibliothek, Ordner übernehmen, Umbenennen, Ordner anlegen/umbenennen, Rückgängig, Sortierer-Lauf)
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
| `models\buffalo_l\` | Nur nach dem Download in den Einstellungen: Modelle der Gesichtserkennung (`det_10g.onnx`, `w600k_r50.onnx`); bleibt beim Umzug des Katalogs hier |

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
| `PHOTOVIEWERV3_FACE_MODELS` | Ordner mit `det_10g.onnx` und `w600k_r50.onnx` statt `models\buffalo_l\` (Entwicklung, Tests; dann kein Download). Tests mit echten Modellen laufen nur mit dieser Variable |
| `PV3_FACE_SAMPLE_DIR` | Nur für die lokale Messung (`FaceRecognitionMeasurementTests`): Ordner mit Fotos, aus dem eine Stichprobe gelesen wird; Ausgabe nur zusammengefasste Zahlen |

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

Dasselbe als Skript gegen eine beliebige EXE (Debug-Build oder portables Paket; kopiert die Testbibliothek nach
`%TEMP%\PhotoViewerV3-smoke-<Tag>\lib`, eigenes AppData daneben, ExitCode 0 = alle Schritte OK):

```powershell
pwsh -File build\run-smoke.ps1 -Exe src\PhotoViewerV3.Wpf\bin\Debug\net10.0-windows\PhotoViewerV3.exe -TestLib <Testbibliothek>
```

Die Testbibliothek sollte Bilder (JPG/PNG), Videos (MP4/MOV), einen Ordner ohne direkte Dateien mit zwei
Unterordnern („Leer\Sub A“, „Leer\Sub B“), einen Ordner „Unsortiert“ mit mehreren JPGs sowie die Ordner
„2024\Familie“ und „2025\Screenshots“ enthalten (das Szenario prüft diese Namen) — insgesamt mindestens sechs
Mediendateien, damit der Mass-Missing-Guard beim Incremental-Schritt nicht greift. Der Schritt legt
`Unsortiert\smoke-neu.jpg` an (bleibt liegen, wird beim nächsten Lauf vorab entfernt); der Archiv-Schritt legt
`Unsortiert\smoke-archiv.zip` an, entpackt es nach `Unsortiert\smoke-archiv\` und verschiebt das ZIP in den
Windows-Papierkorb (der Ordner wird beim nächsten Lauf vorab entfernt). Die Ordnerüberwachung ist während des Laufs
ausgeschaltet und wird erst im letzten Schritt (Ruhezeit 1 s, `Unsortiert\smoke-watch.jpg` kopieren und löschen)
eingeschaltet. Der Schritt „Auto-Entpacken“ davor legt den Geschwisterordner `<Bibliothek>-Auto-Eingang` als Eingang an,
schaltet das automatische Entpacken für die Bibliothek ein und am Ende wieder aus und entfernt Eingang und Ordner wieder.
Der Schritt „Eingang → Archiv“
legt den Geschwisterordner `<Bibliothek>-Eingang` als Eingangs-Wurzel an (wird am Ende wieder entfernt) und hinterlässt
in der Bibliothek die leeren Ordner `2026\Smoke-Archiv` und `2026\Smoke-Umbenannt` (beim nächsten Lauf vorab entfernt).
Der Schritt „Ordner übernehmen“ legt den Geschwisterordner `<Bibliothek>-Uebernahme-Eingang` als Eingang an, übernimmt
ihn nach `2026\Smoke-Uebernahme` (der Quellordner geht dabei in den Windows-Papierkorb), macht das mit Rückgängig wieder
rückgängig und entfernt Eingang und Ordner am Ende — deshalb immer gegen eine Kopie laufen lassen.

Spike- und Benchmark-Harness: `dotnet run --project tests/PhotoViewerV3.PerfTests -c Release -- <spike>` (siehe
[docs/spikes/](docs/spikes/)).

## Projektstruktur

```
PhotoViewerV3.slnx                 # Solution (.slnx)
Directory.Packages.props           # Central Package Management inkl. transitiver Security-Pins
build/                             # publish.ps1 (portable Version), run-smoke.ps1 (Rauchtest gegen eine beliebige EXE)
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

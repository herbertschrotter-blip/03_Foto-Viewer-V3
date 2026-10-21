# ADR-0051: Orte — Weltkarte mit Foto-Gruppen, eigene Kachelkarte und hierarchisches Raster

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0017](0017-modul-entkopplung-messenger.md) (Messenger), [ADR-0037](0037-gesichter-zuordnen-personen-seite.md)
  (Kachel-Listen als Blatt-Peers), [ADR-0042](0042-navigation-und-einstellungen-mit-reitern.md) (Navigation),
  Vertrag „Orte“ zwischen Agent V (Daten: Koordinaten, GPS lesen/schreiben, Geocoder, Galerie-Filter) und Agent W (Karte)

## Kontext

Herbert will seine Fotos auf einer Weltkarte sehen: gruppiert, je näher gezoomt desto feiner, bis zu einzelnen Fotos;
Start im Bereich, wo die Fotos des aktuellen Bereichs gemacht wurden (plus Rand), hinauszoomen bis zur Welt. Fotos ohne
Koordinaten sollen sich auf der Karte verorten lassen (Punkt wählen, Ziehen aus einer Seitenleiste „Ohne Ort“). Kataloge
haben bis 500 000 Dateien; der Alltag läuft im Debug-Build. Kartenkacheln von OpenStreetMap hat Herbert freigegeben —
unter deren Nutzungsregeln (User-Agent, Cache, max. 2 parallele Abrufe, keine Massenabrufe, Zuordnung sichtbar).
WebView2 ist ausgeschlossen; erlaubt war `Mapsui.Wpf` oder eine begründete leichtere Eigenlösung. Harte Regeln:
Bibliotheks-Trennung (nur der aktuelle Bereich), versteckte Bibliotheken (Ablage außerhalb des Katalogs umbiegbar,
Hinweis, dass der Kartenserver den Ausschnitt sieht), Galerie-Kacheln/Marker keine großen UI-Automation-Bäume.

## Entscheidung

1. **Neue Seite „Orte“** (`Wpf/Places`, Navigation zwischen Personen und Sortieren, Symbol `Map24`): Kopfzeile wie die
   Galerie (Titel, „1.234 verortete Fotos · 56 ohne Ort“, Ortssuche über `IReverseGeocoder.Search`), Karte, Seitenleiste
   „Ohne Ort“ (TileListBox + VirtualizingTilePanel, Mehrfachauswahl mit Strg/Umschalt, Ziehen auf die Karte).
2. **Eigene Kachelkarte** `MapControl` statt Mapsui: ein `FrameworkElement`, das in `OnRender` Kacheln (Web-Mercator,
   fehlende aus gröberen Stufen hochskaliert, ohne Kacheln ein graues Raster) und Marker zeichnet; Ziehen, Mausrad,
   Doppelklick, Tasten; Marker sind keine Elemente (Karte = ein Blatt im UI-Automation-Baum). Ausschnitt-Modell
   `MapViewport` ohne WPF (testbar): Start = Ausdehnung der Fotos plus Rand (10 %, mindestens 80 px, höchstens Zoom 16),
   ohne verortete Fotos die Welt.
3. **Gruppen als hierarchisches Raster** (`Services/Places/Clustering/GeoClusterIndex`, WPF-frei): Fotos einmal nach
   Morton-Code (2 × 30 Bit ≈ 4 cm) sortieren, gleiche Codes = ein Aufnahmeort. Eine Rasterzelle der Zoomstufe z (64 px)
   ist ein Code-Präfix = ein zusammenhängender Bereich der Sortierung; vorberechnet nur Präfixsummen (Anzahl,
   Schwerpunkt) und ein Segmentbaum für das **beste Foto** (höchste Bewertung, sonst neuestes nach Aufnahme-, sonst
   Änderungsdatum). Abfrage = belegte Zellen des Ausschnitts (je zwei binäre Suchen), danach Marker näher als 56 px
   zusammenlegen (größte zuerst) — gegen Überlappen an Zellgrenzen. „Zerfalls-Zoom“ einer Gruppe = erste Stufe, auf der
   ihre Zellen nach dem Zusammenlegen ≥ 2 Marker ergeben; zerfällt sie nie (alle am selben Ort), öffnet ein Klick die
   Lightbox mit ihren Fotos.
4. **Bedienung:** Klick auf Gruppe → hineinzoomen bis sie zerfällt; Doppelklick bzw. „In der Galerie zeigen“ →
   `ShowFilesOnMapSelectionMessage(Bounds, FileIds)` (Vertrag; Empfänger = Galerie von Agent V) + Wechsel zur Galerie;
   Klick auf Einzelfoto → Lightbox (Fotos im Ausschnitt, neueste zuerst). Verorten: Ablegen auf der Karte oder
   Rechtsklick „Hier verorten (n Fotos)“ → Bestätigung mit Ortsname (`IReverseGeocoder.Nearest`) und „Änderungsdatum
   beibehalten“ (Standard an) → `IGeoTagService.SetLocationAsync`. `IMapPicker` (Dialog „Ort setzen…“ von V) =
   `MapPickerWindow` (Klick setzt Nadel, Ortssuche).
5. **Kacheln:** `OsmTileSource` mit User-Agent „Lumora/&lt;Version&gt; (private use)“, Semaphore 2, Platten-Cache
   `<CacheRoot>\maptiles\z\x\y.png` über **`ICatalogStoragePaths`** (neu, `Services/Infrastructure`, heute
   `%LocalAppData%\Lumora\cache`; Agent U biegt ihn im versteckten Modus um, leer = nur Speicher). Ablaufzeit aus
   `Cache-Control`/`Expires`, mindestens 7 Tage; abgelaufene Kacheln sofort zeigen und einmal neu holen; Grenze 500 MB,
   darüber LRU nach Zugriffszeit auf 90 %. `MapTileProvider` lädt nur die gerade sichtbaren Kacheln (Warteschlange wird
   bei jeder Bewegung ersetzt), Speicher-LRU 384 Kacheln. Netzfehler → 30 s nur Cache („Offline — Karte aus dem
   Cache“), Gruppen funktionieren weiter. Abschaltbar mit `LUMORA_MAP_TILES=off`; im Rauchtest und in Tests immer
   aus (keine OSM-Abrufe). Zuordnung „© OpenStreetMap-Mitwirkende“ unten rechts, immer sichtbar.
6. **Trennung & Datenschutz:** Punkte, Medien und Zählungen nur aus `IPlacesScope.GetLibraryIdsAsync` (heute aktive
   Bibliothek; Agent T stellt auf Verknüpfungsgruppen um), Bibliothekswechsel lädt neu und setzt den Start-Ausschnitt
   neu; Verorten nimmt nur Dateien des Bereichs. Hinweis-Leiste im versteckten Modus (`IMapPrivacyContext`, Agent U →
   `ICatalogContext.IsHidden`), wenn Kacheln aus dem Netz kommen. Logs nur Zahlen.

## Begründung

- **Eigene Karte statt Mapsui:** Mapsui 5 bringt SkiaSharp (native DLLs, mehrere MB), NetTopologySuite und BruTile mit
  und rendert in eine eigene Skia-Fläche — Vorschaubilder als Marker, Drag&amp;Drop, Kontextmenü, Theme-Pinsel und der
  kleine Automation-Baum hätten Adapter gebraucht. Gebraucht werden nur Web-Mercator-Kacheln, Pan/Zoom und Marker: rund
  700 Zeilen eigener Code, keine neuen Pakete, Kachelquelle in Tests ohne Netz austauschbar.
- **Raster statt Supercluster:** Der erste Entwurf (Supercluster-Prinzip: gieriges Zusammenfassen im Radius je Stufe,
  Hash-Raster als Nachbarsuche) brauchte für 500 000 gehäufte Punkte 5,0 s im Debug-Build (1,7 s Release) und je Stufe
  ein volles Feld (bis ~100 MB). Das Morton-Raster braucht eine Sortierung und ≈ 45 Byte je Foto; Zellen schachteln sich
  exakt (Gruppen zerfallen beim Hineinzoomen, springen nie um). Den Nachteil des Rasters (zwei Gruppen dicht an einer
  Zellgrenze) behebt das Zusammenlegen naher Marker je Abfrage (≤ wenige hundert Zellen, linear über ein Hilfsraster).
- **Messung** (Debug-Build, `GeoClusterIndexTests.Messung_500000_Punkte_Aufbau_und_Abfrage`, 360 Ausschnitte 1200 ×
  800 px über Zoom 2–19): 500 000 Fotos in 400 Orten gehäuft — Aufbau **275 ms**, Abfrage Ø **0,14 ms**, max 6,9 ms;
  gleichmäßig über Europa — Aufbau **223 ms**, Ø 0,16 ms, max 3,0 ms. Gefordert waren &lt; 1 s bzw. &lt; 10 ms.

## Konsequenzen

### Positiv
- Karte ohne neue Pakete, ohne Netz testbar; Rauchtest-Schritt „Orte-Karte“ läuft mit abgeschalteter Kachelquelle.
- Gruppen für 500 000 Fotos in Sekundenbruchteilen; jede Bewegung rechnet die Marker synchron neu.

### Negativ
- Kein Weiterlaufen über den 180. Längengrad (die Welt ist ein Quadrat); keine Animationen beim Zoomen.
- Karten-Code (Kacheln, Eingabe) ist eigener Code und muss gepflegt werden.
- Die Seitenleiste „Ohne Ort“ = alle Medien des Bereichs minus verortete (zwei Abfragen); nach dem Zusammenbau kann
  Agent V eine direkte Abfrage `GeoSource`/`Latitude IS NULL` anbieten.

### Neutral
- Bis zum Zusammenbau mit Agent V stehen die Vertragstypen als Platzhalter in
  `Services/Places/PlacesContract.Placeholder.cs`, die Dienste als Sitzungsspeicher in `Services/Places/Placeholders`
  (TryAdd; V's Registrierung hat Vorrang). `IMapPicker` hat Agent W festgelegt (im Vertrag nur erwähnt).
- `ICatalogStoragePaths` ist neu und kann mit gleichnamigen Ansätzen anderer Agenten zusammengeführt werden.

## Alternativen

- **Mapsui.Wpf:** siehe Begründung → verworfen (Gewicht, Adapter für Marker/Drag&amp;Drop/Automation).
- **WebView2 + Leaflet:** vom Auftrag ausgeschlossen (Browser-Komponente, Datenschutz, Gewicht).
- **Supercluster (Radius, je Stufe vorab):** gemessen zu langsam im Debug-Build und speicherhungrig → ersetzt durch das
  Morton-Raster.
- **Kacheln vorab für die Ausdehnung laden:** widerspräche den OSM-Regeln (keine Massenabrufe) → nur sichtbare Kacheln.

## Referenzen

- Code: `src/Lumora.Services/Places/Clustering`, `src/Lumora.Wpf/Places`, `Shell/Smoke/SmokeScenario.Places.cs`
- OSM-Kachel-Nutzungsbedingungen: operations.osmfoundation.org/policies/tiles · Lizenz: openstreetmap.org/copyright

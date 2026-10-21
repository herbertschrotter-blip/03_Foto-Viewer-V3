# ADR-0055: Orte — umschaltbare Kartenstile (CARTO, MapTiler, OSM) und EIN gemeinsamer Filter für Galerie, Ordner und Orte

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0051](0051-orte-weltkarte-mit-foto-gruppen.md) (Kachelkarte, OSM-Quelle, Cache),
  [ADR-0050](0050-orte.md) (Ort-/Kartenausschnitt-Filter), [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md) (Filterkarte),
  [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich), [ADR-0049](0049-versteckte-bibliotheken.md)
  (versteckter Katalog), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md) (intelligente Alben)

## Kontext

Herbert wünscht (2026-10-21) moderne, ruhigere Kartenstile, die Lumoras Farbschema folgen, und dieselbe Filterkarte
auf der Seite „Orte“ wie in Galerie und Ordner — mit **einem** Filterzustand für alle drei Seiten. Bisher zeigte die
Karte nur OpenStreetMap-Standard, und die Karte filterte nicht. CARTO-Basemaps verlangen laut carto.com/basemaps und
den Basemap-Bedingungen einen eigenen, kostenlosen Schlüssel des Nutzers (sonst Wasserzeichen), höchstens 30 Tage
Cache, keine Massenabrufe, Namensnennung „© OpenStreetMap-Mitwirkende, © CARTO“; Satellit nur über MapTiler mit eigenem
Schlüssel. Galerie und Ordner teilten schon ein Singleton-`GalleryViewModel` — damit aber auch Ordnerauswahl,
Sortierung und Ansicht, die Herbert seitengebunden haben will. Keine neuen Pakete, keine Migration.

## Entscheidung

1. **Kartenstile** (`Places/Map/Tiles/MapStyles.cs`): `MapStyle` = Automatisch (Standard), Voyager, Positron, Dark
   Matter, Standard (OpenStreetMap), Satellit. `MapStyleCatalog.Resolve(stil, dunkel, schlüssel)` liefert die konkrete
   Quelle: **Automatisch = hell Positron, dunkel Dark Matter**; CARTO-/Satellit-Stile ohne Schlüssel fallen auf OSM
   zurück und tragen den Hinweis „API-Schlüssel nötig – in den Einstellungen eintragen“ (Automatisch ohne Schlüssel
   still, = bisheriges Verhalten). URLs: `https://basemaps.cartocdn.com/rastertiles/{voyager|light_all|dark_all}/{z}/{x}/{y}.png?key=…`
   (Parametername `key` laut carto.com/basemaps), `https://api.maptiler.com/maps/satellite/256/{z}/{x}/{y}.jpg?key=…`.
   Ohne Schlüssel baut `BuildUrl` keine URL — es gibt keinen unbeschlüsselten CARTO-/MapTiler-Abruf.
2. **Umschaltbare Quelle** `StyledMapTileSource` (Singleton `IMapTileSource`): löst bei jeder Änderung von Stil,
   Schlüssel (`IMapStyleService`) oder Farbschema (`IMapThemeSource` ← `ApplicationThemeManager.Changed`) neu auf und
   tauscht die Netz-Quelle `HttpMapTileSource` (verallgemeinerte OSM-Quelle aus ADR-0051) aus; `MapTileProvider` verwirft
   dann Speicher-Kacheln (Generationszähler) und fordert den zuletzt sichtbaren Ausschnitt neu an. Abgeschaltet (Tests,
   Rauchtest, `PHOTOVIEWERV3_MAP_TILES=off`) wird der Stil aufgelöst (Namensnennung, Hinweis), aber nie abgerufen.
3. **Cache je Stil** `<CacheRoot>\maptiles\<stil>\z\x\y.png` (`osm`, `carto-*`, `maptiler-satellite`), LRU-Grenze 500 MB
   über alle Stile wie bisher. Quellen mit Alterungsgrenze (CARTO 30 Tage; MapTiler „nur temporärer persönlicher Cache“
   → ebenfalls 30 Tage): Abrufzeit = Erstellungszeit der Datei, ältere Kacheln werden beim Lesen gelöscht und neu geholt,
   die Ablaufzeit wird auf höchstens 30 Tage gekappt, und `MapTileDiskCache.SweepExpired` räumt beim Start auch gerade
   nicht benutzte Stile. 401/403 → Zustand `KeyRejected` (Pause, Hinweis unten links).
4. **Einstellungen › Darstellung › Karte** (`SettingsMapSection` + `MapStyleSettingsViewModel`): Stilauswahl (wirkt
   sofort) und je Dienst eine **vorgeschlagene Karte „Kartendienst“** (`SettingsMapServiceCard` +
   `MapServiceAccountViewModel`, Zusatzwunsch Herbert 2026-10-21) — auch ohne Schlüssel sichtbar mit Beschreibung
   (Stile, Gratis-Kontingent, Bedingungen), Status („Kein Schlüssel – noch nicht eingerichtet“ / „noch nicht geprüft“ /
   „Eingerichtet“ / „abgelehnt“), **API-Schlüssel anfordern…** und Anleitung in drei Schritten; dazu Schlüssel
   (Passwortfeld) mit **Schlüssel prüfen** (genau ein Kachelabruf, `IMapKeyValidator`; gültig → sofort gespeichert,
   Status „Eingerichtet“, Stile freigeschaltet), **Website** (vorbelegt, änderbar, „Website öffnen“), E-Mail der
   Registrierung, **Konto / Verbrauch** (vorbelegt, „Konto öffnen“, „Verbrauch im Konto ansehen“), Notiz und Lumoras
   **eigene Zählung** „Diesen Monat von Lumora abgerufen: n Kacheln (Stand …)“ (`MapTileUsageCounter`, je Kalendermonat
   UTC, gebündelt gespeichert; CARTO mit „Gratis bis 5 Mio./Monat“). Geprüfte URLs (offizielle Seiten): CARTO
   Anforderung `https://carto.com/basemaps/apikey/`, Konto `https://dashboard.basemaps.carto.com`; MapTiler Schlüssel
   `https://cloud.maptiler.com/account/keys/`, Verbrauch `https://cloud.maptiler.com/account/analytics`. Links nur
   http/https (`MapServiceLinks`), geöffnet nur auf Klick im Standardbrowser — Lumora registriert nichts und sendet selbst
   keine Daten. **Kein „Verbrauch abrufen“**: CARTO dokumentiert keine Verbrauchs-API, MapTiler nur über die Admin-API mit
   Service-Zugangsdaten (nicht mit dem Kartenschlüssel). Ablage in der Settings-Tabelle (`ui.map.style`,
   `ui.map.cartoKey`, `ui.map.maptilerKey`, `ui.map.<dienst>.{website,email,accountUrl,note,verified,usage}`, „verified“
   = 12 Hex-Zeichen SHA-256 des geprüften Schlüssels) — unverschlüsselt, nie im Protokoll und nicht in „Versionsinfo
   kopieren“; im versteckten Katalog dessen eigene Einstellungen. Auf der Karte ein **Stil-Menü** (Ebenen-Knopf) mit
   denselben Stilen.
5. **Filterkarte als Steuerelement**: `GalleryFilterCard` (Zeilen „Datei“/„Inhalt“, „Es gilt“, „Filter zurücksetzen“,
   „Als Album speichern…“) und `GallerySearchBox` sind aus `GalleryToolbar` herausgelöst; Galerie/Ordner (über die
   Toolbar) und Orte binden **dieselben Steuerelemente** an das Singleton-`GalleryViewModel`.
6. **Ein Filterzustand**: Das `GalleryViewModel` veröffentlicht bei jedem Neuladen den Filter-Teil seiner Abfrage (ohne
   Ordner, Sortierung) in `ISharedGalleryFilter` (Singleton je Katalog-Sitzung, Gleichheit über
   `GalleryPageSource.FilterKey` = SQL + Parameter). Die Orte-Karte liest damit ihre Medien über
   `IPlacesCatalogReader.GetMediaAsync(bereich, filter)` → `GalleryPageSource.CreateFilteredCommand` — **dieselbe
   Klausel-Erzeugung** wie Zählung und Seiten der Galerie, plus „nur vorhandene Bilder/Videos“. Punkte ohne Medienzeile
   zählen nicht, so filtern Gruppen, Zähler, „Im Ausschnitt“, „Ohne Ort“ und der Start-Ausschnitt mit; bei einer
   Filteränderung bleibt der Ausschnitt, solange er Treffer zeigt. Ausgenommen ist nur die Bedingung
   „Kartenausschnitt“ (aus „In der Galerie zeigen“): sie stammt von der Karte selbst und gilt in Galerie und Ordner —
   auf der Karte angewendet, schrumpfte die Karte auf die eben gezeigte Gruppe.
7. **Seitengebunden** (`GalleryViewModel.Pages.cs`): Ordnerauswahl, Sortierung und Ansicht je Seite „Galerie“/„Ordner“
   — beim `INavigationService.Navigated` merkt sich das ViewModel diese Teile der verlassenen Seite und stellt die der
   neuen her (Ansicht der Ordner-Seite in `ui.folders.viewMode`). Der Kartenausschnitt gehört ohnehin der Orte-Seite.
8. **Bibliothekswechsel** behält den Filter: Dateiart, Favoriten, Bewertung, Ablage, Doppelte und Suche gelten weiter;
   Tags, Personen, Kategorien, Motive und Album behalten nur Häkchen, die es im neuen Bereich gibt (Tags/Kategorien
   werden jetzt **vor** der ersten Seite neu gelesen, ein Album-Filter wartet auf die Album-Liste); Ort,
   Kartenausschnitt und Bildinhalt-Suche (Datei-Ids bzw. Ortsliste des alten Bereichs) fallen immer weg.

## Begründung

- **Positron statt Voyager für hell:** Die Marker sind farbige Vorschaubilder mit Akzent-Zähler — auf dem grau-weißen
  Positron stehen sie klar heraus, Voyagers farbige Straßen, Parks und Wasserflächen konkurrieren mit den Fotos. Dark
  Matter ist das passende dunkle Gegenstück (gleiche Zurückhaltung); Voyager bleibt wählbar.
- **Ein Singleton-ViewModel mit seitengebundenen Teilen statt zwei ViewModels mit geteiltem Zustandsdienst:** Der
  Filterzustand lebt in über einem Dutzend Teil-Dateien (Flyout-Einträge mit Häkchen, Zählern, Modi). Ein Herauslösen in
  einen Dienst hätte all das umgebaut; das bestehende Singleton teilt den Filter schon, es fehlte nur das Gegenteil —
  Ordner/Sortierung/Ansicht je Seite. `ISharedGalleryFilter` ist der schmale Vertrag für Seiten ohne eigene Galerie.
- **Filter-SQL aus `GalleryPageSource` statt Erweiterung von `IGeoQueryService`:** Die Galerie-Klauseln leben im
  Wpf-Projekt; ein Services-Dienst dürfte sie nicht kennen, eine Nachbildung wäre eine zweite Filter-SQL. Die Karte
  filtert über die Medienliste, die Punkte-Abfrage (`IGeoQueryService`) bleibt unverändert.
- **30 Tage auch für MapTiler:** Die MapTiler-Bedingungen nennen keine Frist, erlauben aber nur einen temporären
  persönlichen Cache; dieselbe Grenze wie bei CARTO ist einfach und vorsichtig.

## Konsequenzen

### Positiv
- Ruhige, zum Farbschema passende Karten; Satellit auf Wunsch; ohne Schlüssel unverändert OSM.
- Eine Filterkarte, ein Filter, drei Seiten; „Als Album speichern…“ auch von der Karte aus.
- Keine neuen Pakete, keine Migration; Tests und Rauchtest bleiben ohne Netz.

### Negativ
- Die Galerie lädt bei Filteränderungen auf der Karte im Hintergrund mit (eine Seite, gebündelt).
- Ein Bibliothekswechsel setzt nicht mehr alles zurück — wer einen leeren Bereich sieht, prüft „Es gilt“.
- 256-px-Kacheln: auf Bildschirmen mit hoher Skalierung weniger scharf (`@2x` hätte den Speicher-Cache vervierfacht).

### Neutral
- Alte OSM-Kacheln direkt unter `maptiles\z\…` (bis 0.4.0) bleiben liegen und fallen über die LRU-Grenze weg.
- `OsmTileSource` ist nur noch eine Konstanten-Klasse; die Netz-Quelle heißt `HttpMapTileSource`.

## Alternativen

- **Voyager als „Automatisch hell“:** lesbar, aber bunter → verworfen (Begründung oben), wählbar.
- **`@2x`-Kacheln auf HiDPI:** schärfer, aber 4× Speicher je Kachel → zurückgestellt.
- **Zweites GalleryViewModel je Seite + Zustandsdienst:** sauberer Schnitt, aber großer Umbau aller Filter-Teile →
  verworfen zugunsten seitengebundener Teile im Singleton.
- **Filter in `IGeoQueryService` (Services):** zweite Filter-SQL bzw. WPF-Kopplung → verworfen.

## Referenzen

- Code: `src/PhotoViewerV3.Wpf/Places/Map/Tiles/MapStyles.cs`, `HttpMapTileSource.cs`, `MapTileDiskCache.cs`,
  `Places/Styles/*`, `Settings/Sections/SettingsMapSection.xaml`, `Gallery/Controls/GalleryFilterCard.xaml`,
  `GallerySearchBox.xaml`, `Gallery/ViewModels/SharedGalleryFilter.cs`, `GalleryViewModel.Pages.cs`,
  `Gallery/Data/GalleryPageSource.Shared.cs`, `Places/Data/PlacesCatalogReader.cs`, `Places/ViewModels/PlacesViewModel.Styles.cs`
- Tests: `tests/PhotoViewerV3.Wpf.Tests/Places/MapStyleTests.cs`, `MapStyleSettingsTests.cs`,
  `Gallery/SharedGalleryFilterTests.cs`; Rauchtest `Shell/Smoke/SmokeScenario.PlacesShared.cs`
- CARTO: carto.com/basemaps, carto.com/legal/basemap-terms · MapTiler: maptiler.com/terms/cloud, maptiler.com/copyright

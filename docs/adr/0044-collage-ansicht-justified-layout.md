# ADR-0044: Collage-Ansicht — Justified Layout in einem eigenen virtualisierenden Panel

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Auftrag), umgesetzt mit Claude
- **Bezogen auf:** ADR-0041 (Ansicht-Segment, Kopfzeile einzeilig bei 1280 DIP), ADR-0043 (Vorschaubilder, `Files.Width/Height`),
  Mockup 06 (Reiter „Galerie“, Ansicht „Collage“), CHANGELOG 0.2.3/0.2.42 (`VirtualizingTilePanel`, Container-Zuordnung)

## Kontext

Die Galerie zeigte Fotos und Videos nur als gleich große Kacheln (Bild beschnitten, `UniformToFill`) oder als Liste.
Mockup 06 sieht eine dritte Ansicht „Collage“ vor: Bilder im echten Seitenverhältnis, Reihen gleich hoch und randvoll
(„Justified Layout“ wie Google Fotos/Flickr), letzte Reihe nicht gestreckt. Seit 0.2.45 stehen die Bildmaße nach
EXIF-Drehung in `Files.Width/Height`. Eine Seite hat bis zu 1000 Items; die Kacheln sind seit 0.2.3 UI-virtualisiert und
seit 0.2.42 gegen falsche Container-Zuordnung (Kachel zeigt A, Doppelklick öffnet B) abgesichert. Galerie-Container
müssen Blätter im Automation-Baum bleiben (v0.2.0-Hänger).

## Entscheidung

1. **Eigenes Panel `JustifiedTilePanel`** (Gallery/Controls) neben dem `VirtualizingTilePanel`, in derselben
   `GalleryListBox`. Panel, Item-Vorlage und Container-Stil setzt der Code-Behind je `GalleryViewMode` als lokale Werte
   (`GalleryView.ApplyGalleryViewMode`/`ApplyListTemplates`, nur geänderte, Panel zuletzt) — kein Stil-Trigger: dessen
   Neubewertung beim Seitenwechsel ließ beim späteren Entfernen von Items Container ohne ListBox-Vorfahren ihre
   Bindungen auswerten (8 Binding-Fehler im Rauchtest, mit lokalen Werten 0). Auswahl, Kontextmenü, Doppelklick-Handler, Tastenkürzel, Strg+Mausrad und der gemeldete
   sichtbare Bereich (`VirtualizingTilePanel.VisibleRangeChangedEvent`, Vorschaubilder sichtbare zuerst) bleiben gleich.
2. **Umbruch vorab für die ganze Seite** (`JustifiedRowLayout`, O(n), Arrays wiederverwendet): Items füllen eine Reihe bei
   Zielhöhe, bis die Breite überläuft; dann wird die Reihe auf genau die Breite **verkleinert** (Bildhöhe ≤ Zielhöhe, nie
   gestreckt). Letzte Reihe bleibt bei Zielhöhe, linksbündig. Ein allein zu breites Bild (Panorama) bekommt eine eigene
   Reihe mit voller Breite. Seitenverhältnisse werden auf **0,4 … 6** begrenzt (nur noch extremere Bilder werden in der
   Zelle mittig beschnitten). Zelle = Bild + 6 DIP (Container-Rand 3 je Seite = Fuge 6, wie Mockup); Kanten auf ganzen DIP,
   die rechte Kante einer gefüllten Reihe ist exakt die Breite. Neu berechnet wird nur bei geänderter Item-Liste, Breite,
   Zielhöhe oder `LayoutVersion` — Scrollen rechnet nicht neu. Das erste sichtbare Item bleibt dabei oben (Anker).
3. **Realisierung exakt wie `VirtualizingTilePanel` (0.2.42)**: nur sichtbare Reihen + 1 Pufferreihe; recycelte Container
   immer mit `PrepareItemContainer`; Anordnen über den echten Item-Index des Containers; Container mit Tastaturfokus
   bleibt außerhalb des Bereichs an seinem Platz. `IScrollInfo` pixelbasiert, `MakeVisible`/`BringIndexIntoView` je
   Reihe; Pfeiltasten über Reihen nutzt die ListBox-Navigation (geometrisch, Nachbarreihe ist durch den Puffer realisiert).
4. **Seitenverhältnis je Item** (`GalleryItem.LayoutAspectRatio`, Interface `IJustifiedLayoutItem`): aus
   `Files.Width/Height` (neu im Galerie-DTO, über den Spaltennamen gelesen); fehlen sie oder sind 0 × 0 → aus dem
   geladenen Vorschaubild; sonst und bei Archiven 4:3. Ändert es sich (Maße beim Neuladen, erstes Vorschaubild ohne
   Maße), erhöht das ViewModel gebündelt je Dispatcher-Runde `CollageLayoutVersion` → das Panel bricht neu um.
5. **Zielhöhe = Vorschaugrößen-Regler** (`TileSize`, 120–320 DIP, Strg+Mausrad/Strg+Plus/Minus wie bei Kacheln).
6. **Dekodierung je Ansicht** (`ThumbnailDecodeBox.FromCollageRow`): Collage dekodiert auf Reihenhöhe × Skalierung
   (Querformat: kurze Kante, Hochformat: Breite aus der Höhe), Breite der Fläche = schmalste Zelle (0,4 × Höhe); die
   Kachel-Konstanten (Rand, Namenszeile) gelten dort nicht. Neu dekodiert wird nur, wenn die Fläche deutlich größer ist
   **und** das gezeigte Bild sie nicht schon bedeckt (Collage → Kacheln dekodiert nichts neu).
7. **Collage-Vorlage** in einem eigenen Wörterbuch (`Gallery/Views/GalleryCollageTemplates.xaml`, von Tests geladen):
   Bild füllt die Zelle, kein Namensfeld (Name als Tooltip und beim Darüberfahren unten eingeblendet), Favorit, Sterne,
   Video-Symbol, Personen-Plaketten und „fehlt“/„Eingang“ als Overlay, Auswahlrahmen über dem Bild.
8. **Kopfzeile bleibt bei 1280 DIP einzeilig** (ADR-0041): reicht die Breite der Sortier- und Ansichtsleiste mit dem
   dritten Segment-Eintrag nicht, zeigen „Alle auswählen“ und „Rückgängig“ nur ihr Symbol (Tooltip/Automation-Name
   bleiben); mit Hysterese, damit das Umschalten keine Layout-Schleife auslöst.

## Begründung

- Ein eigenes Panel statt eines umgebauten `VirtualizingTilePanel` hält das bewährte Kachel-Panel (und die Personen-Seite,
  die es mitnutzt) unverändert; die Realisierungslogik ist bewusst Zeile für Zeile übernommen, damit die 0.2.42-Regeln
  für beide gelten (gemeinsame Basisklasse wäre ein späterer, reiner Umbau).
- Nur verkleinern (wie Mockup 06) begrenzt die Bildhöhe auf die Zielhöhe — so muss nie größer dekodiert werden als für
  den Regler, und es wird nie hochskaliert.
- Umbruch für die ganze Seite ist billig (Messung unten) und macht Extent, Scrollbalken und `ScrollIntoView` exakt.
- Eine `GalleryListBox` für beide Raster-Ansichten: Auswahl-Abgleich, Blatt-Automation-Peers und Doppelklick-Weg (inkl.
  Id-Rückfall der Lightbox aus 0.2.42) gelten automatisch auch für die Collage.

## Messwerte

- Umbruch 1000 Items (gemischte Seitenverhältnisse, 1000 DIP breit, Zielhöhe 180): **Median 0,046 ms**, max 0,18 ms
  (`JustifiedRowLayoutTests.Messung_1000_Items_Umbruch_unter_2_ms`); im Panel inkl. Lesen der Seitenverhältnisse
  0,11 ms; 1000 Items in 900 × 600 realisieren 19 Container (250 Reihen).
- Rauchtest (46 Items): Umbruch 1,2 ms beim ersten Aufbau (kalter JIT), 13 Container, Reihen randvoll ±0 px.
- Speicher je Vorschaubild bei gleichem Regler (180, 100 %): Kachel 164 × 123 px (4:3, ≈ 81 KB), Collage 240 × 180 px
  (≈ 173 KB) — gleiche Grundsätze (nur passend zur Anzeigegröße dekodiert, nie Original), aber ≈ 2,1-fach je Item, weil
  die Collage-Zelle bei gleichem Regler größer ist. 1000 Items bei 150 %: ≈ 180 MB (Kacheln) gegen ≈ 390 MB (Collage).

## Konsequenzen

### Positiv
- Fotos und Videos ohne Beschnitt, Reihen randvoll; Wechsel Kacheln ↔ Collage ↔ Liste ohne Neuladen der Seite.
- Gleiche Garantien wie beim Kachel-Raster (Container-Zuordnung, Fokus, Recycling), durch Tests mit Treffertest belegt.

### Negativ
- Realisierungslogik liegt zweimal vor (`VirtualizingTilePanel`, `JustifiedTilePanel`).
- Vorschaubild-Speicher je Item in der Collage etwa doppelt so groß wie bei Kacheln gleicher Reglerstufe.
- Neue Dateien ohne Maße erscheinen zuerst 4:3 und springen auf ihr Seitenverhältnis, sobald das Vorschaubild da ist.

### Neutral
- Unterordner-Kacheln (Ordner ohne direkte Dateien) bleiben ein eigener Zustand (168 × 124 ≈ 4:3), unabhängig von der
  Ansicht; Archive sind in der Collage 4:3-Zellen.
- `GalleryFileDto` hat `Width`/`Height` als init-Eigenschaften (Positionsparameter unverändert).

## Alternativen

- **Ein Panel mit Modus-Schalter:** weniger Code, aber jede Änderung am Collage-Umbruch riskiert das Kachel-Raster und die
  Personen-Seite → verworfen.
- **Reihe wahlweise verkleinern oder vergrößern (näher an der Zielhöhe):** gleichmäßigere Reihen, aber Bildhöhe bis
  ≈ 1,2 × Zielhöhe → größer dekodieren oder hochskalieren → verworfen (Mockup 06 verkleinert nur).
- **Zweite ListBox nur für die Collage:** doppelte Auswahl-Synchronisation und Bindungen auf 1000 Items → verworfen.
- **Zielhöhe 0,75 × Regler (Speicher wie Kacheln je Item):** kleinere Bilder als im Mockup → vorerst verworfen; bleibt
  Stellschraube, falls der Speicher bei 1000er-Seiten stört.

## Referenzen

- `src/Lumora.Wpf/Gallery/Controls/JustifiedTilePanel.cs`, `JustifiedTilePanel.Layout.cs`
- `src/Lumora.Wpf/Gallery/Views/GalleryCollageTemplates.xaml`, `GalleryView.xaml(.cs)` (`ApplyGalleryViewMode`)
- `src/Lumora.Wpf/Gallery/ViewModels/GalleryViewModel.Collage.cs`, `GalleryItem.Collage.cs`,
  `GalleryViewModel.ThumbnailDecode.cs` (`FromCollageRow`, `IsCoveredBy`)
- Tests: `JustifiedRowLayoutTests`, `JustifiedTilePanelTests`, `CollageThumbnailDecodeTests`; Rauchtest-Schritt
  „Collage-Ansicht“ (`Shell/Smoke/SmokeScenario.Collage.cs`)

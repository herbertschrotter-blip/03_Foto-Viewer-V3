# Spike #07 — Galerie-Kopfzeile bei schmalen Fenstern

- **Status:** 🟢 Grün
- **Datum:** 2026-10-19 (v0.2.13)
- **Bezogen auf:** `src/Lumora.Wpf/Gallery/Controls/GalleryToolbar.xaml`, `ToolbarFlowPanel.cs`,
  Mockup `mockups/04-fluent-light.html` (Filter-Pills), Test `tests/Lumora.Wpf.Tests/Gallery/GalleryToolbarLayoutTests.cs`

## Problem

Seit 0.2.8–0.2.10 trug die erste Kopfzeilen-Zeile sieben Filter-Pills, das Tag-Dropdown, den Ordner-Chip, das Suchfeld
und „Suchen“/„Leeren“ in einem Grid mit `*`-Spalte für die Suche. Bei 1280 px Fensterbreite blieb für die Suche nur ein
schmaler Rest (Platzhalter abgeschnitten), darunter lief die Zeile rechts über.

## Lösung

- Typ-Filter als **Segmentgruppe** (Alle | Bilder | Videos | Favoriten | Archive) im Fluent-Stil, Status-Filter
  **Nicht archiviert / Archiviert** in ein **Dropdown „Status“** (zeigt den aktiven Wert in Akzentfarbe). Bindungen
  unverändert (`Filter` über `EnumEquals`, Werte `GalleryFilter.*`).
- `ToolbarFlowPanel`: Elemente nebeneinander, Umbruch wie ein WrapPanel. Füll-Elemente (Suche, Abstandhalter) zählen
  für den Umbruch nur mit `MinWidth` und bekommen danach den Rest ihrer Zeile bis `MaxWidth`.
- Suche: Mindestbreite 360 px (Eingabefeld ≥ 240 px), höchstens 640 px; kürzerer Platzhalter „Name, Endung oder Tag…“
  (die Suche findet seit 0.2.10 auch Tag-Namen); „Leeren“ als Symbolknopf (✕), der eingebaute Leeren-Knopf des
  Eingabefelds ist aus (er leerte nur den Text, ohne neu zu laden).
- Zweite Zeile: Sortierung, Seitengröße links; Kachel/Liste, Rückgängig, Aktualisieren rechtsbündig — bricht ebenfalls
  um, falls nötig (rechte Hälfte der Ordner-Seite).

## Messung (Testfenster bei −5000/−5000, Inhaltsbreite = Fenster − 220 px Navigation − 2 × 16 px Rand)

| Fensterbreite | Inhalt | Zeilen Filter/Suche | Suche (min 360) | Eingabefeld | Bild |
|---|---|---|---|---|---|
| 1024 | 772 px | 2 (Suche eigene Zeile) | 640 px | 525 px | [kopfzeile-1024.png](07-galerie-kopfzeile/kopfzeile-1024.png) |
| 1280 | 1028 px | 1 | 429 px | 314 px | [kopfzeile-1280.png](07-galerie-kopfzeile/kopfzeile-1280.png) |
| 1280 + Ordner-Chip + Status aktiv | 1028 px | 2 | 640 px | 525 px | [kopfzeile-1280-ordner-status.png](07-galerie-kopfzeile/kopfzeile-1280-ordner-status.png) |
| 1920 | 1668 px | 1 (Suche rechtsbündig) | 640 px | 525 px | [kopfzeile-1920.png](07-galerie-kopfzeile/kopfzeile-1920.png) |
| 2560 | 2308 px | 1 | 640 px | 525 px | — |
| Ordner-Seite (560 px Inhalt) | 560 px | 2 | 467 px | 352 px | — |

Der Platzhalter braucht inkl. Suchsymbol und Innenabstand rund 215 px; in keiner Breite ragt ein Element über den Rand.
Die Tests erzeugen die Bilder bei jedem Lauf neu unter `tests/Lumora.Wpf.Tests/bin/…/toolbar-shots/`
(dunkles Theme, 96 DPI); die Breite des Navigationsbereichs ist im Test nachgebildet. Gegenprobe in der echten App
(Rauchtest, Fenster 1280 × 800, helles Theme, Status „Nicht archiviert“ aktiv): Filter, Status, Tags und Suche stehen in
einer Zeile, das Eingabefeld ist rund 245 px breit, der Platzhalter vollständig sichtbar — die echte Inhaltsbreite liegt
nur knapp unter der im Test angenommenen.

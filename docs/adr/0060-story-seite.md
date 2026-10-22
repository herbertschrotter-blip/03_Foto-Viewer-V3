# ADR-0060: Story-Seite — virtualisierte Übersicht, Story-Ansicht mit Collage-Blöcken, Galerie-Menü, Reiter „Storys“

- **Status:** Accepted
- **Datum:** 2026-10-22
- **Entscheider:** Herbert Schrotter (Mockup 09 abgenommen 2026-10-21), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** Mockup [`09-storys.html`](../../mockups/09-storys.html) (Übersicht, Story, Rückblick, Einstellungen),
  [ADR-0058](0058-story-erkennung.md) (Story-Dienst `IStoryService`), [ADR-0057](0057-foto-guete-und-highlights.md)
  (Highlights über `IHighlightSelector`), [ADR-0059](0059-diashow.md) (Diashow), [ADR-0044](0044-collage-ansicht-justified-layout.md)
  (Justified-Layout der Galerie), [ADR-0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md) (Vorschaubilder, `Files.Width/Height`),
  [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich), [ADR-0049](0049-versteckte-bibliotheken.md) (versteckter Katalog),
  [ADR-0050](0050-orte.md) (Kartenausschnitt-Filter der Galerie)

## Kontext

Phase 2 der Welle 6: Die Story-Erkennung (ADR-0058) und die Highlights (ADR-0057) liefern Daten, die Diashow (ADR-0059) ist
fertig — es fehlte die Oberfläche aus dem abgenommenen Mockup 09. Herbert: nur sachliche Überschriften (Datum, Orte, Namen,
kein erfundener Text), Collage-Bild erst mit Welle 6b, keine Musik. Die Seite muss mit hunderten Storys und Reisen mit
mehreren hundert Fotos flüssig bleiben, Kacheln dürfen den UI-Automation-Baum nicht aufblähen (CLAUDE.md), und alles muss
hell und dunkel mit den WPF-UI-Ressourcen funktionieren.

## Entscheidung

1. **Seite „Storys“** (`Wpf/Stories`, Navigation nach „Alben“, Tag `stories`) mit Zähler **„n neu“** als `InfoBadge` am
   Navigationseintrag. Der Zähler hängt an einer gemeinsamen Instanz (`StoriesNavigationBadge.Shared`, `x:Static` in
   `MainWindow.xaml`), gesetzt von der Übersicht nach jedem Laden und von `StoriesNavigationBadgeUpdater` (Start in
   `App.xaml.cs`, liest die Übersicht gebündelt nach `StoriesChangedMessage` und Bibliothekswechsel).
2. **Übersicht als virtualisierte Zeilenliste**: `ItemsControl` + `VirtualizingStackPanel` (Pixel-Scrollen, Standard-
   Virtualisierung) über Zeilen-Daten — Rückblick-Banner, Abschnitts-Kopf, Jahres-Chips, Kartenreihe (so viele Karten,
   wie bei mindestens 232 DIP in die Breite passen), Hinweis. Abschnitte wie im Mockup: Banner „Heute vor N Jahren“ (nur ein
   Rückblick der Art „heute“, sonst kein Banner), Angeheftet, Neu (im Filter „Alle“ eine Reihe), Reisen & Ereignisse mit
   Jahres-Chips (im Filter „Alle“ zwei Reihen + „Alle anzeigen“), Personen und Orte (eine Reihe + „Alle n“), Eigene Storys.
   Art-Filter mit Zählern des Dienstes; Suche clientseitig über Überschrift, sachliche Überschrift, Untertitel, Art und
   Jahre (alle Wörter, ohne Groß-/Kleinschreibung und Akzente). „Ausgeblendet (n)“ schaltet auf die ausgeblendeten Storys mit
   „Wieder zeigen“. Vorschaubilder (3 je Karte über `GetPreviewFileIdsAsync`) und Personen-Rundbilder (bestätigte Gesichter,
   Titel-Gesicht der Person über den Ausschnitt-Cache der Personen-Seite) kommen erst, wenn eine Reihe realisiert wird.
3. **Story-Ansicht** in derselben Seite (ersetzt die Übersicht, Brotkrumen zurück): ebenfalls eine virtualisierte
   Zeilenliste — Titelbild (scharf aus dem Original wie die Diashow, bis dahin das Vorschaubild), Platzhalter, je
   Tagesabschnitt Kopf + Collage-Blöcke (Highlights) bzw. dichte Reihen (alle Fotos), „Alle n Fotos dieses Abschnitts“
   (aufklappen) bzw. „Nur Highlights zeigen“, bei mehr als vier Tagen die ersten drei offen und „n weitere Tage … Anzeigen“,
   Ende-Kasten („64 von 421 Fotos als Highlights · 37 ähnliche weggelassen“, Album, Diashow). Die **Leiste**
   Highlights/Alle Fotos · Tages-Chips · Diashow liegt über der Liste und wird per `TranslateTransform` mit dem Titelbild
   hinausgeschoben, bis sie oben stehen bleibt (sticky wie im Mockup, ohne die Virtualisierung aufzugeben).
4. **`StoryLayoutEngine`** (rein, getestet): verteilt die Highlights eines Abschnitts in Aufnahme-Reihenfolge auf
   Vorlagen wie im Mockup — groß+2, 3er, 4er, 2+groß im Wechsel (Startpunkt je Tag verschoben, nie zweimal dieselbe
   hintereinander bei langen Folgen), **Panorama** (≥ 2,2:1) allein über die volle Breite, **zwei Hochformate**
   (< 0,9:1) nebeneinander (mit einem Querformat in der breiten Spalte bzw. als Paar), Reste als Paar bzw. einzeln;
   beim großen Platz das Foto mit dem höchsten Wert. Spalten über kumulierte Rundung auf ganze DIP — jede Reihe füllt die
   Breite exakt, nur die 6-DIP-Fugen bleiben. Höhen wie im Mockup bei 1000 DIP, 0,6- bis 1,5-fach mit der Breite.
   Die dichte Collage („Alle Fotos“, aufgeklappter Tag) nutzt das `JustifiedRowLayout` der Galerie (ADR-0044); Nicht-
   Highlights sind blass (38 %).
5. **Eine Story über genau einen Tag** (Rückblick „Heute vor …“, Ereignis) wird nach **Tageszeit** geteilt (Nacht < 5,
   Vormittag < 12, Nachmittag < 17, Abend), wenn mindestens zwei Tageszeiten belegt sind — Abschnitt-Ort aus den Orten der
   Fotos. Sachlich, kein erfundener Text; sonst gelten die Tagesabschnitte des Dienstes.
6. **Kacheln und Karten sind Automation-Blätter** (`StoryLeafButton`, Name = Dateiname bzw. „Art: Überschrift, Orte,
   Zähler“), Bilder zeichnet `StoryImage` (UniformToFill mit runden Ecken in `OnRender`) statt gebundener `ImageBrush`
   (die meldeten beim Abbau virtualisierter Zeilen Binding-Fehler). Vorschaubilder über die Thumbnail-Pipeline in zwei
   Zielgrößen (330 × 248 bzw. 660 × 440), gebündelt mit Background-Priorität an den UI-Thread, Speicher bis 900 Bilder. Vorlagen
   binden Befehle über eine `Owner`-Referenz in Zeile/Karte/Kachel statt `RelativeSource FindAncestor` auf Seite bzw. Liste
   (beim Abbau virtualisierter Zeilen meldeten diese Binding-Fehler); Segment- und Chip-Knöpfe sind `StorySelectButton`
   (ToggleButton, der nicht selbst umschaltet — gewählt nur aus dem ViewModel). Die Übersicht lädt erst, wenn die Seite
   zum ersten Mal gezeigt wird, danach auch nach jedem Bibliothekswechsel (ein früh erzeugtes ViewModel, das schon vorher bei
   jedem Wechsel lud, ließ im Rauchtest den Schritt „Motive“ scheitern).
7. **Aktionen** (über `IStoryService`): Öffnen markiert als gesehen; „…“-Menü mit Titel ändern (nur eigener Text, leer =
   sachliche Überschrift), Titelbild wählen (Klick-Modus, „Automatisch“), Fotos hinzufügen (Galerie mit gemerktem Ziel →
   Rechtsklick „Zur Story … hinzufügen“), Fotos entfernen (Markier-Modus) bzw. je Kachel „Aus Story entfernen“, Anheften,
   Als Album speichern (Highlights bzw. alle je nach Modus), In der Galerie zeigen, Ausblenden, Neu berechnen, Löschen (nur
   eigene/Rückblicke) und **„Als Collage-Bild speichern…“ sichtbar, aber deaktiviert („Welle 6b“)**. Diashow startet mit den
   Highlights (sonst allen) bzw. im Modus „Alle Fotos“ mit allen, immer ab dem Anfang; Doppelklick öffnet die Lightbox mit den
   Fotos der Story (höchstens 5 000 um das Foto). Rückblicke werden beim Öffnen bzw. beim Start der Diashow über
   `OpenMemoryAsync` gespeichert.
8. **Galerie-Kontextmenü** (`StoryGalleryBridge`, ein optionaler Parameter + eine Zeile in `GalleryView`): „Als Story…“
   (Auswahl, eigener Titel optional) öffnet danach die neue Story; „In Storys zeigen“ lädt die Storys der Datei beim
   Aufklappen. **„In der Galerie zeigen“** nutzt den vorhandenen Datei-Filter der Galerie (`ShowFilesOnMapSelectionMessage`
   ohne Ausschnitt → Filter über die Id-Liste).
9. **Einstellungen › Storys** (neuer Reiter nach „Darstellung“, `settings-stories`): Story-Arten an/aus, Zu Hause
   (automatisch mit dem erkannten Ort aus dem Zuhause-Zwischenspeicher, oder Ort über die Offline-Ortssuche wählen),
   Pause, Mindestgröße, Highlights-Anteil (8/15/30 %); Werte außerhalb der Liste werden ergänzt, jede Änderung sofort
   gespeichert (die Erkennung rechnet neu). Der Abschnitt **Diashow** zieht aus „Darstellung“ hierher.
   „Unscharfe/dunkle nie als Titelbild“ ist **kein** Schalter: der Dienst kennt keine solche Einstellung,
   `IHighlightSelector.PickCoverAsync` tut es immer, wenn es eine Alternative gibt — die Karte sagt das als Hinweis.
10. **Statusleiste** „Storys aktuell“ / „Storys werden berechnet …“ / „Storys warten auf Aufnahmedaten und Orte“ aus
    `StoryIndexStatusMessage` (erst nach der ersten Meldung sichtbar).
11. **Versteckte Bibliothek:** alles läuft über den Katalog der Sitzung (Dienste je Sitzung), Bilder aus dem Cache des
    Katalogs, das Titelbild aus der Originaldatei wie Lightbox/Diashow — nichts landet außerhalb.

## Begründung

- Eine Zeilenliste statt verschachtelter `WrapPanel`/`ScrollViewer`: einzige Möglichkeit, Übersicht und Story mit hunderten
  Karten bzw. Fotos zu virtualisieren; Kartenreihen werden aus der Breite neu gebildet (reine Rechnung, testbar).
- Layout als reine Funktion: Tabellentests für Panoramen, Hochformate, Videos, Reste, Lückenlosigkeit und Determinismus
  ohne WPF; die Ansicht setzt nur Plätze auf ein `Canvas`.
- Die fixierte Leiste als Überlagerung: WPF hat kein `position: sticky`; die Verschiebung nach dem Scroll-Versatz ist
  billig und behält die Virtualisierung.
- „In der Galerie zeigen“ über den vorhandenen Id-Filter statt eines neuen Galerie-Filters: kein Eingriff in die geteilte
  Galerie; die Bedingung heißt dort vorerst „Kartenausschnitt (n Dateien)“ (offener Punkt).

## Konsequenzen

### Positiv
- 300 Storys: höchstens ≈ 20 Zeilen realisiert (UI-Test), Bilder nur für sichtbare Reihen; Story mit hunderten Fotos
  realisiert nur die sichtbaren Blöcke.
- Keine neue Tabelle, keine Migration; Dienste aus ADR-0057/0058/0059 unverändert.

### Negativ
- Die Bezeichnung des Galerie-Filters nach „In der Galerie zeigen“ lautet „Kartenausschnitt“.
- Personen-Rundbilder brauchen eine Abfrage über `StoryItems` × `Faces` je sichtbarer Reihe.

### Neutral
- Der Abschnitt Diashow steht nur noch unter „Storys“ (Einstellungen › Darstellung zeigt Farbschema und Karte).
- Rauchtest: neuer Schritt „Story-Seite“ nach „Diashow“, vor „Versteckte Bibliothek“, mit Screenshots 62–66.

## Alternativen

- **WrapPanel ohne Virtualisierung** (wie Alben) → verworfen: hunderte Karten mit je drei Bildern und Automation-Peers.
- **Eine `ListBox` je Tag mit `JustifiedTilePanel`** → verworfen: verschachtelte Scroll-Bereiche, keine gemeinsame
  Virtualisierung; das Layout wird stattdessen mit `JustifiedRowLayout` gerechnet.
- **Eigener Galerie-Filter „Story“** → zurückgestellt (geteilte Galerie-Dateien; Id-Filter reicht funktional).
- **Schalter „Unscharfe/dunkle nie als Titelbild“** → weggelassen (keine Einstellung im Dienst; Verhalten ist fest).

## Referenzen

- Code: `src/Lumora.Wpf/Stories/**`, `src/Lumora.Wpf/Shell/StatusBarViewModel.Stories.cs`, Rauchtest
  `src/Lumora.Wpf/Shell/Smoke/SmokeScenario.StoryPage.cs`
- Tests: `tests/Lumora.Wpf.Tests/Stories/**`

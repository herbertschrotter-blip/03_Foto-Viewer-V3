# ADR-0063: Story-Assistent — Sätze in eigenen Worten über eine lokale Sprach-KI (nur Absicht), Auflösung in Lumora, Collage-Bild

- **Status:** Accepted
- **Datum:** 2026-10-22
- **Entscheider:** Herbert Schrotter (Mockup 12 abgenommen 2026-10-22, Nachträge: Herberts Beispielsatz, Tags/Kategorien),
  Claude (Umsetzung, Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** Mockup [`12-story-assistent.html`](../../mockups/12-story-assistent.html), [ADR-0058](0058-story-erkennung.md)
  (Storys, Reise-Erkennung, Zuhause), [ADR-0060](0060-story-seite.md) (Story-Seite, „Als Collage-Bild speichern…“),
  [ADR-0057](0057-foto-guete-und-highlights.md) (`IHighlightSelector`), [ADR-0059](0059-diashow.md), [ADR-0054](0054-alben-verknuepfen-statt-kopieren.md),
  [ADR-0045](0045-personen-details-und-beziehungen.md) (Beziehungen, Geburtsdatum), [ADR-0052](0052-motive-siglip2-lokal.md) (Motive, `IGpuGate`),
  [ADR-0050](0050-orte.md) (Offline-Ortssuche), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md), [ADR-0049](0049-versteckte-bibliotheken.md),
  [ADR-0061](0061-container-assistent.md) (winget-Muster)

## Kontext

Herbert will Storys, Collagen, Alben und Diashows mit Sätzen in eigenen Worten anstoßen („Collage mit meiner Schwester aus
meiner Kindheit bis 20 Jahre“, „Weihnachten bei Oma, alle Jahre“, „Erstelle mir ein Album mit den schönsten Urlaubsfotos der
letzten fünf Jahre mit Nicole und Nina“). Harte Regeln: Fotos verlassen nie den Rechner, die Sprach-KI sieht nie Fotos,
Dateinamen, Pfade, Koordinaten oder Geburtsdaten; kein erfundener Text (Überschriften sachlich); nichts entsteht ohne
Bestätigung; Downloads und Installationen nur nach Bestätigung mit Name, Quelle, Größe und Lizenz; kein Cloud-LLM. Der
Rechner hat eine GTX 1080 Ti (11 GB, Pascal), die Gesichts- und Motiv-Erkennung teilen sich die Grafikkarte schon.

## Entscheidung

1. **Ablauf** (`Services/Assistant`, WPF-frei): Satz → **Sprach-KI übersetzt NUR in eine Absicht** (`StoryIntent`) →
   **Lumora löst auf** (`StoryIntentResolver`) → **Treffer zählen** (`AssistantQueries`, `IHighlightSelector`) → Karte
   „So habe ich es verstanden“ → erst **„Erstellen“** legt Story/Album an, startet die Diashow, zeigt die Galerie oder
   öffnet den Collage-Dialog. Jede Änderung in der Karte zählt nur in Lumora neu (keine zweite KI-Anfrage).
2. **Was die Sprach-KI sieht** (`IntentPrompt`): den Satz (höchstens 300 Zeichen), das heutige Datum, die Personen der Gruppe
   (Anzeigename, Rufname, Beziehung, „Das bin ich“ — höchstens 150, meiste Fotos zuerst), die **Namen** der Motive, Tags,
   Kategorien und Kategorie-Werte (höchstens 300) und die erlaubten Aktionen. Keine Geburtsdaten (Alter rechnet Lumora),
   keine Zählungen, keine Fotos, Pfade, Dateinamen, Koordinaten. Im versteckten Modus nur Namen aus dem versteckten Katalog.
3. **Schema** (`StoryIntentJson`): ein JSON-Objekt mit 20 Pflichtfeldern — `action` (story|collage|album|slideshow|gallery),
   `persons[{name, relation, me}]`, `personMatch` (all|any, Standard all = „alle zusammen auf dem Foto“), `others`
   (any|none), `terms[]` (Orte, Motive, Tags, Kategorien, Themen nur als Text), `termMatch`, `trip` (Urlaub/Reise/Ferien),
   `dateFrom/dateTo` (JJJJ-MM-TT), `lastYears` („die letzten N Jahre“ — Lumora rechnet relativ zu heute), `ageFrom/ageTo/
   agePerson`, `everyYearFrom/To` (MM-TT, wiederkehrend), `media`, `best` + `bestWanted`, `minRating`, `favorites`. „Keine
   Angabe“ ist "" / 0 / −1 (kleine Modelle halten feste Typen zuverlässiger ein als `null`). Das Schema geht als Ollamas
   `format` mit (die Form wird schon beim Erzeugen erzwungen). **Strikte Prüfung**: unbekannte Felder, falsche Typen,
   unbekannte Aufzählungswerte, Werte außerhalb der Grenzen (Jahre 1800 … nächstes Jahr, Alter 0–120, ab ≤ bis, höchstens
   6 Personen/8 Begriffe) → abgelehnt; abgeschnittenes JSON (`done_reason = length` bzw. ohne schließende Klammer) wird als
   solches erkannt. Fehlende Felder (Modelle ohne Schema-Unterstützung) gelten als „keine Angabe“, nur `action` ist Pflicht.
   „Urlaub/Reise“ als Begriff wird zur Reise-Erkennung. Titel, Beschreibungen, Kommentare gibt es im Schema nicht.
4. **Auflösung in Lumora**:
   - **Personen** über Anzeige-/Vor-/Ruf-/Nachname bzw. Beziehung mit Synonymen (Papa = Vater, Oma = Großmutter, „Oma
     (mütterlicherseits)“ beginnt mit „Oma“) und „ich“ (Person „Das bin ich“). Mehrdeutig oder unbekannt → **Rückfrage**
     mit Gesicht, Beziehung und Fotozahl („Beide“ bei 2–4 Treffern, „Ohne diese Person“); die Antwort merkt sich Lumora als
     **Alias** (`AssistantAliases`, je Gruppe, mehrere Personen = „beide“), änderbar bzw. „Vergessen“ unter Einstellungen ›
     Assistent (das Mockup sagt „unter Personen“ — dort gibt es noch keinen Platz, siehe Konsequenzen).
   - **Zeitraum**: absolut; „letzte N Jahre“ = heute − N Jahre + 1 Tag bis heute (in der Karte als konkrete Daten);
     **Alter** aus dem Geburtsdatum (frühester möglicher Tag bis zum Tag vor dem (bis+1)-ten Geburtstag; nur Jahr/Monat
     bekannt → großzügig); fehlt das Geburtsdatum → Rückfrage statt Raten; **wiederkehrende Tage** jedes Jahr (auch über den
     Jahreswechsel). Genau geprüft wird der Kalendertag in Ortszeit der Aufnahme (wie die Storys).
   - **Begriffe** gegen Tags, Kategorien (Name → alle Werte; Wert), Motive, Personen, Länder/Regionen der Orte im Bereich
     und die **Offline-Ortssuche** (nur bei exakt gleichem Namen, Rechteck ± 20 km). Mehrere Treffer werden **vereinigt
     (ODER)** und in der Karte mit Zahlen gezeigt („Barcelona: Tag (212) · Ort (180)“), auf einen umschaltbar; kein Treffer
     → **„nicht gefunden“** markiert (wirkt nicht, wird nicht still weggelassen). Treffer ohne Fotos im Bereich nur, wenn es
     keine anderen gibt. Mehrere Begriffe zusammen (UND), umschaltbar auf „einer reicht“.
   - **Urlaub/Reise**: Dateien der Reise-Storys der Gruppe (Reise-Erkennung, ADR-0058) plus passende Tags/Kategorien
     („Urlaub“, „Reisen“, „Ferien“ …) — Standard vereinigt, einzeln abschaltbar. Ist kein Zuhause **eingestellt**
     (automatisch), fragt Lumora: „Zuhause ist Graz“ (speichert `stories.home`), „In Einstellungen › Storys festlegen“, „Ohne
     Reise-Erkennung“ — bis zur Antwort wirkt die Reise-Erkennung nicht.
   - **„Die besten N“** über `IHighlightSelector` (Schärfe, Gesichter, Doppelte/Serien nur einmal) aus höchstens 4 000
     gleichmäßig über die Zeit verteilten Treffern. **„Die schönsten“ ohne Zahl**: bis 24 Treffer alle, sonst rund 10 % auf
     Fünfer gerundet, mindestens 24, höchstens 100 — ein Album/eine Story bleibt überschaubar, eine kleine Auswahl wäre
     „zu dünn“; in der Karte mit einem Klick 24/50/100/200/alle.
   - Alles nur im aktuellen Bereich (`ILibraryScope`, ADR-0048).
5. **Sachliche Überschrift** (`AssistantTitleBuilder`, mit `StoryTitleBuilder`): Namen („Nicole und Nina“), Begriffe in den
   Worten des Benutzers bzw. der Ortsname, wiederkehrende Tage, „Urlaub“, Jahre **der gewählten Fotos** („Nicole und Nina ·
   Urlaub 2023–2025“, „Anna · 1985–2005“). Für die Story ist sie der eigene Titel (über „Titel ändern“ zurücksetzbar), für das
   Album der Name (eindeutig gemacht). Nie Text der Sprach-KI.
6. **Ollama** (`OllamaClient`, `OllamaIntentModel`): nur `http://127.0.0.1:11434` (der Konstruktor lehnt andere Adressen
   ab), `/api/chat` mit `format` = Schema, `temperature 0`, `seed 1`, `num_predict 600`, Zeitlimit 120 s (inkl. Laden),
   `keep_alive 60s` (danach gibt Ollama den Grafikspeicher frei), „Grafikkarte nutzen“ aus → `num_gpu 0`. Fehler werden zu
   verständlichen Meldungen (Server weg, Modell fehlt, Zeitlimit, Serverfehler). Die Ausgabe von winget und Ollama geht nie
   ins Protokoll.
7. **Modellwahl** (konfigurierbar, `assistant.model`): Standard **`qwen2.5:3b`** (Qwen 2.5 3B Instruct, ≈ 1,9 GB, ≈ 2,5 GB
   Grafikspeicher, ≈ 1–3 s je Satz auf einer 1080 Ti, gutes Deutsch, zuverlässige strukturierte Ausgabe; Lizenz Qwen Research
   License — frei für private, nicht-kommerzielle Nutzung, wie buffalo_l), Alternative **`qwen2.5:7b`** (≈ 4,7 GB, Apache 2.0,
   zuverlässiger bei langen Sätzen, ≈ 3–6 s). Beide passen mit Gesichts- (≈ 0,3 GB) und Motiv-Modell (≈ 0,4 GB) in 11 GB;
   Pascal (Compute 6.1) wird von Ollama unterstützt.
8. **Installation und Download nur nach Bestätigung**: „Ollama installieren…“ zeigt Name (`Ollama.Ollama`), Hersteller,
   Quelle (winget), Größe (ca. 1 GB), Lizenz (MIT) und ruft erst auf „Zustimmen und installieren“ `winget install --id
   Ollama.Ollama -e --accept-package-agreements --accept-source-agreements` auf (Muster ADR-0061, `IProcessOutputRunner`).
   „Herunterladen…“ fragt erst nach dem Klick die **genaue Größe** aus dem Manifest der Ollama-Registry (`registry.ollama.ai`,
   gesendet wird nur der Modellname; offline → ungefähre Größe), zeigt Name, Quelle, Größe, Lizenz und lädt nach der
   Bestätigung über `/api/pull` mit Fortschritt und Abbrechen (Ollama setzt später fort). Sonst gibt es keinen Netzverkehr
   außer zu 127.0.0.1.
9. **Ohne Ollama**: die Leiste zeigt den Zustand („nicht installiert“/„läuft nicht“/„Modell fehlt“) mit dem Weg in
   Einstellungen › Assistent; **regelbasierter Rückfall** (`SimpleIntentParser`) für klare Sätze — bekannte Namen und
   Beziehungen, „ich“, Begriffe aus dem Wortschatz, Jahre, „die letzten N Jahre“, „die besten N“, Urlaub, Aktionswort; bleibt
   ein Wort übrig, das kein Füllwort ist, gibt er auf („dafür braucht es die Sprach-KI“) statt zu raten. Er greift auch, wenn
   die KI ungültig oder abgeschnitten antwortet.
10. **Grafikkarte**: Ollama ist ein fremder Prozess. Solange eine Anfrage läuft, hält der Assistent die gemeinsame Sperre
    `IGpuGate` (ADR-0052) — Gesichts- und Motiv-Index rechnen Foto für Foto unter derselben Sperre und warten so zwischen
    zwei Fotos, bis der Satz übersetzt ist (nacheinander statt gleichzeitig). Ohne „Grafikkarte nutzen“ keine Sperre.
11. **Collage-Bild** (`CollageLayout` rein und getestet, `CollageService`, `ICollageRenderer` mit `WpfCollageRenderer` im
    Wpf-Projekt): Format 16:9 / A4 quer / A4 hoch / Quadrat, Größe Full HD / 4K (lange Kante 1920 / 3840) / Druck 300 dpi
    (lange Kante 3508 = A4 bei 300 dpi, dpi in den Metadaten), 12/24/36/48 Fotos, Anordnung **Großes Titelbild** (bestes
    Foto auf 2 × 2 Feldern, unvollständige letzte Reihe gestreckt), **Raster** (Zeilen fast gleich voll, Felder ≈ 4:3),
    **Zeitstrahl** (Aufnahme-Reihenfolge, Breite nach Seitenverhältnis); ganze Pixel, keine Überlappung, lückenlos (jede
    Reihe füllt die Breite, die Reihen die Höhe, nur Fugen). Fotos (keine Videos) aus den **Originalen**, je Platz passend
    dekodiert, EXIF-Ausrichtung, mittig beschnitten; sachliche Überschrift (änderbar, an/aus), Datum je Foto; JPEG (92) oder
    PNG nach Endung. Gerendert auf eigenem STA-Thread, erst Temp-Datei im Zielordner, dann umbenennen. „Neu mischen“ wählt
    zufällig aus dem besseren Teil des Vorrats (bis 144 gute Fotos), ein Klick tauscht ein Foto. Dateidialog ohne „Zuletzt
    verwendet“. Der Menüpunkt **„Als Collage-Bild speichern…“** der Story-Ansicht ist jetzt aktiv (Highlights, sonst alle
    Fotos der Story).
12. **Versteckte Bibliothek**: alle Dienste je Katalog-Sitzung — Verlauf, Aliase und Einstellungen liegen nur im versteckten
    Katalog, der Wortschatz kommt nur von dort; die Collage lässt sich nur unterhalb eines Bibliotheksordners im Container
    speichern (Standard `<Bibliothek>\Collagen`), alles andere lehnt der Dienst ab.
13. **Protokoll**: nie der Satz, nie Namen — nur „Assistent: Anfrage verstanden (Sprach-KI|Regeln) — n Personen, n Begriffe,
    n Rückfragen“, „Assistent: Anfrage, n Treffer“, Dauer, Ids.
14. **Daten** (Migration `AddStoryAssistant`, nur CREATE TABLE): `AssistantHistory` (Satz, Absicht als JSON, Aktion,
    Trefferzahl; je Gruppe höchstens 50, gleicher Satz nur aufgefrischt; „Öffnen“ rechnet aus der gespeicherten Absicht ohne
    KI neu) und `AssistantAliases` (Begriff → Person, ON DELETE CASCADE). Beide mit `GroupId` ohne Fremdschlüssel, in
    `LibraryGroupStore.TermTables`; Verknüpfen: Antworten der Zielgruppe gewinnen bei gleichem Begriff, Personen-Zusammenführung
    nimmt Antworten mit, Verlauf wandert; Lösen: Kopien (Antworten auf die kopierten Personen); leere Gruppe: weg.
    Einstellungen `assistant.enabled|model|useGpu|history` je Katalog.
15. **Oberfläche** (Mockup 12): Leiste über der Story-Übersicht (höchstens zwei Drittel der Höhe, scrollt selbst) mit
    Beispielen (ausgeblendet, solange eine Karte offen ist), Hinweis „läuft nur auf diesem PC“, Fortschritt mit „Abbrechen“;
    Karte mit änderbaren Punkten (Klick öffnet ein Menü: Person tauschen/entfernen, alle zusammen ↔ mindestens eine, weitere
    Personen, Zeitraum ändern/ohne, Urlaub-Quellen, Begriff je Treffer/ändern/entfernen, Medien, Auswahl, Was entsteht inkl.
    „+ Story“ bei der Collage), Trefferzahl, Vorschau-Streifen (8 gute Fotos), **Erstellen / Nur Fotos zeigen (Galerie) /
    Satz ändern / Abbrechen**; Rückfrage-Karte; „Zuletzt gefragt“ (3, Öffnen bzw. „Wieder abspielen“, löschen). Reiter
    **Einstellungen › Assistent** nach „Storys“. Rauchtest-Schritt „Story-Assistent“ mit Attrappe statt Ollama.

## Begründung

- Die KI nur als **Übersetzer in Filter** zu nutzen hält alles Persönliche in Lumora: die Auflösung (Geburtsdaten, Orte,
  Gesichter) läuft auf dem Katalog, die KI braucht keine Daten außer Namen. Eine Absicht mit festem Schema ist prüfbar,
  speicherbar (Verlauf ohne erneute KI-Anfrage) und testbar ohne Modell.
- **Rückfrage statt Raten** und „nicht gefunden“ statt still weglassen: ein falsch verstandener Name wäre bei einem Album mit
  hunderten Fotos teuer; die Antwort wird gemerkt, damit dieselbe Frage nicht wiederkommt.
- Ollama statt eingebettetem Modell: ein fremder, vom Benutzer bestätigter Dienst ohne neues NuGet-Paket und ohne
  Modelldateien im Programm; Lumora spricht nur lokales HTTP. Ein 3B-Modell reicht für die reine Übersetzung, weil Lumora
  die eigentliche Arbeit macht.
- Collage als reine Layout-Funktion + Renderer hinter einem Vertrag: Tabellentests für Maße, Überlappung und Lückenlosigkeit
  ohne WPF; die Services-Schicht bleibt WPF-frei (CLAUDE.md).

## Konsequenzen

### Positiv
- Herberts Beispielsätze laufen ohne Cloud; ohne Ollama verstehen die Regeln klare Sätze („Anna 2019“, „Album mit allen
  Fotos vom Hund 2022“).
- Nichts entsteht ohne Bestätigung; die Karte zeigt jeden Punkt, Zahlen und Herkunft („aus dem Geburtsdatum von Anna“).
- Der Collage-Menüpunkt der Story-Ansicht ist aktiv.

### Negativ
- Gemerkte Antworten lassen sich bisher nur unter Einstellungen › Assistent ansehen und vergessen (Mockup: „unter Personen“).
- „Nur Fotos zeigen (Galerie)“ nutzt den Datei-Filter der Galerie aus ADR-0060 (Chip „Kartenausschnitt (n Dateien)“).
- Personen-Bezüge zählen nur **bestätigte** Gesichter; Vorschläge bleiben außen vor.
- Ortsrechteck ± 20 km statt Kreis/Region; Länder und Regionen nur, wenn sie im Bereich vorkommen.
- Die Qualität hängt am Modell; Feinschliff am Prompt erst mit echten Sätzen von Herbert (offen, CHANGELOG).

### Neutral
- Neue Tabellen `AssistantHistory`, `AssistantAliases` (PLAN.md Kap. 4); keine neuen NuGet-Pakete.
- Rauchtest-Schritt „Story-Assistent“ nach „Story-Seite“, vor „Versteckte Bibliothek“, mit Screenshots 67–68; Attrappe
  (`RuleIntentModel`, `ReadyAssistantRuntime`) nur mit `LUMORA_SMOKE_ROOT`.

## Alternativen

- **Cloud-LLM** → verworfen (Herbert: nichts verlässt den Rechner, keine Schlüssel).
- **Modell in Lumora einbetten (ONNX/llama.cpp-Bindung)** → verworfen: großes neues Paket, Modelldatei im Programm, GPU-Pflege
  in Lumora; Ollama ist ein bestätigter, austauschbarer Dienst.
- **KI schreibt SQL bzw. Galerie-Filter direkt** → verworfen: nicht prüfbar, bräuchte Ids und Schema-Wissen.
- **KI schreibt Titel/Texte** → verworfen (Herbert: kein erfundener Text).
- **Regel-Parser als Hauptweg** → nur als Rückfall: freie Sätze („aus meiner Kindheit“, „Weihnachten“) brauchen Sprachverständnis.
- **Collage mit beliebigen Freiformen** → verworfen: drei feste, lückenlose Anordnungen sind testbar und sehen ruhig aus.

## Referenzen

- Code: `src/Lumora.Services/Assistant/**`, `src/Lumora.Wpf/Stories/Assistant/**`, Rauchtest
  `src/Lumora.Wpf/Shell/Smoke/SmokeScenario.StoryAssistant.cs`, Migration `AddStoryAssistant`
- Tests: `tests/Lumora.Tests/Assistant/**`, `tests/Lumora.Wpf.Tests/Stories/Assistant/**`

# ADR-0057: Foto-Güte und Highlights — Schärfe/Belichtung aus dem Vorschaubild, Bildwirkung aus den Motiv-Vektoren, Auswahl mit Serien und Zeit-Quoten

- **Status:** Accepted
- **Datum:** 2026-10-22
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md) (Vorschaubilder 400 px),
  [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) (Bereich), [ADR-0052](0052-motive-siglip2-lokal.md)
  (SigLIP-2-Bild-Vektoren), [ADR-0053](0053-doppelte-erkennen.md) (pHash, Prüfsumme, Hintergrund-Index als Muster),
  [Spike #16](../spikes/16-foto-guete.md), Mockup `mockups/09-storys.html`

## Kontext

Welle 6 „Storys“ (Mockup 09, abgenommen) zeigt je Story eine Auswahl „Highlights“ (Leiste Highlights/Alle Fotos, „warum
ausgewählt“ beim Überfahren, „+4 ähnliche“ bei Serien, Ende-Kasten „64 von 421 Fotos als Highlights · 37 ähnliche
weggelassen“) und ein Titelbild, das nie unscharf oder dunkel ist, wenn es eine Alternative gibt. Dafür braucht jedes
Foto einen Güte-Wert — ohne neues Modell, ohne Download, ohne neues Paket, für 50 000–500 000 Dateien im Hintergrund — und
einen Dienst, der aus einer Dateiliste die besten auswählt. Die Story-Seite selbst baut ein anderer Auftrag; beide
verbindet der Vertrag `IHighlightSelector`.

## Entscheidung

1. **Tabelle `FileQuality`** (Migration `AddFileQuality`, nur CREATE TABLE): `FileId` PK → Files ON DELETE CASCADE,
   `Sharpness`, `Exposure`, `Aesthetic` (REAL NULL), `AestheticVersion` (Modell + Prompt-Satz), `Version` (Verfahren),
   `SourceSize`, `SourceModifiedAt`, `ErrorCode`. Ein Wert gilt nur, solange Größe und Änderungszeit der Files-Zeile gleich
   sind und die Version passt (Muster `FileFingerprints`); ein Incremental-Scan muss nichts löschen. Kein zusätzlicher
   Index: Zählen und Stapel laufen über `Files` (Bibliothek) mit Primärschlüssel-Lookup.
2. **Schärfe** = Laplace-Operator (4er-Nachbarschaft) auf der Helligkeit (BT.601, Festkomma) des Vorschaubilds, geladen
   auf längste Kante 512 (Vorschaubild 400 × 533 → fast unskaliert); Varianz je Kachel eines 4 × 4-Rasters, Mittel der
   vier schärfsten Kacheln (ein Porträt mit unscharfem Hintergrund zählt nach dem Gesicht). Normiert logarithmisch
   zwischen Varianz 5 (→ 0) und 8 000 (→ 1); Stichprobe Handy-Katalog: p10 0,60 · p50 0,79 · p90 0,91, weichgezeichnet
   p50 0,11. **Belichtung** = Glockenkurve der mittleren Helligkeit um 0,46 (σ 0,2) × (1 − 1,5 · Anteil ausgefressen über
   2 % − 1,5 · Anteil abgesoffen über 5 %).
3. **Eigener Durchlauf neben dem pHash.** Der pHash (ADR-0053) lädt auf 128 px (≈ 1,3 ms) — zu klein für Schärfe; ein
   gemeinsames Laden auf 512 px hätte das Eingangsbild des pHash verändert (andere Fingerabdrücke → Versionswechsel, also
   Umbau von ADR-0053). Gemessen: 1 000 Vorschaubilder 7,0 s (Laden 3,1 ms + Rechnen 3,9 ms im Debug-Build) → **500 000 ≈
   58 min** auf einem Kern im Hintergrund, einmalig.
4. **Hintergrund-Index `FileQualityIndexService`** im Muster `DuplicateIndexService.Images`: 20 s nach dem Start, nach
   jedem Scan, bei neuen Dateien, beim Bereichswechsel (laufender Durchgang wird abgebrochen) und 30 s nach neuen
   Motiv-Vektoren; nur Dateien des aktuellen Bereichs (`ILibraryScope`: aktive Bibliothek bzw. „Alle verknüpften“), wie
   die Motive. Stapel zu 100 mit einer Transaktion (Single-Writer), ruht bei Scan-Sperre und während die Katalog-DB
   umzieht, liest keine Cloud-Platzhalter und keine Dateien nicht erreichbarer Wurzeln; Bilder ohne Vorschaubild aus dem
   lokalen Original, Videos nur über ihr Vorschaubild. Nicht dekodierbar → `ErrorCode = 'decode'` (kein Dauer-Wiederversuch);
   gesperrte oder gerade geschriebene Dateien (IO-Fehler) werden nicht gemerkt, sondern beim nächsten Durchgang wieder
   versucht. Geschriebenes wird auch nach Abbruch gemeldet (`FileQualityChangedMessage`).
   Statusleiste „Foto-Güte: x von y bewertet“ bzw. „Foto-Güte: Bildwirkung x von y“; `CancelPass()` bricht ab.
5. **Bildwirkung ohne neues Modell**: Zero-Shot über die vorhandenen SigLIP-2-Bild-Vektoren (`FileEmbeddings`, float16).
   Positiv „a beautiful, sharp, well-exposed photo“ / „ein schönes, scharfes, gut belichtetes Foto“ / „ein gelungenes
   Urlaubsfoto mit schönen Farben“, negativ „a blurry, dark, failed photo“ / „ein verschwommenes, dunkles, missglücktes
   Foto“, je Seite gemittelt; Wert = σ((cos⁺ − cos⁻ + 0,0045) · 105). Von sieben gemessenen Sätzen der beste:
   weichgezeichnete Fassungen in 100 %, abgedunkelte in 98 % niedriger (ein Satz mit „Zufallsfoto vom Boden“ und
   „great composition“ nur 95 %/83 %, „a good/bad photo“ nur 65 %/66 %). Die Text-Vektoren rechnet das Sprachmodell
   einmal je Modell und legt sie in `Settings["quality.aestheticVectors"]` ab; das Modell wird nur geladen, wenn es
   tatsächlich Vektoren ohne Bildwirkung gibt; scheitert es (defekte Datei), bleibt die Bildwirkung für diese Sitzung leer.
   Ohne Modell/Vektor bleibt `Aesthetic` NULL — alles andere funktioniert.
6. **Vertrag `IHighlightSelector`** (`SelectAsync`, `PickCoverAsync`, `HighlightOptions(Share 0,15, Min 6, Max 80)`,
   `HighlightPick(FileId, Score, Reason, SimilarCount)`) — die Datei ist zwischen Foto-Güte und Storys wortgleich.
   Registrierung mit `services.Replace(…QualityHighlightSelector)`, die Storys legen eine einfache Ersatz-Implementierung
   per `TryAdd` an — diese gewinnt in jeder Reihenfolge.
7. **Auswahl (`HighlightPlanner`, rein rechnerisch)**: Wert = Güte (Schärfe halb relativ zur Story, halb absolut;
   Belichtung; Bildwirkung, falls vorhanden — Gewichte 0,40/0,25/0,35, ohne Bildwirkung 0,60/0,40) + bestätigte Personen
   (`Faces.State = 2`, 0,10 + 0,04 je weitere bis 3, × Gesichtsqualität) + Favorit 0,15 + Bewertung 0,03 je Stern über 1
   + Video 0,05 (Abwechslung) − 0,25 für fast einfarbige Bilder. **Serien/Doppelte**: gleiche gültige Prüfsumme immer
   eine Gruppe; pHash-Abstand ≤ „ähnlich“-Schwelle der Doppelten (Standard 10) UND Aufnahmezeit-Abstand ≤ 60 s → Serie
   (Ketten gewollt, aber eine Serie umfasst höchstens 5 min — Intervallaufnahmen werden keine endlose Kette); je Gruppe
   nur das beste Foto, `SimilarCount` = Rest (byte-gleiche Kopien zählen im Text „Serie von N“ einmal). **Anzahl** k = Anteil ×
   Dateien in [Min, Max], höchstens so viele wie Gruppen. **Verteilung**: je Aufnahmestunde höchstens ihr Anteil an k
   (aufgerundet, mindestens 1), erst die besten unter dieser Quote, dann nach Wert auffüllen. Ausgabe in
   Eingabe-Reihenfolge; `Reason` aus den stärksten Gründen („beste aus einer Serie von 5“, „scharf“, „2 Personen“ bzw.
   „Gesicht gut sichtbar“, „Favorit“, „5 Sterne“, „starke Bildwirkung“, „gut belichtet“, „Video“; höchstens drei).
   **Titelbild**: Fotos vor Videos; ausgeschlossen, wer im unteren Viertel bzw. unter 0,35 Schärfe oder 0,45 Belichtung
   liegt; liegen alle dort, dann wenigstens nie das unschärfste und nie das dunkelste. Gemessen: `SelectAsync` für 5 000
   Dateien **Median 87 ms** (unter Last der ganzen Test-Suite 128 ms; Ziel < 200 ms), `PickCoverAsync` 92 ms.
8. **„Neu aufbauen“: keine Mitnahme.** `FileQuality` geht wie die Fingerabdrücke per Kaskade mit und wird neu
   berechnet. Mitnehmen hieße jede bewertete Datei in die Sicherungs-Tabelle von `UserDataCarryOver` aufzunehmen
   (heute nur Dateien mit Benutzerdaten) — bei 500 000 Dateien teurer in der Schreibtransaktion als der Neuaufbau im
   Hintergrund (≈ 1 h, ohne Benutzerdaten-Verlust). Die Bildwirkung kommt danach ohne Modell-Lauf zurück, weil die
   Motiv-Vektoren mitgenommen werden. Datei-Operationen brauchen nichts: der Abzug der Doppelten (ADR-0053) liest die
   Kaskaden-Tabellen aus dem Schema und nimmt `FileQuality` von selbst mit.

## Begründung

- Schärfe und Belichtung sind billig aus dem fertigen Vorschaubild zu rechnen und reichen, um missglückte Fotos (verwackelt,
  falsch fokussiert, viel zu dunkel) auszusortieren — mehr verlangt das Mockup nicht. Die Kachel-Variante behandelt
  Porträts mit Freistellung richtig, ohne Gesichtserkennung vorauszusetzen.
- Die Bildwirkung nutzt ohnehin vorhandene Vektoren: kein Bild wird zusätzlich geladen, kein Modell läuft im Durchgang.
  Das ersetzt kein echtes Ästhetik-Modell, trennt aber gut von schlecht (Gegenproben) und ist nur schwach mit der Schärfe
  korreliert (Spearman 0,24) — sie bringt also eigene Information.
- Relative Schärfe/Bildwirkung innerhalb der Story macht die Auswahl unabhängig von Kamera und Katalog; der absolute Anteil
  verhindert, dass in einer durchweg scharfen Story die etwas weicheren abgestraft werden.
- Stunden-Quoten sind einfach, vorhersagbar und testbar; sie verhindern, dass eine einzelne ergiebige Stunde alle
  Highlights stellt, ohne schwache Stunden zu erzwingen (aufgefüllt wird nach Wert).

## Konsequenzen

### Positiv
- Storys bekommen Highlights, Serien-Hinweis, „warum ausgewählt“ und ein sicheres Titelbild über einen festen Vertrag.
- Katalog + ≈ 60 Bytes je Datei (500 000 ≈ 30 MB); keine neue Abhängigkeit, kein Download.

### Negativ
- Erster Durchgang ≈ 1 h für 500 000 Dateien (Debug-Build, ein Kern, Hintergrund); Dateien ohne Vorschaubild lesen das
  Original (≈ 40 ms).
- Die Bildwirkung hängt am Motiv-Index: ohne Modell bleibt sie leer, die Gewichte verschieben sich dann auf Schärfe und
  Belichtung.
- Sehr bewusst helle (Schnee) oder dunkle (Nacht) Fotos bekommen eine niedrigere Belichtung; Bildwirkung und Personen
  gleichen das teilweise aus.

### Neutral
- Wie bei den Fingerabdrücken (ADR-0053): ein Video, dessen Vorschaubild-Zeile „erzeugt“ meldet, dessen Datei aber fehlt,
  bleibt offen und wird je Durchgang übersprungen, bis das Vorschaubild neu entsteht.
- Das Gerüst des Hintergrund-Index (Scan-Sperre, Katalog-Umzug, offline Wurzeln) ist bewusst eine Kopie des Musters aus
  `DuplicateIndexService`; ein gemeinsamer Baustein für alle Hintergrund-Indizes wäre ein eigener Umbau.
- Versteckte Bibliotheken: eigener Katalog, eigene `FileQuality` und eigener Settings-Eintrag; nichts außerhalb.
- Ändert sich ein Verfahren, `ImageQuality.Version` bzw. `AestheticPrompts.PromptVersion` erhöhen — dann wird neu gerechnet.

## Alternativen

- **Gemeinsamer Durchlauf mit dem pHash** (einmal auf 512 laden, für den pHash verkleinern) → verworfen: verschiebt die
  Fingerabdrücke (Umbau ADR-0053), und der pHash lädt auf 128 px fünfmal schneller.
- **Eigenes Ästhetik-Modell (NIMA, LAION-Aesthetic)** → verworfen: Download und Lizenz, Auftrag „ohne neues Modell“.
- **Ein einzelnes Prompt-Paar „a good/bad photo“** → gemessen deutlich schwächer (65 %/66 %).
- **Globale statt Kachel-Schärfe** → Porträts mit unscharfem Hintergrund fielen durch.
- **Zeitliche Gleichverteilung (k gleich große Zeitfenster)** → zwingt Highlights in leere oder schwache Abschnitte;
  Quoten nach Fotoanzahl je Stunde folgen dem tatsächlichen Geschehen.
- **Werte über „Neu aufbauen“ mitnehmen** → siehe Punkt 8, vertagt.

## Referenzen

- Code: `src/Lumora.Services/Quality/**`, `src/Lumora.Domain/Entities/FileQuality.cs`,
  `src/Lumora.Data/EntitiesMapping/QualityConfiguration.cs`, Statusleiste `src/Lumora.Wpf/Shell/StatusBarViewModel.Quality.cs`
- Tests: `tests/Lumora.Tests/Quality/**`, Messung `tests/Lumora.Wpf.Tests/Quality/QualityMeasurementTests.cs`
- [Spike #16](../spikes/16-foto-guete.md)

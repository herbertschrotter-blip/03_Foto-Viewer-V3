# ADR-0053: Doppelte erkennen — SHA-256 mit Größen-Vorfilter, pHash aus dem Vorschaubild, Aufräumen mit Rückgängig

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter, Claude
- **Bezogen auf:** [ADR-0012](0012-operation-logging.md) (Datei-Operationen mit Protokoll, Papierkorb),
  [ADR-0025](0025-scan-sperre-fuer-datei-operationen.md) (Scan-Sperre), [ADR-0041](0041-galerie-filter-unabhaengig-kombinierbar.md)
  (Filterkarte), [ADR-0042](0042-navigation-und-einstellungen-mit-reitern.md) (Navigation), [ADR-0043](0043-vorschaubilder-kurze-kante-und-bildmasse.md)
  (Vorschaubilder 400 px), [ADR-0046](0046-aufnahmedatum.md) (Nachtragen im Hintergrund), [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md)
  (Bibliotheks-Trennung), [ADR-0049](0049-versteckte-bibliotheken.md) (versteckte Bibliotheken), [Spike #15](../spikes/15-doppelte.md)

## Kontext

Herbert (2026-10-21): „Ein Modus, um doppelte Bilder zu erkennen, z. B. über Hash.“ Kataloge haben 50 000–500 000
Dateien, viele Fotos liegen doppelt (Handy-Sicherung im Eingang und schon in der Bibliothek, WhatsApp-Kopien, verkleinert
verschickte Bilder, Serienaufnahmen). Gesucht sind zwei Arten: **byte-gleiche** Dateien und **ähnliche** Bilder. Löschen
darf nur nach Bestätigung geschehen, über den bestehenden Weg der Datei-Operationen (Papierkorb, Protokoll) und muss
rückgängig zu machen sein; Bewertung, Favorit, Tags, Kategorien und Personen der gelöschten Datei sollen nicht verloren
gehen. Alles gilt nur im aktuellen Bibliotheks-Bereich; OneDrive-Platzhalter dürfen nicht heruntergeladen werden.

## Entscheidung

1. **Exakt gleich = SHA-256 über alle Bytes, nur für Dateien gleicher Größe.** Eine Datei mit einmaliger Größe kann kein
   exaktes Duplikat haben. In Herberts Handy-Katalog teilen 13 % der Dateien ihre Größe mit einer anderen (Spike 15) —
   nur sie werden gelesen. SHA-256 statt xxHash128: im .NET-Framework enthalten (kein neues Paket), mit den SHA-Befehlen
   der CPU schneller als die Platte liefert (gemessen 353 MB/s, plattengebunden) und als kryptografische Prüfsumme auch
   für den späteren Abgleich mit dem Handy geeignet. Die Prüfsumme gilt nur für die gelesene Dateiversion (`ContentSize`,
   `ContentModifiedAt` = Werte der Files-Zeile); eine geänderte Datei fällt bis zum nächsten Durchgang aus allen Gruppen.
2. **Ähnlich = 64-Bit-pHash (DCT, wie `imagehash.phash`) aus dem Vorschaubild**: Helligkeit, flächengetreu auf 32 × 32,
   2-D-DCT, 8 × 8 niedrigste Frequenzen, Bit = über dem Median. Eingang ist das fertige Vorschaubild (kurze Kante 400 px,
   EXIF-Drehung angewandt) auf längste Kante 128 geladen (≈ 1 ms); fehlt es, das lokale Original (≈ 43 ms); Videos nur über
   ihr Vorschaubild. Fast einfarbige Bilder (Standardabweichung der 32 × 32-Helligkeit < 3) werden markiert und zählen nie
   als ähnlich (ihr Fingerabdruck ist Rauschen). Abstand = Hamming. Gemessen an 400 Handyfotos: verkleinert + neu
   gespeichert p95 4 (max 8), 3 % beschnitten Median 6 / p95 10, heller p95 2; fremde Fotos im Median 32.
   **Standard-Schwelle 10**, einstellbar 1–12 (Regler auf der Seite, gespeichert in `duplicates.similarThreshold`).
3. **Gruppen ähnlicher Bilder: Mehrfach-Index-Hashing + Stern-Gruppen.** Vier 16-Bit-Bänder mit je einem Bucket-Feld
   (Zählsortierung); bei Abstand ≤ r unterscheiden sich zwei Fingerabdrücke in mindestens einem Band in ≤ ⌊r/4⌋ Bits —
   gesucht werden je Band alle Schlüssel in diesem Abstand (Ergebnis = Vollvergleich, im Test geprüft). Gruppen nach dem
   Stern-Verfahren: der Reihe nach (ältere FileId zuerst) wird jedes freie Bild Mittelpunkt, alle freien im Abstand ≤ r
   gehören dazu — keine Ketten A~B~C, in denen Anfang und Ende nichts gemein haben. 500 000 synthetische Fingerabdrücke:
   r = 10 ≈ 5 s, 100 000 ≈ 0,2 s. Ein BK-Baum wurde verworfen (bei r ≈ 10 besucht er einen Großteil des Baums).
4. **Schema — Migration `AddDuplicateDetection`** (nur CREATE TABLE): `FileFingerprints (FileId PK → Files ON DELETE
   CASCADE, ContentHash BLOB, ContentSize, ContentModifiedAt, ImageHash INTEGER (vorzeichenbehaftet gespeichert),
   ImageHashVersion, ImageModifiedAt, ImageLowDetail, ImageError)` mit `IX_FileFingerprints_ContentHash WHERE ContentHash
   IS NOT NULL`; `DuplicateCleanupUndo (Id, BatchId → OperationBatches CASCADE, FileId, KeptFileId, Snapshot, Transfer,
   CreatedAtUtc, UndoneAtUtc)`. Eigene Tabelle statt Files-Spalten: kein Tabellen-Neuaufbau, und „Neu aufbauen“ löscht die
   Fingerabdrücke per Kaskade mit (sie entstehen neu).
5. **Hintergrund-Index** (`IDuplicateIndexService`, Muster Aufnahmedatum): nach jedem Scan, bei neuen Dateien und 15 s nach
   dem Start; erst Prüfsummen (Größen der Kollisionen als TEMP-Tabelle der Lese-Verbindung, Stapel zu 100 nach FileId),
   dann Bild-Fingerabdrücke (niedrige Thread-Priorität); eine Transaktion je Stapel (Single-Writer), ruht bei Scan-Sperre
   und während die Katalog-DB umzieht, liest keine Cloud-Platzhalter und keine Dateien nicht erreichbarer Wurzeln.
   Lesefehler werden je Dateiversion gemerkt (kein Dauer-Wiederversuch). Statusleiste „Doppelte: Prüfsummen n / m“.
6. **Seite „Doppelte“** als eigener Navigationseintrag zwischen „Orte“ und „Sortieren“ (wie Personen/Orte eine
   Arbeitsseite, kein Einstellungs-Reiter): Umschalter „Exakt gleich | Ähnlich“ (Schwelle als Regler), Filter „Eingang
   schon in der Bibliothek“, Gruppen als Karten mit den Dateien nebeneinander (Vorschaubild, Auflösung, Größe,
   Aufnahmedatum, Ordner, Bewertung/Favorit, Bibliothek/Eingang, bei „ähnlich“ Abstand), virtualisiert.
7. **Vorschlag „behalten“ — feste, sichtbare Regel**: höhere Auflösung → Aufnahmedatum aus der Datei (nicht nur aus dem
   Dateinamen) → liegt schon in der Bibliothek statt im Eingang → mehr eigene Angaben (Bewertung, Favorit, Tags,
   Kategorien, Personen) → größere Datei → länger im Katalog. Die Regel steht auf der Seite, jede Gruppe nennt das
   entscheidende Kriterium (gegenüber der nächsten nicht byte-gleichen Datei); die Wahl ist per Radio-Knopf änderbar, jede
   Gruppe abwählbar.
8. **„Übrige in den Papierkorb“ nur nach Bestätigung, über `IFileOperationService.RecycleAsync`** (ein Recycle-Batch mit
   Protokoll, Katalog/Vorschaubild/FTS konsistent, Scan-Sperre). Vorher, unter der Scan-Sperre: je gelöschter Datei ein
   **Abzug** ihrer Files-Zeile und aller per Kaskade abhängigen Zeilen (aus dem Schema gelesen: Tags, Kategorien,
   Gesichter samt abgelehnter Vorschläge, Gesichts- und Motiv-Merker, Fingerabdrücke — künftige Tabellen wie Alben kommen
   von selbst dazu; nicht die Vorschaubild-Zeilen) und — Option **„Angaben übertragen“, Standard an** — die Übertragung auf
   die behaltene Datei: Favorit (oder), Bewertung (höhere), fehlende Tags und Kategorie-Werte (ein vorhandener Wert der
   behaltenen bleibt), Personen: ist die behaltene Datei analysiert, bekommt ihr Gesicht mit der besten Überdeckung
   (IoU ≥ 0,3) die Person; ist sie es nicht und die Dateien sind byte-gleich, werden Gesichter und Merker kopiert; sonst
   „nicht übertragbar“ (gemeldet). Abzug und Übertragung stehen in `DuplicateCleanupUndo`.
9. **„Rückgängig“ auf der Seite** (die Galerie-Taste bleibt bei Papierkorb-Batches wirkungslos, wie bisher): neue
   Methode `IFileOperationService.RestoreRecycledAsync` holt die Dateien aus dem Windows-Papierkorb zurück (eigene
   Auswertung der `$I`/`$R`-Paare unter `<Laufwerk>\$Recycle.Bin\<SID>`, keine Shell-Oberfläche; `IRecycleBin.
   RestoreFromRecycleBin`), legt die Files-Zeile **mit derselben FileId** und alle abhängigen Zeilen aus dem Abzug wieder an
   (die Tabellen sind AUTOINCREMENT — gelöschte Ids werden nie neu vergeben; Zeilen mit inzwischen fehlendem Bezug werden
   einzeln übersprungen) und nimmt die Übertragung an der behaltenen Datei zurück (nur, was seither unverändert ist).
   Item Undone, Batch Undone, `FilesChangedMessage` (Added) — wie jedes Undo nach ADR-0012.
10. **Hinweis beim Scannen eines Eingangs**: liegt eine neu gehashte Datei im Eingang byte-gleich schon im Bibliotheksordner,
    meldet der Index `InboxDuplicatesFoundMessage`; die Statusleiste zeigt „N Dateien im Eingang sind schon in der
    Bibliothek — Anzeigen“ (öffnet die Seite exakt + Eingang-Filter, ✕ blendet aus) — nur, wenn die Zahl gestiegen ist.
    Für die Handy-Übertragung (Welle 7): `IDuplicateService.FindByContentAsync(Bereich, Größe, SHA-256)` — rechnet gleich
    große Dateien ohne Prüfsumme sofort.
11. **Galerie-Filter „Doppelte“**: Chip in der Zeile **„Inhalt“** (gleicher Inhalt; in „Datei“ bräche die Zeile bei
    1280 DIP um, ADR-0041): nur Dateien mit byte-gleichem Gegenstück im Bereich (EXISTS über den Prüfsummen-Index), UND
    mit allen anderen Filtern, in „Es gilt“ mit eigenem ✕. Ähnliche Bilder bleiben der Seite vorbehalten.
12. **Bereich**: Gruppen, Zähler, Hinweis und Filter nur in den Bibliotheken des Bereichs (`ILibraryScope`); in „Alle
    verknüpften“ über die ganze Gruppe (Bibliothek je Datei sichtbar). Eine versteckte Bibliothek hat ihren eigenen Katalog —
    ihre Fingerabdrücke und Rückgängig-Daten liegen nur dort.

## Alternativen

- **xxHash128 / BLAKE3** — schneller in der CPU, aber als NuGet-Paket (nicht freigegeben) und hier ohne Nutzen: der
  Durchsatz ist plattengebunden.
- **Teil-Prüfsumme (erste 64 KB) als zweiter Vorfilter** — bei Fotos mit gleicher Größe fast immer gleich, spart wenig;
  für große Videos gleicher Größe (selten) möglich, vertagt.
- **dHash / aHash** — billiger, aber empfindlicher für Helligkeit und Kompression; pHash trennt fremde Fotos besser.
- **Ketten-Gruppen (Union-Find)** — fasst lange Serien zu einer Gruppe zusammen, verbindet aber über Zwischenbilder auch
  Unähnliches; Stern-Gruppen sind vorhersagbar.
- **Löschen ohne Abzug, Rückgängig nur der Datei** — die Datei käme mit neuer FileId und ohne Tags, Personen, Bewertung
  zurück; der generische Abzug aus dem Schema bleibt ohne Pflege richtig, wenn Tabellen dazukommen.
- **Papierkorb allgemein in der Galerie rückgängig machbar** — sinnvoll, aber außerhalb dieses Auftrags; die neue
  Methode `RestoreRecycledAsync` macht es später mit wenig Aufwand möglich.

## Konsequenzen

- Katalog + ≈ 70 Bytes je Datei (500 000 ≈ 35 MB); erster Durchgang je nach Anteil gleich großer Dateien (Herbert: 13 %,
  Ø 26 MB inkl. Videos) ≈ 80 min Lesen für 500 000 Dateien, Bild-Fingerabdrücke aus Vorschaubildern ≈ 10 min, aus
  Originalen (ohne Vorschaubild) ≈ 6 h — jeweils im Hintergrund, pausiert bei Scans.
- „Ähnlich“ über 500 000 Fotos braucht beim Öffnen ≈ 7 s (Spike 15); exakt gleich ≈ 1,3 s; der Galerie-Filter „nur Doppelte“
  ≈ 1,1 s (nicht korreliert: die Prüfsummen mit Gegenstück werden einmal gesammelt — ein korreliertes `EXISTS` war für den
  Eingang-Zähler unbrauchbar).
- Dateien nur in der Cloud (OneDrive-Platzhalter) werden nicht gehasht — ihre Doppelten fehlen, bis sie lokal liegen.
- Nach „Neu aufbauen“ werden Fingerabdrücke neu berechnet (keine Übernahme), Rückgängig-Daten älterer Aufräumaktionen
  bleiben gültig (Ordner werden über den Pfad gesucht).

## Offene Punkte

- Fingerabdrücke über „Neu aufbauen“ hinweg mitnehmen (`UserDataCarryOver`).
- Rückgängig ist nur für die jüngste Aufräumaktion auf der Seite erreichbar; die Galerie-Taste „Rückgängig“ kennt sie nicht.
- Manuell gesetztes Aufnahmedatum und manueller Ort der gelöschten Datei werden nicht auf die behaltene übertragen.
- Schwelle und Vorschlag an Herberts echtem Katalog nachjustieren (Serienaufnahmen bei r = 10 ≈ 17 % aufeinanderfolgender Fotos).

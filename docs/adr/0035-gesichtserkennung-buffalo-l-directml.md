# ADR-0035: Gesichtserkennung mit InsightFace buffalo_l, Modell per Benutzer-Download, DirectML

- **Status:** Accepted (Teil 1 umgesetzt in 0.2.34; Personen-Seite und Lernen folgen in Welle 2); teilweise ersetzt durch [ADR-0048](0048-bibliotheks-trennung-und-verknuepfen.md) — Personen sind nicht mehr global, sondern gehören einer Bibliotheks-Gruppe; Lernen und Vorschläge nur im Bibliotheks-Bereich (0.2.51)
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter (Modell, Lernen wie digiKam, Grafikkarte), Claude (Umsetzung, Detailentscheidungen ohne Rückfrage, siehe unten)
- **Bezogen auf:** ADR-0015 (Persons/Faces bewusst nicht im V0.1-Schema — hiermit umgesetzt), ADR-0004 (EF Core + Raw-SQL),
  ADR-0023/0028 (Benutzerdaten über „Neu aufbauen“), ADR-0030 (Schreiber warten auf den Katalog), PLAN Phase 9

## Kontext

Phase 9 (PLAN Kap. 7): Offline-Gesichtserkennung, später Personen benennen und aus bestätigten Gesichtern lernen (wie
digiKam). Herberts PC: i7-8700K, 32 GB, GTX 1080 Ti. Bibliotheken bis 500 000 Fotos. ADR-0015 hatte das Schema bewusst
offen gelassen (Embedding-Dimension, Personen global oder je Bibliothek, Clustering-Tabellen). CLAUDE.md verbietet WPF in
Services und „externe Sample-Assets im Produktivpfad“; die InsightFace-Modelle sind nur für nicht-kommerzielle Nutzung
freigegeben und dürfen weder ins Repo noch ins portable Paket.

## Entscheidung

1. **Modelle: InsightFace buffalo_l** — SCRFD `det_10g.onnx` (Erkennung, 5 Landmarken) und ArcFace `w600k_r50.onnx`
   (512-dimensionaler Fingerabdruck). Die übrigen drei Modelle des Pakets (1k3d68, 2d106det, genderage) werden nicht
   entpackt. Ein-/Ausgaben selbst geprüft: det_10g `input.1` [1, 3, ?, ?], neun Ausgaben [12800|3200|800, 1|4|10]
   (Score/Box/Landmarken je Stride 8/16/32, 2 Anker); w600k_r50 `input.1` [N, 3, 112, 112] → [N, 512] (Stapel geht).
2. **Pipeline wie InsightFace**, Kennung `buffalo_l/det_10g+w600k_r50/v1` (`Faces.ModelVersion`, `FaceScans.ModelVersion`):
   Bild über WIC mit längster Kante 1280 und EXIF-Orientierung → Letterbox 640 × 640 oben links, bilinear, RGB,
   (x − 127.5) / 128 → SCRFD, Anker-Dekodierung, Score ≥ 0.5, NMS 0.4 → je Gesicht Ähnlichkeitstransformation der 5
   Landmarken auf `arcface_dst` (kleinste Quadrate = Umeyama), 112er-Ausschnitt aus dem 1280er-Bild →
   (x − 127.5) / 127.5 → ArcFace → L2-normiert. Höchstens 64 Gesichter je Bild. Ändert sich etwas davon, wird die
   Kennung hochgezählt und alles neu indiziert (bestätigte Zuordnungen bleiben, Punkt 6).
3. **ONNX Runtime mit DirectML** (`Microsoft.ML.OnnxRuntime.DirectML` 1.24.4, nur im Services-Projekt): Einstellung
   „Grafikkarte verwenden“ (Standard an) versucht DirectML (Adapter 0, Probelauf beim Laden) und fällt bei jedem Fehler
   auf die CPU zurück (höchstens die Hälfte der Kerne). Sitzungen werden einmal geladen und wiederverwendet; je Sitzung
   ein `Run` zur Zeit (DirectML erlaubt keine parallelen Aufrufe derselben Sitzung). ArcFace rechnet auf der CPU in Stapeln, mit DirectML einzeln (Stapel scheitert dort an einer BatchNormalization, Spike 09).
4. **Bild laden hinter einem Interface**: `IFaceImageLoader` in Services (RGB-Puffer + Maße), Implementierung
   `WicFaceImageLoader` im Wpf-Projekt (zwei Durchgänge wie der Thumbnail-Renderer, skaliertes Dekodieren). Videos werden
   vorerst übersprungen (nur `MediaType = Image`).
5. **Schema (Migration `AddFaces`)**: `Persons` (global wie Tags; `CoverFaceId` bewusst OHNE Fremdschlüssel, sonst Zyklus
   Persons ↔ Faces — Leser tolerieren ein fehlendes Gesicht), `Faces` (Box und Landmarken relativ 0..1 zum orientierten
   Bild, Fingerabdruck 2048 Bytes, Score/Größe/Schärfe/Qualität, `PersonId`/`SuggestedPersonId` → Persons ON DELETE SET
   NULL, `State` 0–3, Files ON DELETE CASCADE, Indizes FileId/PersonId/State/SuggestedPersonId), `FaceRejections`
   (FaceId + PersonId, beide CASCADE), `FaceScans` (FileId PK → Files CASCADE, ModelVersion, Zeitpunkt, Anzahl,
   ErrorCode). Schnittstelle zur Lern-/Gruppierlogik: `FaceEmbeddingCodec` (float[512] ↔ 2048 Bytes little-endian).
6. **Hintergrund-Index** (`IFaceIndexService`): Standard AUS. Ein Worker, nie zwei Durchgänge gleichzeitig; arbeitet die
   aktive Bibliothek ab (Fotos ohne FaceScans-Eintrag mit aktueller Kennung, neueste zuerst), drei Bilder vorab geladen,
   Analyse mit niedriger Thread-Priorität, Schreiben in Stapeln (16 Fotos oder 3 s) in EINER Transaktion mit
   `SqliteBusyRetry` (ADR-0030, eigener stiller Messenger — kein „Änderung wartet“ für Hintergrundarbeit). Startet beim
   App-Start (10 s verzögert), nach jedem Scan und bei neuen Dateien; Pause/Fortsetzen und Abbrechen in der Statusleiste.
   Bei erneuter Analyse einer Datei übernimmt jede neue Erkennung das vorhandene Gesicht mit der größten Überlappung
   (IoU ≥ 0.5): Id, Person und Zustand bleiben (Ablehnungen, Titelbild), ein reiner Vorschlag fällt auf „unbestätigt“
   zurück, nicht wiedergefundene unbestätigte Gesichter werden gelöscht — bestätigte und ignorierte bleiben mit ihrer
   alten ModelVersion stehen (keine Benutzerentscheidung geht still verloren; `Persons.CoverFaceId` auf ein gelöschtes
   Gesicht wird gelöst). Fehlende Dateien (offline) werden nicht vermerkt, nicht dekodierbare mit `decode_failed`,
   Analysefehler mit `analyze_failed` (sonst stünde dieselbe Datei bei jedem Durchgang vorn); drei Analysefehler
   hintereinander beenden den Durchgang nach dem Schreiben mit Fehlermeldung (Modell/Grafikkarte). Ändert der
   Incremental-Scan Größe oder Änderungszeit einer Datei (an Ort und Stelle bearbeitet), löscht er ihren FaceScans-Eintrag
   (`IncrementalCatalogWriter`) — sie wird neu analysiert und abgeglichen.
7. **„Neu aufbauen“ nimmt die Gesichter mit** (kompakter Block `UserDataCarryOver.Faces.cs`): statt 2 KB je Gesicht in
   TEMP-Tabellen zu kopieren (500 000 Fotos ≈ mehrere 100 MB im Arbeitsspeicher, `temp_store=MEMORY`), werden Faces und
   FaceScans in der Neuaufbau-Transaktion **geparkt** — FileId negativ, `PRAGMA defer_foreign_keys = ON` (gilt nur bis
   zum Commit) —, die Kaskade beim Löschen der alten Files-Zeilen erreicht sie nicht; nach dem Bulk-Insert bekommen sie
   über dieselbe Zuordnung Ordnerpfad + Name wie Bewertungen und Tags die neuen FileIds, der Rest wird gelöscht. Face-Ids
   bleiben, also auch FaceRejections und `CoverFaceId`; kein Foto muss neu analysiert werden. Bleibt eine negative FileId
   übrig, scheitert der Commit an den Fremdschlüsseln und der alte Katalog bleibt unverändert (ADR-0028).
8. **Modell per Benutzer-Download** (`IFaceModelProvider`): Ordner `<AppData>\models\buffalo_l\` (bleibt beim Umzug der
   Katalog-DB in der App-Wurzel); `LUMORA_FACE_MODELS` überschreibt ihn für Entwicklung und Tests. Fehlt das
   Modell, zeigen die Einstellungen „Modell herunterladen… (275 MB, InsightFace buffalo_l, nur nicht-kommerzielle
   Nutzung)“. Erst nach Bestätigung im Dialog (AppDialogWindow über `IDialogService`, mit Quelle, Zielordner und
   Lizenzhinweis) lädt die App das Archiv von GitHub (Release v0.7), prüft SHA-256
   `80ffe37d…b0ca2f`, entpackt nur die zwei Modelle (.tmp + Umbenennen) und löscht das Archiv; Fortschritt in der Karte,
   abbrechbar, bei Abbruch/Fehler bleibt nichts Halbes liegen.

## Abgrenzung zur CLAUDE.md-Regel „Keine externen Sample-Assets im Produktivpfad“

Die Regel zielt auf Testdaten/Beispielbilder, die der Produktivcode heimlich nachlädt. Die Modelle sind ein
**bewusst per Benutzeraktion geladenes Laufzeit-Asset**: kein Download ohne Klick und Bestätigung, feste Quelle und
Prüfsumme, Lizenzhinweis im Dialog, nichts davon im Repo oder im portablen Paket. Die CLAUDE.md-Zeile selbst bleibt
unverändert; eine Präzisierung („…außer Laufzeit-Modelle, die der Benutzer nach Bestätigung herunterlädt, ADR-0035“)
ist als Vorschlag an Herbert offen.

## Lizenz

InsightFace-Code steht unter MIT, die vortrainierten Modelle (buffalo_l) sind **nur für nicht-kommerzielle
Forschungszwecke** freigegeben. Lumora ist ein privates Projekt; die Modelle werden nicht weitergegeben, sondern
vom Benutzer selbst bezogen. Für eine kommerzielle Nutzung wären andere Modelle nötig (neue Kennung, Neuindizierung).

## Konsequenzen

### Positiv
- Läuft vollständig lokal; Fotos und Gesichtsdaten verlassen den Rechner nicht.
- Grafikkarte macht den Index etwa fünfmal schneller (Spike 09: 46 statt 231 ms/Foto, 500 000 Fotos ≈ 6,4 h statt 32 h); ohne geeignete Grafikkarte läuft er auf der CPU.
- Fingerabdrücke, Qualität und bestätigte Zuordnungen überstehen „Neu aufbauen“ und Modellwechsel.
- Welle 2 (Personen-Seite, Lernen) baut auf festem Schema und `FaceEmbeddingCodec` auf.

### Negativ
- Paket wird größer (ONNX Runtime + DirectML ≈ 40 MB native DLLs).
- DB wächst um ≈ 2,3 KB je Gesicht (gemessen 1,12 Gesichter je Foto → 500 000 Fotos ≈ 1,3 GB).
- Laden (JPEG-Dekodierung) dominiert die Zeit je Foto; bei 500 000 Fotos dauert der Erstindex Stunden (Spike 09).
- Bestätigte Gesichter, die ein neues Modell nicht wiederfindet, behalten einen Fingerabdruck der alten Kennung —
  Welle 2 muss `ModelVersion` beim Vergleichen beachten.

## Alternativen

- **RetinaFace + ArcFace getrennt / FaceONNX** — verworfen: buffalo_l ist das Standardpaket von InsightFace, SCRFD ist
  schneller als RetinaFace bei gleicher Qualität, keine zusätzliche Bibliothek.
- **Modell im Paket ausliefern** — verworfen: Lizenz (nicht-kommerziell), Größe.
- **Faces in TEMP-Tabellen über „Neu aufbauen“ tragen** — verworfen: Arbeitsspeicher (siehe Punkt 7).
- **Nur bestätigte Gesichter über „Neu aufbauen“ tragen, Rest neu indizieren** — verworfen: Stunden Rechenzeit nach
  jedem Neuaufbau großer Bibliotheken.
- **CUDA statt DirectML** — verworfen: braucht CUDA/cuDNN-Installation und NVIDIA; DirectML läuft auf jeder
  DirectX-12-Grafikkarte.

## Referenzen

- `docs/spikes/09-gesichtserkennung.md` — Messung an 1 000 Fotos (CPU vs. DirectML, Qualität, Kosinus-Ähnlichkeiten)
- InsightFace `python-package/insightface/model_zoo/scrfd.py`, `arcface_onnx.py`, `utils/face_align.py`

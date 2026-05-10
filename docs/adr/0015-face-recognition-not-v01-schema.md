# ADR-0015: Face Recognition NICHT im V0.1-Schema

- **Status:** Accepted
- **Datum:** 2026-05-10
- **Entscheider:** Herbert Schrotter (Wunsch für Feature), Claude (ursprünglich für Schema-Vorbereitung), GPT-5 (Widerspruch, hat überzeugt)

## Kontext

User-Wunsch: offline Gesichtserkennung (Phase 9) via ONNX-Modellen (RetinaFace + ArcFace), Auto-Clustering ähnlicher Gesichter, Personen benennen, Suche "Foto mit Person X". Claude schlug zunächst vor, `Persons` + `Faces`-Tabellen schon im V0.1-Schema anzulegen (leer), um spätere Migration zu vermeiden. **GPT-5 widerspricht** dem überzeugend.

## Entscheidung

**Persons + Faces-Tabellen werden NICHT im V0.1-Schema angelegt.**

Face Recognition kommt als **eigenes Phase-9-Epic** mit:
- **Eigener Migration** (EF Core Migration ab dem Punkt)
- ONNX-Runtime + InsightFace-Modelle (RetinaFace Detection + ArcFace Embedding)
- Schema-Design final erst kurz vor Implementation

## Begründung (von GPT-5)

### Warum keine Vorab-Tabellen
1. **Modell ist noch nicht stabil**:
   - 128-, 512- oder 1024-dimensionale Embeddings? offen
   - Face-Crops cachen? offen
   - Clustering-Jobs eigene Tabellen? offen
   - Personen pro Library oder global? offen
2. **Leere Tabellen sind trotzdem Vertrag**: Sobald DB mit `Faces` ausgeliefert wird, entsteht implizit ein Schema-Versprechen. Spätere Änderungen brauchen trotzdem Migration.
3. **V0.1 hat wichtigere Risiken**: Scan, Virtualisierung, Thumbnailing, SQLite-Writes, File-Operations.
4. **Wenn User später Tags/Plans hat, brauchen wir ohnehin echte Migrationen** — dann ist es besser, Migrationen sauber einzuführen, statt heute spekulative Tabellen anzulegen.

### Warum Claudes ursprüngliches Argument schwächer ist
- Claude argumentierte: "spätere Migration vermeiden". Aber Migration ist eh nötig sobald reale Daten in DB sind (Tags, Plans, Favoriten).
- Schema-Vertrag mit leeren Tabellen ist nicht "kostenlos" — verhindert späteres Modell-Refactoring.

## Konsequenzen

### Positiv
- V0.1-Schema bleibt fokussiert (7 Tabellen statt 9)
- Modell-Design für Faces kommt mit Implementation-Erfahrung
- Keine spekulativen Schema-Festlegungen für ungeklärte Domäne
- Phase-9-Implementation kann frei entscheiden (Embedding-Dim, Crops, Clustering-Strategie)

### Negativ
- Eine echte Migration ist nötig sobald Phase 9 implementiert wird (akzeptiert ab V0.2 sowieso strict Migrations)
- Wenn V0.1 bereits in produktiver Nutzung ist, kann nicht mehr per `EnsureCreated` resetted werden — Migration ist Pflicht

### Neutral
- ML.NET / ONNX-Runtime Bibliotheken sind nicht in V0.1-NuGet-Liste
- DB-Größe-Schätzung: pro 100k Faces ~200 MB Embeddings — Phase-9-Reality-Check

## Phase-9-Roadmap (grob)

1. Schema-Design final (Persons, Faces mit endgültiger Embedding-Dim)
2. ONNX-Modelle ausliefern (Installer-Variante oder Settings-Download)
3. Background-Indexer für Face-Detection
4. Embedding-Berechnung (ArcFace)
5. Auto-Clustering via Cosine-Distance + Greedy/Threshold (HDBSCAN später)
6. UI: Personen-Liste, Cluster-Bestätigung, Suche "Fotos mit Person X"
7. Privacy-UX: "Daten bleiben lokal" deutlich kommunizieren, Button "Gesichtsdaten löschen"

## Alternativen

- **Tabellen leer anlegen, "verhindert Migration"** → verworfen weil: Modell unstabil, leere Tabellen sind trotzdem Vertrag, Migration eh nötig ab V0.2.
- **Face Recognition in V0.1** → verworfen weil: ONNX-Runtime + Modell-Distribution + UI + Privacy-UX zu viel für V0.1, lenkt von Foundation ab.
- **Cloud-API (Azure Face / AWS Rekognition)** → verworfen weil: User will offline, Privacy-Grund.
- **FaceONNX als Wrapper** → später prüfen, aber für Phase 9 wahrscheinlich direkter `Microsoft.ML.OnnxRuntime` für volle Kontrolle.

## Referenzen

- [chatgpt-review/runde-2-chatgpt-response.md](../../chatgpt-review/runde-2-chatgpt-response.md) "Warum Persons/Faces in V0.1 ablehnen"
- [chatgpt-review/runde-2-claude-analysis.md](../../chatgpt-review/runde-2-claude-analysis.md) — Claude akzeptiert Widerspruch

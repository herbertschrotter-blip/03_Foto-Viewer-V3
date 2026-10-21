# Spike #10 — Gesichter lernen und gruppieren (synthetische ArcFace-Daten)

- **Status:** 🟢 Grün auf synthetischen Daten — Nachjustieren an echten Fingerabdrücken in Welle 2 offen
- **Datum:** 2026-10-21 (v0.2.35)
- **Bezogen auf:** [ADR-0036](../adr/0036-gesichter-lernen-und-gruppieren.md), `src/Lumora.Services/Faces/Recognition/`,
  Harness `tests/Lumora.Tests/Faces/Recognition/FaceLearningEvaluationTests.cs`

## Fragen

1. Bleiben Gruppen-Vorschläge rein, auch bei Doppelgängern, Geschwistern und Alterung? Welche Parameter?
2. Wie gut ordnet das Ensemble mit 1, 3, 10 bestätigten Beispielen je Person zu, wie oft „unbekannt“, wie oft werden
   Unbekannte fälschlich benannt? Was bringt eine Ablehnung?
3. Schadet ein einzelnes falsches Beispiel?
4. Tempo: Training (= Neu-Lernen nach Korrektur) bei 10 000 Beispielen / 200 Personen < 2 s? 100 000 Beispiele?

## Messaufbau

- **Keine echten Fingerabdrücke** (Teil 1 entsteht parallel) und **keine Fotos** — nur synthetische Vektoren
  (`SyntheticFaces.cs`, feste Saat) auf der Einheitskugel in 512 Dimensionen, die die ArcFace-Statistik nachbilden:
  jedes Gesicht hat Kosinus 0,72–0,90 zum Zentrum seiner Person; Personen teilen sich zu 30 % eine von 6
  Gruppen-Richtungen; Doppelgänger haben Zentren-Kosinus ρ = 0,62 (Geschwister-Welt 0,75/0,85); 20 % der Personen
  altern (Zentrum dreht sich über die Zeit um bis zu 50°); 5 % der Gesichter haben schlechte Qualität (Kosinus 0,3–0,55,
  Qualität 0,05–0,35).
- Harness-Größe über `LUMORA_FACELEARN_SCALE` (`small` = Standard im normalen Testlauf mit lockeren Untergrenzen,
  `full` = diese Tabellen), `LUMORA_FACELEARN_OUT` = Datei für die Tabellen. Messlauf: `dotnet test -c Release`
  (Debug-Builds rechnen ohne JIT-Optimierung 4–7× langsamer).
- Rechner: Herberts PC (i7-8700K, 6 Kerne / 12 Threads, 32 GB). Zeiten = Median aus 3 Läufen nach Aufwärmen.

### Statistik der synthetischen Daten

| Paare | p5 | Median | p95 |
|---|---:|---:|---:|
| gleiche Person | 0,56 | 0,65 | 0,76 |
| verschiedene Personen | -0,07 | 0,01 | 0,22 |
| Doppelgänger-Paar (ρ 0,62) | 0,36 | 0,42 | 0,49 |
| Alterung Anfang ↔ Ende | 0,35 | 0,45 | 0,55 |

## Ergebnis 1 — Gruppierung (300 Personen, ~5 950 Gesichter, 4–80 Gesichter je Person)

„Reinheit“ = Anteil der gruppierten Gesichter, die zur Mehrheitsperson ihrer Gruppe gehören. „Vollständigkeit“ = je
Person der Anteil ihrer guten Gesichter in ihrer größten Gruppe. „markiert“ = Anteil der Mitglieder mit `IsOutlier`;
„fremde markiert“ = Anteil der fremden Mitglieder, die markiert sind.

**Welt „normal“** (10 % Doppelgänger ρ 0,62):

| Variante | Gruppen | gemischte Gruppen | Reinheit | Vollständigkeit | gruppiert | markiert | fremde markiert | Zeit |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **Standard (Merge 0,55, min 3)** | 302 | **0** | 100,0 % | 97,8 % | 98,5 % | 0,5 % | – | 1.061 ms |
| Merge 0,50 | 299 | 0 | 100,0 % | 99,9 % | 99,9 % | 0,6 % | – | 980 ms |
| Merge 0,45 | 299 | 0 | 100,0 % | 100,0 % | 100,0 % | 0,6 % | – | 760 ms |
| Merge 0,60 | 302 | 0 | 100,0 % | 87,1 % | 88,9 % | 0,6 % | – | 782 ms |
| Mindestgröße 2 | 322 | 0 | 100,0 % | 98,0 % | 99,2 % | 0,5 % | – | 1.054 ms |
| ohne Qualitätsfilter | 302 | 0 | 100,0 % | 97,8 % | 98,5 % | 0,5 % | – | 1.073 ms |
| Single Linkage ≈ DBSCAN (Kante 0,50) | 281 | 17 | 97,0 % | 100,0 % | 99,9 % | 1,2 % | 24,3 % | 956 ms |

**Welt „schwierig“** (30 % Doppelgänger ρ 0,62): Standard 304 Gruppen, 0 gemischt, Reinheit 100 %, Vollständigkeit
97,9 %; Single Linkage 32 gemischte Gruppen (Reinheit 95,0 %).

**Welt „Geschwister“** (30 % Doppelgänger ρ 0,75, Gesichter des Paars ~0,45–0,55 — überlappt mit „gleiche Person“):

| Variante | Gruppen | gemischte Gruppen | Reinheit | Vollständigkeit | gruppiert | markiert | fremde markiert | Zeit |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| **Standard (Merge 0,55, min 3)** | 304 | **0** | 100,0 % | 97,9 % | 98,5 % | 0,4 % | – | 967 ms |
| Merge 0,50 | 284 | 14 | 98,5 % | 99,8 % | 99,8 % | 0,6 % | 9,4 % | 1.125 ms |
| Merge 0,45 | 261 | 38 | 95,9 % | 99,9 % | 99,9 % | 0,8 % | 11,6 % | 1.719 ms |
| Merge 0,60 | 304 | 0 | 100,0 % | 86,8 % | 88,8 % | 0,5 % | – | 1.068 ms |
| Single Linkage ≈ DBSCAN (Kante 0,50) | 221 | 53 | 88,3 % | 99,2 % | 99,2 % | 2,9 % | 19,2 % | 1.188 ms |

**Wahl:** `MergeSimilarity` **0,55** — der kleinste Wert ohne gemischte Gruppe in allen drei Welten; 0,50 vermischt
Geschwister, 0,60 kostet 11 Punkte Vollständigkeit. Werden ganze Geschwister verschmolzen, hilft die Ausreißer-Markierung
kaum (nur 9–12 % der Fremden markiert) — sie ist für einzelne Streuer gedacht; gegen Vermischung schützt die Schwelle.
`MinGroupSize` **3** (Zweiergruppen sind ebenfalls rein, aber schwache Evidenz — Option). Der Qualitätsfilter ändert auf
diesen Daten nichts (schlechte Gesichter fallen ohnehin heraus), bleibt aber als Schutz, weil echte unscharfe Gesichter
nicht zufällig, sondern in Richtung „Durchschnittsgesicht“ verrauscht sind und dann Brücken bilden.

## Ergebnis 2 — Zuordnung (200 bekannte, 50 unbekannte Personen, je 10 Anfragen)

| Beispiele je Person | Verfahren | richtig | falsch | „unbekannt“ | Unbekannte fälschlich zugeordnet |
|---:|---|---:|---:|---:|---:|
| 1 | Ensemble (k-NN + linear) | 87,7 % | 0,1 % | 12,3 % | 1,2 % |
| 1 | nur k-NN | 87,9 % | 0,1 % | 12,0 % | 1,6 % |
| 3 | Ensemble | 93,9 % | 0,0 % | 6,1 % | 0,0 % |
| 3 | nur k-NN | 93,9 % | 0,0 % | 6,1 % | 0,0 % |
| 10 | Ensemble | 95,0 % | 0,0 % | 5,0 % | 2,8 % |
| 10 | nur k-NN | 94,5 % | 0,0 % | 5,5 % | 2,8 % |

Das „unbekannt“ bei bekannten Personen stammt aus schlechter Qualität (5 % der Beispiele werden nicht gelernt, 5 % der
Anfragen sind verrauscht) und Alterung (Beispiel und Anfrage aus verschiedenen Lebensphasen). Die Fehlzuordnungen
Unbekannter sind Doppelgänger bekannter Personen; nach der Ablehnung kam für sie zu 100 % „unbekannt“ (richtig).

**Geschwister-Welt** (jede zweite Person ist Doppelgänger der vorigen, beide bekannt, keine schlechte Qualität):

| ρ | Beispiele | richtig | falsch | „unbekannt“ | nach Ablehnung richtig |
|---:|---:|---:|---:|---:|---:|
| 0,75 | 1 | 99,9 % | 0,2 % | 0,0 % | 100 % |
| 0,85 | 1 | 95,0 % | 5,1 % | 0,0 % | **100 %** |
| 0,85 | 3 | 100,0 % | 0,0 % | 0,0 % | – |
| 0,85 | 10 | 100,0 % | 0,0 % | 0,0 % | – |

Bei fast-Zwillingen mit nur einem Beispiel verwechselt das Modell 5 % — nach einer Ablehnung kommt in allen Fällen der
richtige Zwilling als nächstbester Kandidat. Ab 3 Beispielen keine Verwechslung mehr.

### Schwelle „unbekannt“ (20 % Doppelgänger)

| Beispiele | Schwelle | richtig | „unbekannt“ | Unbekannte fälschlich zugeordnet |
|---:|---:|---:|---:|---:|
| 3 | 0,40 | 94,3 % | 5,8 % | 11,6 % |
| 3 | 0,42 | 94,3 % | 5,8 % | 6,8 % |
| 3 | **0,45** | 94,2 % | 5,8 % | 2,8 % |
| 3 | 0,50 | 93,4 % | 6,6 % | 0,0 % |
| 10 | 0,40 | 96,2 % | 3,9 % | 6,4 % |
| 10 | 0,42 | 96,2 % | 3,9 % | 4,6 % |
| 10 | **0,45** | 96,0 % | 4,1 % | 2,4 % |
| 10 | 0,50 | 95,2 % | 4,9 % | 0,0 % |

**Wahl:** `UnknownSimilarity` **0,45** — senkt die Fehlbenennung Unbekannter gegenüber 0,42 auf weniger als die Hälfte bei
praktisch gleicher Trefferquote. 0,50 wäre auf diesen Daten noch besser; echte Gesichter streuen innerhalb einer Person
aber stärker (Profil, Unschärfe), dort kostet eine höhere Schwelle mehr Treffer — deshalb erst an echten Daten entscheiden.

## Ergebnis 3 — ein falsches Beispiel

Person A mit 20 richtigen Beispielen + 1 Gesicht von B (hohe Qualität) als A bestätigt; B nicht trainiert; 500 Anfragen
je Person:

| k | B-Anfragen → A | A-Anfragen → A |
|---:|---:|---:|
| 1 (Einzel-Nachbar) | 100,0 % | 100,0 % |
| **3** | **0,0 %** | 100,0 % |
| 5 | 0,0 % | 100,0 % |

(gleich mit und ohne linearen Klassifikator). Ist B bekannt, sind alle Zuordnungen mit und ohne falsches Beispiel
identisch (Unit-Test). Die Ausreißer-Prüfung meldet das falsche Beispiel beim Bestätigen (`FarFromExamples`).

## Ergebnis 4 — Tempo (Release, i7-8700K)

| Fall | Training (= Neu-Lernen) | Training nur k-NN | Vorschlag je Gesicht (1 Thread) | Vorschläge parallel je 1 000 | Ausreißer-Prüfung je Gesicht |
|---|---:|---:|---:|---:|---:|
| 10.000 Beispiele / 200 Personen | **232 ms** | 35 ms | 0,42 ms | 153 ms | 0,39 ms |
| 100.000 Beispiele / 500 Personen | 829 ms | 298 ms | 1,45 ms | 623 ms | 2,06 ms |

| Gruppierung (25 Gesichter je Person) | Zeit |
|---|---:|
| 5.000 unbekannte Gesichter | 660 ms |
| 10.000 unbekannte Gesichter | 1.980 ms |
| 20.000 unbekannte Gesichter | 6.676 ms |

Ziel „10 000 / 200 < 2 s“ erfüllt (Faktor 8 Reserve). Die Gruppierung wächst quadratisch (Brute-Force-Nachbarn); für
> 50 000 unbekannte Gesichter muss Welle 2 abschnittsweise gruppieren.

## Folgerungen für Welle 2

- Standard-Schwellen (ADR-0036) an Herberts echten Fingerabdrücken nachjustieren: Verteilung „gleiche Person“ /
  „verschiedene Personen“ aus den ersten bestätigten Personen messen, dann `UnknownSimilarity`, `MergeSimilarity`,
  `OutlierMinSimilarity` setzen; dieselbe Harness mit echten (lokal geladenen, nie eingecheckten) Vektoren laufen lassen
  und nur Zahlen dokumentieren.
- Ensemble vs. nur k-NN an echten Daten vergleichen (Option `UseLinearClassifier`).
- Qualitätswert aus Teil 1 auf 0…1 abbilden (`FaceSample.Quality`).
- Nach jeder Bestätigung/Korrektur im Hintergrund neu trainieren (Sekundenbruchteile), Ablehnungen persistieren und
  mitgeben; `ConfirmedPersonId` nur aus menschlicher Bestätigung füllen.

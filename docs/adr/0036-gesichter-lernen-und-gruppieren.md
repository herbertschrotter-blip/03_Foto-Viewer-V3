# ADR-0036: Gesichter lernen und gruppieren — nur aus Bestätigtem, k-NN + linearer Klassifikator, strenge Gruppen

- **Status:** Accepted
- **Datum:** 2026-10-21
- **Entscheider:** Herbert Schrotter
- **Bezogen auf:** Gesichtserkennung Teil 1 (Erkennung, Fingerabdrücke, Schema, Hintergrund-Index — parallel, Welle 5),
  [Spike 10](../spikes/10-gesichter-lernen.md)

## Kontext

Nach Erkennung und Fingerabdruck (ArcFace, 512 float, L2-normiert) braucht V3 zwei Dinge: (1) bekannte Personen auf
neuen Fotos vorschlagen und (2) für den Einstieg unbekannte Gesichter zu Gruppen bündeln, die Herbert auf einmal benennt.
Vorbilder sind digiKam 8.6/8.7 (Zuordnung nur aus bestätigten Gesichtern, k-NN + SVM, Distanzprüfung, abgelehnte
Vorschläge) und PhotoPrism (Gruppierung per DBSCAN auf Kosinus-Distanz). Herberts Sorge: „wenn in der Gruppe was anderes
drin ist, lernt es doch falsch.“ Fotos verlassen nie den Rechner, es gibt kein ML.NET und keine neuen Pakete; echte
Fingerabdrücke liegen bei der Entscheidung noch nicht vor (Teil 1 entsteht parallel).

## Entscheidung

Reine, WPF- und EF-freie Bausteine in `Services/Faces/Recognition` (Verdrahtung, DB, UI in Welle 2):

1. **Lernen nur aus bestätigten Gesichtern.** `IFaceRecognizer.Train(confirmed, rejections, options)` nimmt nur
   `FaceSample` mit `ConfirmedPersonId` (von einem Menschen bestätigt) und Qualität ≥ `MinQuality` (0,4). Eigene Vorschläge
   fließen nie ein. Training ist immer ein vollständiger Neuaufbau (10 000 Beispiele / 200 Personen: 232 ms) — nach einer
   Korrektur lernt die Person nur aus den verbleibenden Beispielen.
2. **Ensemble je Person.** k-NN: Mittel der **k = 3** ähnlichsten Beispiele der Person (nie ein Einzel-Nachbar, sobald es
   3 Beispiele gibt) plus ein **linearer Klassifikator** je Person (One-vs-Rest, logistische Regression per SGD,
   ausgewogene Epochen, bis 1 000 Gegenbeispiele: Hälfte von den Personen mit den ähnlichsten Zentren, Rest zufällig mit
   fester Saat). Score = ½ · k-NN-Anteil (Kosinus 0,2 → 0 … 0,7 → 1) + ½ · Wahrscheinlichkeit des Klassifikators.
3. **Distanzprüfung „unbekannt“.** k-NN-Ähnlichkeit des besten Kandidaten < **0,45** oder Score < 0,45 → `Unknown`; die
   Kandidaten bleiben als `Alternatives` („vielleicht …?“). Anfragen unter der Mindestqualität bekommen Vorschläge mit
   Score × 0,75.
4. **Ablehnungen** (`FaceRejection(FaceId, PersonId)`) werden im Modell je Gesicht gemerkt; die Person wird für dieses
   Gesicht übersprungen, der nächstbeste Kandidat rückt nach. Zusätzlich `Suggest(face, excludedPersons)` für die sofortige
   Reaktion ohne Neu-Training.
5. **Ausreißer-Prüfung beim Bestätigen** (`CheckOutlier`): „Rückfrage empfohlen“, wenn eine andere Person um ≥ 0,08
   ähnlicher ist, die k-NN-Ähnlichkeit zur Person < 0,4 liegt oder (ab 5 Beispielen) um ≥ 3,5 robuste Abweichungen
   (Median/MAD der Leave-one-out-Werte der Person) unter dem Üblichen. Das Gesicht selbst wird dabei ausgeklammert.
6. **Strenge Gruppierung** (`IFaceClusterer.Cluster`): Qualitätsfilter → k-NN-Graph (20 Nachbarn ab Kosinus 0,45) →
   **agglomerativ mit Average Linkage**: verschmolzen wird nur, wenn die mittlere Ähnlichkeit ALLER Paare zwischen zwei
   Gruppen ≥ **0,55** ist (exakt über Summenvektoren: (S_A·S_B)/(|A|·|B|)) → Mitglieder mit Zentrums-Ähnlichkeit < 0,5
   fliegen raus → Gruppen unter **3** Gesichtern sind Rauschen. Mitglieder nach Nähe zum Zentrum sortiert, zweifelhafte
   (robuste Abweichung ≥ 2 oder Zentrums-Ähnlichkeit < 0,6) mit `IsOutlier` am Ende. Rauschen und übersprungene
   (schlechte Qualität, ungültig) getrennt im Ergebnis.
7. **Rein und deterministisch**: Reihenfolge nach FaceId (bzw. Person, FaceId), feste Saat, feste Gleichstands-Regeln;
   Modell unveränderlich und thread-sicher lesbar (`SuggestMany` parallel); SIMD über `System.Numerics.Vector<T>`.

Alle Schwellen sind an synthetischen ArcFace-ähnlichen Daten gewählt (Spike 10) und werden in Welle 2 an Herberts echten
Fingerabdrücken nachjustiert.

## Begründung

- **Herberts Schutzmaßnahmen** sind direkt umgesetzt: streng gruppieren (Average Linkage 0,55 statt DBSCAN/Single
  Linkage: in der Geschwister-Welt 0 statt 53 gemischte Gruppen, Vollständigkeit 97,9 %), Ausreißer sortiert und markiert,
  nur Bestätigtes lernt, Rückfrage beim Bestätigen, schnelles Neu-Lernen, mehrere Nachbarn entscheiden (ein falsches
  Beispiel unter 20: k = 3 → 0 % Fehlzuordnungen, k = 1 → 100 %).
- **Average Linkage statt DBSCAN**: DBSCAN verbindet zwei Personen schon über eine Kette einzelner ähnlicher Paare
  (Doppelgänger, Brücken). Average Linkage verlangt, dass die Gruppen *im Mittel* zusammenpassen; mit Summenvektoren ist das
  exakt und billig. Der k-NN-Graph begrenzt Speicher und Rechenzeit.
- **k = 3** ist der kleinste Wert, bei dem ein einzelnes falsches Beispiel allein den Mittelwert nicht über die Schwelle
  hebt; größere k verwässern Personen mit Alterungsverlauf (nur die nächsten Lebensphasen sind ähnlich).
- **Schwelle 0,45**: Schwellen-Studie (200 bekannte / 50 unbekannte Personen, 20 % Doppelgänger): 0,42 → 4,6–6,8 %
  Unbekannte fälschlich zugeordnet, 0,45 → 2,4–2,8 %, 0,50 → 0 % bei ~1 Punkt weniger Treffern. 0,45 ist der Kompromiss;
  echte Daten streuen innerhalb einer Person stärker.
- **Linearer Klassifikator** wie bei digiKam: auf synthetischen Daten nur kleiner Gewinn (+0,5 Punkte bei 10 Beispielen,
  weniger Fehlzuordnungen Unbekannter bei 1 Beispiel), kostet ~200 ms Training bei 10 000 Beispielen. Echte Fingerabdrücke
  haben strukturierte Störrichtungen (Licht, Pose, Brille), die ein diskriminativer Klassifikator ausgleichen kann; per
  Option abschaltbar, Welle 2 vergleicht an echten Daten.
- **Eigenimplementierung** statt ML.NET: wenige hundert Zeilen, keine Abhängigkeit, deterministisch, schnell genug.

## Konsequenzen

### Positiv
- Lernen, Vorschlagen und Gruppieren sind ohne DB/UI vollständig testbar (40 Tests inkl. Bewertungs-Harness).
- Neu-Lernen ist so schnell, dass Welle 2 nach jeder Bestätigung/Korrektur im Hintergrund neu trainieren kann
  (100 000 Beispiele / 500 Personen: 0,8 s).
- Gruppen sind auf allen synthetischen Welten rein; zweifelhafte Mitglieder sind markiert.

### Negativ
- Gruppierung ist O(n²·d) über Brute-Force-Nachbarn: 10 000 Gesichter ~2 s, 20 000 ~6,7 s. Für sehr große Bestände
  (> 50 000 unbekannte Gesichter) muss Welle 2 in Abschnitten gruppieren (z. B. je Bibliothek/Zeitraum) oder vorher per
  Vorschlag bekannte Personen abziehen.
- Strenge kostet Vollständigkeit: Personen mit starker Alterung oder wenigen Gesichtern zerfallen eher in mehrere Gruppen
  bzw. bleiben Rauschen (Vollständigkeit 97,8 % statt 99,9 % bei 0,50).
- Schwellen beruhen auf synthetischen Daten — die echte Kalibrierung steht aus.

### Neutral
- `FaceSample.Quality` erwartet 0…1 (1 = best); Welle 2 bildet den Qualitätswert aus Teil 1 darauf ab.
- Ablehnungen wirken nur auf das abgelehnte Gesicht (nicht auf Serienbilder desselben Moments).
- `FaceGroup.Centroid` erlaubt Welle 2, für eine ganze Gruppe per `Suggest` einen Namen vorzuschlagen.

## Alternativen

- **DBSCAN wie PhotoPrism** (bzw. Single Linkage): in der Harness 17–53 gemischte Gruppen → verworfen (Herberts Sorge).
- **Centroid je Person statt k-NN**: einfach, aber ein falsches Beispiel verschiebt das Zentrum, und Alterungsverläufe
  werden schlecht abgebildet → verworfen.
- **1-NN**: folgt einem einzelnen falschen Beispiel (100 % Fehlzuordnung im Test) → verworfen.
- **ML.NET / Accord**: neue schwere Abhängigkeit, nicht deterministisch ohne Zusatzaufwand → verworfen.
- **Selbstlernen aus sicheren Vorschlägen** (auto-confirm ab hohem Score): Selbstverstärkung von Fehlern → verworfen.
- **Mindestgröße 2**: ebenfalls rein (+20 Gruppen), aber Zweiergruppen sind schwache Evidenz; Option bleibt.

## Referenzen

- Code: `src/PhotoViewerV3.Services/Faces/Recognition/` (`FaceRecognizer`, `FaceModel`, `LinearPersonClassifier`,
  `FaceClusterer`, `FaceLearningOptions`, `FaceLearningModels`, `VectorMath`)
- Tests: `tests/PhotoViewerV3.Tests/Faces/Recognition/` (`FaceRecognizerTests`, `FaceClustererTests`,
  `FaceLearningEvaluationTests`, Generator `SyntheticFaces`)
- Messung: [docs/spikes/10-gesichter-lernen.md](../spikes/10-gesichter-lernen.md)

namespace Lumora.Services.Faces.Recognition;

// Standard-Schwellen sind an synthetischen ArcFace-ähnlichen Daten gewählt (docs/spikes/10-gesichter-lernen.md) und
// werden in Welle 2 an Herberts echten Fingerabdrücken nachjustiert. Alle Ähnlichkeiten sind Kosinus-Werte normierter
// Vektoren (−1…1; ArcFace: gleiche Person typisch 0,5–0,8, verschiedene Personen 0,0–0,25).

/// <summary>Parameter der Gruppierung unbekannter Gesichter (streng: lieber mehrere kleine reine Gruppen).</summary>
public sealed record FaceClusterOptions
{
    public static FaceClusterOptions Default { get; } = new();

    /// <summary>Gesichter darunter werden nicht gruppiert (verrauschte Vektoren würden Gruppen verbinden).</summary>
    public float MinQuality { get; init; } = 0.4f;

    /// <summary>Kandidaten-Kanten: je Gesicht höchstens so viele nächste Nachbarn (k-NN-Graph).</summary>
    public int NeighborCount { get; init; } = 20;

    /// <summary>Kandidaten-Kante nur ab dieser Paar-Ähnlichkeit.</summary>
    public float EdgeSimilarity { get; init; } = 0.45f;

    /// <summary>
    /// Zwei Gruppen werden nur verschmolzen, wenn die mittlere Ähnlichkeit ALLER Paare zwischen ihnen mindestens so hoch
    /// ist (Average Linkage). Eine einzelne Brücke zwischen zwei Personen reicht dadurch nicht.
    /// </summary>
    public float MergeSimilarity { get; init; } = 0.55f;

    /// <summary>Kleinere Gruppen werden zu Rauschen.</summary>
    public int MinGroupSize { get; init; } = 3;

    /// <summary>Mitglieder mit geringerer Ähnlichkeit zum Gruppenzentrum fliegen aus der Gruppe (Rauschen).</summary>
    public float MinCentroidSimilarity { get; init; } = 0.5f;

    /// <summary>Ausreißer-Markierung: robuste Abweichung (Median/MAD) der Zentrums-Distanz ab diesem Wert.</summary>
    public float OutlierRobustZ { get; init; } = 2.0f;

    /// <summary>Ausreißer-Markierung zusätzlich, wenn die Ähnlichkeit zum Zentrum darunter liegt.</summary>
    public float OutlierCentroidSimilarity { get; init; } = 0.6f;
}

/// <summary>Parameter des Lernens aus bestätigten Gesichtern und der Vorschläge.</summary>
public sealed record FaceRecognizerOptions
{
    public static FaceRecognizerOptions Default { get; } = new();

    /// <summary>Gesichter darunter werden nie Lernbeispiel; als Anfrage bekommen sie einen abgewerteten Score.</summary>
    public float MinQuality { get; init; } = 0.4f;

    /// <summary>Score-Faktor für Anfragen unter der Mindestqualität.</summary>
    public float LowQualityScoreFactor { get; init; } = 0.75f;

    /// <summary>k der k-nächsten-Nachbarn je Person: der Mittelwert der k ähnlichsten Beispiele entscheidet (nie ein Einzel-Nachbar, sobald die Person ≥ k Beispiele hat).</summary>
    public int NeighborCount { get; init; } = 3;

    /// <summary>Distanzprüfung: liegt die k-NN-Ähnlichkeit des besten Kandidaten darunter → „unbekannt“.</summary>
    public float UnknownSimilarity { get; init; } = 0.45f;

    /// <summary>Kombinierter Score (k-NN + linearer Klassifikator) darunter → „unbekannt“.</summary>
    public float MinScore { get; init; } = 0.45f;

    /// <summary>Linearen Klassifikator je Person verwenden (Ensemble wie digiKam); aus = nur k-NN.</summary>
    public bool UseLinearClassifier { get; init; } = true;

    /// <summary>Höchstens so viele Gegenbeispiele je Person (Hälfte die ähnlichsten anderer Personen, Rest zufällig).</summary>
    public int MaxNegatives { get; init; } = 1000;

    /// <summary>Durchläufe des SGD-Trainings je Person.</summary>
    public int Epochs { get; init; } = 6;

    /// <summary>L2-Regularisierung des linearen Klassifikators.</summary>
    public float L2 { get; init; } = 1e-4f;

    /// <summary>Feste Zufallssaat (Reihenfolge, Gegenbeispiel-Stichprobe) — gleiche Eingabe ergibt dasselbe Modell.</summary>
    public int Seed { get; init; } = 4711;

    /// <summary>Vorauswahl: k-NN wird nur für so viele Personen gerechnet (nächste Zentren + beste lineare Scores).</summary>
    public int CandidatePersons { get; init; } = 24;

    /// <summary>Höchstens so viele Alternativen im Vorschlag.</summary>
    public int MaxAlternatives { get; init; } = 5;

    /// <summary>Ausreißer beim Bestätigen: k-NN-Ähnlichkeit zur Person darunter → Rückfrage.</summary>
    public float OutlierMinSimilarity { get; init; } = 0.4f;

    /// <summary>Ausreißer beim Bestätigen: robuste Abweichung vom Median der Person (ab 5 Beispielen).</summary>
    public float OutlierRobustZ { get; init; } = 3.5f;

    /// <summary>Ausreißer beim Bestätigen: eine andere Person ist um mindestens so viel ähnlicher.</summary>
    public float CloserPersonMargin { get; init; } = 0.08f;
}

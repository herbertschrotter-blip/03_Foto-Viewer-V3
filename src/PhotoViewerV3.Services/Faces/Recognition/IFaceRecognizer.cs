namespace PhotoViewerV3.Services.Faces.Recognition;

/// <summary>
/// Lernt Personen aus BESTÄTIGTEN Gesichtern (ADR-0036): Ensemble aus k-nächsten-Nachbarn und einem linearen
/// Klassifikator je Person, Distanzprüfung „keiner nah genug → unbekannt“, Qualitätsfilter, Ablehnungen.
/// Rein und deterministisch; das Modell ist unveränderlich und thread-sicher lesbar.
/// </summary>
public interface IFaceRecognizer
{
    /// <summary>
    /// Lernt komplett neu aus <paramref name="confirmed"/> (nur Einträge mit <see cref="FaceSample.ConfirmedPersonId"/>
    /// und Qualität ≥ <see cref="FaceRecognizerOptions.MinQuality"/>). Nach einer Korrektur einfach mit den verbleibenden
    /// Beispielen erneut aufrufen — es bleibt kein Rest des entfernten Beispiels im Modell.
    /// </summary>
    /// <param name="rejections">Abgelehnte Vorschläge; das Modell bietet für diese Gesichter die Person nie wieder an.</param>
    IFaceModel Train(
        IReadOnlyList<FaceSample> confirmed,
        IReadOnlyCollection<FaceRejection> rejections,
        FaceRecognizerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ausreißer-Prüfung beim Bestätigen: passt <paramref name="face"/> kaum zu den bisherigen Beispielen von
    /// <paramref name="personId"/> (oder deutlich besser zu einer anderen Person) → <see cref="OutlierCheck.IsOutlier"/>
    /// = „Rückfrage empfohlen“. Ist das Gesicht selbst schon Beispiel, wird es dabei ausgeklammert.
    /// </summary>
    OutlierCheck CheckOutlier(FaceSample face, int personId, IFaceModel model);
}

/// <summary>Gelerntes, unveränderliches Modell. Alle Methoden sind thread-sicher.</summary>
public interface IFaceModel
{
    /// <summary>Länge der Fingerabdrücke (0 bei leerem Modell).</summary>
    int Dimension { get; }

    /// <summary>Personen mit mindestens einem Lernbeispiel.</summary>
    IReadOnlyCollection<int> PersonIds { get; }

    /// <summary>Anzahl Lernbeispiele insgesamt.</summary>
    int ExampleCount { get; }

    /// <summary>Bestätigte Gesichter, die wegen Qualität oder ungültigem Fingerabdruck NICHT gelernt wurden.</summary>
    int IgnoredCount { get; }

    int ExampleCountOf(int personId);

    /// <summary>
    /// Vorschlag für ein Gesicht. Personen, die für dieses Gesicht abgelehnt wurden (Training) oder in
    /// <paramref name="excludedPersons"/> stehen, werden übersprungen — der nächstbeste Kandidat rückt nach.
    /// </summary>
    FaceSuggestion Suggest(FaceSample face, IReadOnlyCollection<int>? excludedPersons = null);

    /// <summary>Vorschläge für viele Gesichter, parallel; Ergebnis in Eingabe-Reihenfolge.</summary>
    IReadOnlyList<FaceSuggestion> SuggestMany(IReadOnlyList<FaceSample> faces, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bündelt unbekannte Gesichter zu Gruppen-Vorschlägen (ADR-0036): streng (Average Linkage auf Kosinus-Ähnlichkeit,
/// lieber mehrere kleine reine Gruppen), Mitglieder nach Nähe zum Zentrum sortiert, zweifelhafte markiert.
/// </summary>
public interface IFaceClusterer
{
    FaceClusterResult Cluster(
        IReadOnlyList<FaceSample> unassigned,
        FaceClusterOptions? options = null,
        CancellationToken cancellationToken = default);
}

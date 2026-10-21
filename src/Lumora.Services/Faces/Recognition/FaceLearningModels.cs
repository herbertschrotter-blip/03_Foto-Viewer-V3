namespace Lumora.Services.Faces.Recognition;

// Ein- und Ausgabe-Records von „Lernen & Gruppieren“ (ADR-0036). Bewusst eigene Typen ohne Bezug zu Schema/EF:
// Welle 2 bildet die Gesichter-Zeilen des Katalogs auf FaceSample ab.

/// <summary>
/// Ein Gesicht als Eingabe für Lernen, Vorschlagen und Gruppieren.
/// </summary>
/// <param name="FaceId">Stabile Kennung des Gesichts (Katalog-Zeile).</param>
/// <param name="Embedding">Fingerabdruck (ArcFace: 512 float). Wird intern L2-normiert; Nullvektoren gelten als ungültig.</param>
/// <param name="Quality">Gesichtsqualität 0…1 (1 = best). Unter der Mindestqualität nie Lernbeispiel.</param>
/// <param name="ConfirmedPersonId">
/// Person, die ein MENSCH bestätigt hat. Nur diese Gesichter sind Lernbeispiele — Welle 2 darf hier nie eine
/// automatisch übernommene Zuordnung eintragen (kein Selbstverstärken).
/// </param>
public sealed record FaceSample(long FaceId, ReadOnlyMemory<float> Embedding, float Quality, int? ConfirmedPersonId = null);

/// <summary>Ein abgelehnter Vorschlag: „Gesicht <paramref name="FaceId"/> ist NICHT Person <paramref name="PersonId"/>“.</summary>
public readonly record struct FaceRejection(long FaceId, int PersonId);

/// <summary>Kandidat mit Score (0…1, höher = sicherer).</summary>
public readonly record struct PersonScore(int PersonId, float Score);

/// <summary>
/// Vorschlag für ein Gesicht.
/// </summary>
/// <param name="PersonId">Vorgeschlagene Person; <c>null</c>, wenn <paramref name="Unknown"/>.</param>
/// <param name="Score">Score des besten Kandidaten (0…1), auch wenn er nicht reicht.</param>
/// <param name="Similarity">Mittlere Kosinus-Ähnlichkeit zu den nächsten Beispielen des besten Kandidaten.</param>
/// <param name="Alternatives">
/// Weitere Kandidaten absteigend nach Score (ohne <paramref name="PersonId"/>); bei „unbekannt“ inklusive des besten
/// Kandidaten, damit die Oberfläche „vielleicht …?“ anbieten kann. Abgelehnte Personen fehlen immer.
/// </param>
/// <param name="Unknown">Keine Person ist nah genug bzw. sicher genug.</param>
public sealed record FaceSuggestion(
    int? PersonId,
    float Score,
    float Similarity,
    IReadOnlyList<PersonScore> Alternatives,
    bool Unknown)
{
    public static FaceSuggestion None { get; } = new(null, 0f, 0f, [], true);
}

/// <summary>Grund des Ergebnisses einer Ausreißer-Prüfung.</summary>
public enum OutlierReason
{
    /// <summary>Passt zu den bisherigen Beispielen.</summary>
    Fits,

    /// <summary>Person hat noch keine Beispiele — nichts zu vergleichen.</summary>
    FirstExample,

    /// <summary>Kaum Ähnlichkeit zu den Beispielen der Person (absolute Schwelle).</summary>
    FarFromExamples,

    /// <summary>Deutlich weniger ähnlich als die Beispiele der Person untereinander (robuste Abweichung).</summary>
    UnusualForPerson,

    /// <summary>Passt deutlich besser zu einer anderen Person.</summary>
    CloserToOtherPerson,

    /// <summary>Qualität unter der Mindestqualität — wird ohnehin kein Lernbeispiel.</summary>
    LowQuality,

    /// <summary>Ungültiger Fingerabdruck (leer, falsche Länge, Nullvektor).</summary>
    InvalidEmbedding,
}

/// <summary>
/// Ergebnis der Ausreißer-Prüfung beim Bestätigen. <see cref="IsOutlier"/> = „Rückfrage empfohlen“ statt still aufnehmen.
/// </summary>
/// <param name="IsOutlier">Rückfrage empfohlen.</param>
/// <param name="Score">Ähnlichkeit zu den nächsten Beispielen der Person (Kosinus, Mittel der k nächsten).</param>
/// <param name="TypicalScore">Typischer Wert derselben Größe unter den Beispielen der Person (Median), sonst 0.</param>
/// <param name="Kind">Maschinenlesbarer Grund.</param>
/// <param name="Reason">Deutscher Text für die Rückfrage.</param>
/// <param name="CloserPersonId">Bei <see cref="OutlierReason.CloserToOtherPerson"/>: die besser passende Person.</param>
public sealed record OutlierCheck(
    bool IsOutlier,
    float Score,
    float TypicalScore,
    OutlierReason Kind,
    string Reason,
    int? CloserPersonId = null);

/// <summary>Mitglied einer Gruppe mit Abstand zum Gruppenzentrum.</summary>
/// <param name="FaceId">Gesicht.</param>
/// <param name="Distance">Kosinus-Distanz (1 − cos) zum normierten Gruppenzentrum.</param>
/// <param name="OutlierScore">Robuste Abweichung vom Median der Gruppe (0 = typisch; ab <see cref="FaceClusterOptions.OutlierRobustZ"/> Ausreißer).</param>
/// <param name="IsOutlier">Zweifelhaftes Mitglied — die Oberfläche hebt es hervor.</param>
public readonly record struct FaceGroupMember(long FaceId, float Distance, float OutlierScore, bool IsOutlier);

/// <summary>
/// Gruppen-Vorschlag: vermutlich dieselbe (noch unbekannte) Person.
/// </summary>
/// <param name="Members">Nach Nähe zum Zentrum sortiert (nächste zuerst, Ausreißer am Ende).</param>
/// <param name="Cohesion">Mittlere Kosinus-Ähnlichkeit aller Paare der Gruppe (Average-Linkage-Maß, 0…1).</param>
/// <param name="Centroid">Normiertes Gruppenzentrum — z. B. um mit <see cref="IFaceModel.Suggest(FaceSample, IReadOnlyCollection{int}?)"/> einen Namen vorzuschlagen.</param>
public sealed record FaceGroup(IReadOnlyList<FaceGroupMember> Members, float Cohesion, ReadOnlyMemory<float> Centroid)
{
    public int Count => Members.Count;

    public int OutlierCount => Members.Count(m => m.IsOutlier);
}

/// <summary>Ergebnis der Gruppierung.</summary>
/// <param name="Groups">Gruppen absteigend nach Größe.</param>
/// <param name="Noise">Gesichter ohne Gruppe (zu wenig ähnliche Partner oder aus einer Gruppe ausgeschlossen).</param>
/// <param name="SkippedLowQuality">Gesichter unter der Mindestqualität bzw. mit ungültigem Fingerabdruck — nicht gruppiert.</param>
public sealed record FaceClusterResult(
    IReadOnlyList<FaceGroup> Groups,
    IReadOnlyList<long> Noise,
    IReadOnlyList<long> SkippedLowQuality)
{
    public static FaceClusterResult Empty { get; } = new([], [], []);
}

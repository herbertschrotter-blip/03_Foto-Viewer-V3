using PhotoViewerV3.Domain.Enums;

namespace PhotoViewerV3.Services.Faces.Indexing;

/// <summary>Box relativ zum analysierten Bild (0..1).</summary>
public readonly record struct RelativeBox(float X, float Y, float W, float H)
{
    /// <summary>IoU zweier relativer Boxen (ohne „+1“, Flächen in Bildanteilen).</summary>
    public static float IoU(RelativeBox a, RelativeBox b)
    {
        var w = Math.Max(0f, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
        var h = Math.Max(0f, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        var inter = w * h;
        var union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0f : inter / union;
    }
}

/// <summary>Ein vorhandenes Gesicht der Datei mit den Feldern, die der Benutzer bzw. das Lernen gesetzt hat.</summary>
public sealed record ExistingFace(int Id, RelativeBox Box, FaceState State, int? PersonId, int? SuggestedPersonId, float? SuggestionScore);

/// <summary>Ergebnis des Abgleichs: welche neue Erkennung welches vorhandene Gesicht fortschreibt.</summary>
public sealed record FaceMergePlan(
    IReadOnlyList<(int NewIndex, ExistingFace Existing)> Updates,
    IReadOnlyList<int> Inserts,
    IReadOnlyList<int> DeleteIds);

/// <summary>
/// Abgleich bei erneuter Analyse einer Datei (neue Modell-Version, „Index neu aufbauen“, ADR-0035): jede neue Erkennung
/// übernimmt das vorhandene Gesicht mit der größten Überlappung (IoU ≥ <see cref="MinIoU"/>, gierig nach IoU
/// absteigend) — Id, Person, Zustand bleiben, damit Bestätigungen, Ablehnungen (<c>FaceRejections</c>) und Titelbilder
/// (<c>Persons.CoverFaceId</c>) nicht verloren gehen. Ein reiner Vorschlag (<see cref="FaceState.Suggested"/>) wird
/// zurückgesetzt, weil er auf dem alten Fingerabdruck beruht. Nicht wiedergefundene unbestätigte Gesichter und
/// Vorschläge werden gelöscht; bestätigte und ignorierte bleiben mit ihrer alten ModelVersion stehen (die Entscheidung
/// des Benutzers geht nicht still verloren — Welle 2 erkennt sie an der abweichenden ModelVersion).
/// </summary>
public static class FaceRowMerger
{
    public const float MinIoU = 0.5f;

    public static FaceMergePlan Plan(IReadOnlyList<ExistingFace> existing, IReadOnlyList<RelativeBox> detected)
    {
        var pairs = new List<(float IoU, int New, int Old)>();
        for (var n = 0; n < detected.Count; n++)
        {
            for (var o = 0; o < existing.Count; o++)
            {
                var iou = RelativeBox.IoU(detected[n], existing[o].Box);
                if (iou >= MinIoU)
                {
                    pairs.Add((iou, n, o));
                }
            }
        }

        var usedNew = new bool[detected.Count];
        var usedOld = new bool[existing.Count];
        var updates = new List<(int, ExistingFace)>();
        foreach (var (_, n, o) in pairs.OrderByDescending(p => p.IoU).ThenBy(p => p.New).ThenBy(p => p.Old))
        {
            if (usedNew[n] || usedOld[o])
            {
                continue;
            }

            usedNew[n] = true;
            usedOld[o] = true;
            updates.Add((n, existing[o]));
        }

        var inserts = Enumerable.Range(0, detected.Count).Where(n => !usedNew[n]).ToList();
        // Vom Benutzer entschiedene Gesichter (bestätigt/ignoriert) bleiben auch ungefunden stehen — mit ihrer alten
        // ModelVersion; nur unbestätigte und Vorschläge verschwinden.
        var deletes = Enumerable.Range(0, existing.Count)
            .Where(o => !usedOld[o] && existing[o].State is FaceState.Unconfirmed or FaceState.Suggested)
            .Select(o => existing[o].Id)
            .ToList();
        return new FaceMergePlan(updates.OrderBy(u => u.Item1).ToList(), inserts, deletes);
    }

    /// <summary>Zustand eines fortgeschriebenen Gesichts: Bestätigt/Ignoriert bleiben, ein Vorschlag fällt zurück auf unbestätigt.</summary>
    public static (FaceState State, int? SuggestedPersonId, float? SuggestionScore) CarryState(ExistingFace existing)
        => existing.State == FaceState.Suggested
            ? (FaceState.Unconfirmed, null, null)
            : (existing.State, existing.State == FaceState.Unconfirmed ? null : existing.SuggestedPersonId,
               existing.State == FaceState.Unconfirmed ? null : existing.SuggestionScore);
}

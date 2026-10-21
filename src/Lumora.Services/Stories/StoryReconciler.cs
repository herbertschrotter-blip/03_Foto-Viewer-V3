using Lumora.Domain.Enums;

namespace Lumora.Services.Stories;

/// <summary>Eine gespeicherte Story im Bereich einer Neuberechnung (nur die Felder, die das Wiederfinden braucht).</summary>
public sealed class ExistingStory
{
    public required int Id { get; init; }

    public required StoryKind Kind { get; init; }

    public required string Signature { get; init; }

    public long StartAt { get; init; }

    public long EndAt { get; init; }

    public int? PersonId { get; init; }

    public int? PlaceId { get; init; }

    public bool IsPinned { get; init; }

    public bool IsHidden { get; init; }

    public bool HasCustomTitle { get; init; }

    public bool CoverIsManual { get; init; }

    public int? CoverFileId { get; init; }

    /// <summary>Gespeicherter Zähler (weicht er von den Einträgen ab — z. B. nach entfernten Dateien —, wird nachgezogen).</summary>
    public int FileCount { get; init; }

    /// <summary>Alle Einträge (auch Ausschlüsse) mit Herkunft und Tagesabschnitt.</summary>
    public Dictionary<int, (StoryItemOrigin Origin, int Day)> Items { get; init; } = new();

    /// <summary>Benutzer-Zustand, den eine Neuberechnung nie verlieren darf.</summary>
    public bool HasUserState => IsPinned || IsHidden || HasCustomTitle || CoverIsManual || Items.Values.Any(v => v.Origin != StoryItemOrigin.Auto);
}

/// <summary>Zuordnung einer erkannten Story zu einer gespeicherten (oder null = neu anlegen).</summary>
public sealed record StoryMatch(DetectedStory Detected, ExistingStory? Existing);

/// <summary>Ergebnis des Abgleichs: zuordnen/anlegen, löschen, unverändert behalten (nicht mehr erkannt, aber mit Benutzer-Zustand).</summary>
public sealed record StoryReconcilePlan(IReadOnlyList<StoryMatch> Matches, IReadOnlyList<ExistingStory> Delete, IReadOnlyList<ExistingStory> Keep);

/// <summary>
/// Abgleich erkannter mit gespeicherten Storys (ADR-0058): zuerst über die gleiche Signatur, dann — wenn sich Ränder
/// verschoben haben (neue Fotos am Anfang/Ende, andere Pause, Ereignis wird Reise) — innerhalb derselben Familie über die
/// meisten gemeinsamen Dateien, ersatzweise die größte zeitliche Überschneidung. Gespeicherte Storys mit
/// Benutzer-Zustand werden bevorzugt wiedergefunden. Was ohne Gegenstück bleibt, wird gelöscht — außer es trägt
/// Benutzer-Zustand (dann bleibt es stehen, bis seine Dateien weg sind). Familien: Ereignis/Reise zusammen, ungefähre
/// Gruppen nur über die Signatur, Personen je Person, Orte je Ort.
/// </summary>
public static class StoryReconciler
{
    public static StoryReconcilePlan Reconcile(IReadOnlyList<ExistingStory> existing, IReadOnlyList<DetectedStory> detected)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(detected);
        var matches = new Dictionary<DetectedStory, ExistingStory?>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<int>();
        var bySignature = new Dictionary<string, ExistingStory>(StringComparer.Ordinal);
        foreach (var e in existing)
        {
            bySignature.TryAdd(e.Signature, e);
        }

        foreach (var d in detected)
        {
            if (bySignature.TryGetValue(d.Signature, out var e) && Family(e) == Family(d) && used.Add(e.Id))
            {
                matches[d] = e;
            }
        }

        // Verschobene Ränder: Kandidaten je Familie nach Benutzer-Zustand, gemeinsamen Dateien, Überschneidung.
        // Dateien → offene gespeicherte Story je Familie (gemeinsame Dateien zählen ohne Vergleich jeder mit jeder).
        var candidates = new List<(DetectedStory D, ExistingStory E, bool State, int Shared, long Overlap)>();
        var pools = new Dictionary<string, List<ExistingStory>>(StringComparer.Ordinal);
        var owners = new Dictionary<string, Dictionary<int, ExistingStory>>(StringComparer.Ordinal);
        foreach (var e in existing.Where(e => !used.Contains(e.Id) && !e.Signature.StartsWith("A:", StringComparison.Ordinal)))
        {
            var family = Family(e);
            if (!pools.TryGetValue(family, out var pool))
            {
                pools[family] = pool = new List<ExistingStory>();
                owners[family] = new Dictionary<int, ExistingStory>();
            }

            pool.Add(e);
            foreach (var (fileId, v) in e.Items)
            {
                if (v.Origin != StoryItemOrigin.Excluded)
                {
                    owners[family].TryAdd(fileId, e);
                }
            }
        }

        foreach (var d in detected)
        {
            var family = Family(d);
            if (matches.ContainsKey(d) || d.Signature.StartsWith("A:", StringComparison.Ordinal) || !pools.TryGetValue(family, out var pool))
            {
                continue;
            }

            var shared = new Dictionary<ExistingStory, int>(ReferenceEqualityComparer.Instance);
            var owner = owners[family];
            foreach (var item in d.Items)
            {
                if (owner.TryGetValue(item.FileId, out var e))
                {
                    shared[e] = shared.GetValueOrDefault(e) + 1;
                }
            }

            if (shared.Count > 0)
            {
                foreach (var (e, count) in shared)
                {
                    candidates.Add((d, e, e.HasUserState, count, Math.Min(d.EndAt, e.EndAt) - Math.Max(d.StartAt, e.StartAt)));
                }

                continue;
            }

            // Ohne gemeinsame Dateien (z. B. nach geänderten Aufnahmedaten): zeitliche Überschneidung.
            foreach (var e in pool)
            {
                var overlap = Math.Min(d.EndAt, e.EndAt) - Math.Max(d.StartAt, e.StartAt);
                if (overlap > 0 || (overlap == 0 && d.StartAt <= e.EndAt && e.StartAt <= d.EndAt))
                {
                    candidates.Add((d, e, e.HasUserState, 0, overlap));
                }
            }
        }

        foreach (var c in candidates.OrderByDescending(c => c.State).ThenByDescending(c => c.Shared).ThenByDescending(c => c.Overlap))
        {
            if (!matches.ContainsKey(c.D) && used.Add(c.E.Id))
            {
                matches[c.D] = c.E;
            }
        }

        var plan = detected.Select(d => new StoryMatch(d, matches.GetValueOrDefault(d))).ToList();
        var delete = new List<ExistingStory>();
        var keep = new List<ExistingStory>();
        foreach (var e in existing.Where(e => !used.Contains(e.Id)))
        {
            (e.HasUserState ? keep : delete).Add(e);
        }

        return new StoryReconcilePlan(plan, delete, keep);
    }

    /// <summary>Familie fürs Wiederfinden: Ereignis/Reise, ungefähre Gruppe, Person, Ort.</summary>
    private static string Family(ExistingStory e) => Family(e.Kind, e.Signature, e.PersonId, e.PlaceId);

    private static string Family(DetectedStory d) => Family(d.Kind, d.Signature, d.PersonId, d.PlaceId);

    private static string Family(StoryKind kind, string signature, int? personId, int? placeId)
        => kind switch
        {
            StoryKind.Event or StoryKind.Trip => signature.StartsWith("A:", StringComparison.Ordinal) ? "A" : "ET",
            StoryKind.Person => "P" + personId,
            StoryKind.Place => "O" + placeId,
            _ => kind.ToString(),
        };
}

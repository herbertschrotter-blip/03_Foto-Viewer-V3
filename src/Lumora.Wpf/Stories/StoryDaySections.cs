using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumora.Stories;

/// <summary>Abschnitt eines einzelnen Tages nach Tageszeit („Vormittag“, „Abend“ …) mit den Dateien in Aufnahme-Reihenfolge.</summary>
public sealed record StoryDaySection(string Label, IReadOnlyList<int> FileIds);

/// <summary>
/// Teilt die Dateien EINES Tages nach Tageszeit der Aufnahme (Ortszeit) in Abschnitte — für Storys über genau einen Tag
/// (Rückblick „Heute vor 3 Jahren“, Ereignis), wie im Mockup 09 („Vormittag · Prater“, „Abend · Innere Stadt“). Sachlich:
/// nur die Tageszeit, kein erfundener Text. Grenzen: Nacht bis 5 Uhr, Vormittag bis 12, Nachmittag bis 17, danach Abend.
/// Weniger als zwei belegte Tageszeiten → kein Abschnitt (der Tag bleibt ein Abschnitt).
/// </summary>
public static class StoryDaySections
{
    public static IReadOnlyList<StoryDaySection> Split(IReadOnlyList<(int FileId, DateTime? LocalTime)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var groups = new List<(string Label, List<int> Ids)>();
        foreach (var (fileId, time) in files)
        {
            var label = time is { } t ? Label(t.Hour) : null;
            if (label is null)
            {
                // ohne Uhrzeit: zum vorigen Abschnitt (Reihenfolge der Aufnahme bleibt)
                if (groups.Count == 0)
                {
                    groups.Add((string.Empty, new List<int>()));
                }

                groups[^1].Ids.Add(fileId);
                continue;
            }

            if (groups.Count > 0 && (groups[^1].Label == label || groups[^1].Label.Length == 0))
            {
                groups[^1] = (label, groups[^1].Ids);
                groups[^1].Ids.Add(fileId);
                continue;
            }

            var existing = groups.FindIndex(g => g.Label == label);
            if (existing >= 0)
            {
                groups[existing].Ids.Add(fileId); // Reihenfolge nicht streng nach Uhrzeit — derselbe Abschnitt
                continue;
            }

            groups.Add((label, new List<int> { fileId }));
        }

        return groups.Count(g => g.Label.Length > 0) < 2
            ? []
            : groups.Select(g => new StoryDaySection(g.Label.Length == 0 ? "Ohne Uhrzeit" : g.Label, g.Ids)).ToList();
    }

    /// <summary>„Nacht“, „Vormittag“, „Nachmittag“, „Abend“.</summary>
    public static string Label(int hour) => hour switch
    {
        < 5 => "Nacht",
        < 12 => "Vormittag",
        < 17 => "Nachmittag",
        _ => "Abend",
    };
}

using System.Text.RegularExpressions;

namespace Lumora.Services.Sorter;

/// <summary>
/// Pattern-Engine des Sortierers (Port von V2 <c>Get-FileGroups</c> / <c>Split-IntoSubGroups</c>):
/// Stufe 1 prüft den Dateinamen ohne Endung gegen die aktiven Patterns in Prioritätsreihenfolge,
/// der erste Treffer gewinnt, Schlüssel = kleingeschriebener Treffer, kein Treffer → „_unsorted“.
/// Sub-Level (Stufe 2..N) prüfen — wie in V2 — den vollständigen Dateinamen; kein Treffer → „_other“.
/// Ungültige Regex werden übersprungen und als Warnung gemeldet.
/// </summary>
public static class SorterPatternEngine
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Schutz vor katastrophalem Backtracking bei Benutzer-Patterns.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public sealed record CompiledPattern(string Name, Regex Regex, int GroupCapture);

    public static Regex Compile(string pattern) => new(pattern, Options, MatchTimeout);

    public static bool TryCompile(string? pattern, out Regex? regex, out string? error)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Regex fehlt.";
            return false;
        }

        try
        {
            regex = Compile(pattern);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Höchste Gruppennummer eines Regex (0 = keine Capture-Gruppe).</summary>
    public static int GroupCount(Regex regex) => regex.GetGroupNumbers().Max();

    /// <summary>Aktive Patterns nach Priorität (aufsteigend) kompilieren; ungültige überspringen.</summary>
    public static IReadOnlyList<CompiledPattern> CompilePatterns(IEnumerable<SorterPattern> patterns, ICollection<string>? warnings = null)
    {
        var result = new List<CompiledPattern>();
        foreach (var p in patterns.Where(p => p.Enabled).OrderBy(p => p.Priority).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (TryCompile(p.Regex, out var regex, out var error))
            {
                result.Add(new CompiledPattern(p.Name, regex!, Math.Max(0, p.GroupCapture)));
            }
            else
            {
                warnings?.Add($"Pattern „{p.Name}“ übersprungen: ungültiger Regex ({error}).");
            }
        }

        return result;
    }

    /// <summary>Aktive Sub-Level in Listenreihenfolge kompilieren (= Ordnertiefe); ungültige überspringen.</summary>
    public static IReadOnlyList<CompiledPattern> CompileSubLevels(IEnumerable<SorterSubLevel> subLevels, ICollection<string>? warnings = null)
    {
        var result = new List<CompiledPattern>();
        foreach (var level in subLevels.Where(l => l.Enabled))
        {
            var name = string.IsNullOrWhiteSpace(level.Name) ? "Ebene" : level.Name;
            if (TryCompile(level.Regex, out var regex, out var error))
            {
                result.Add(new CompiledPattern(name, regex!, Math.Max(0, level.GroupCapture)));
            }
            else
            {
                warnings?.Add($"Sub-Level „{name}“ übersprungen: ungültiger Regex ({error}).");
            }
        }

        return result;
    }

    /// <summary>Stufe 1: Dateien gruppieren. Gleiche Schlüssel aus verschiedenen Patterns landen in einer Gruppe.</summary>
    public static List<SorterGroup> GroupFiles(
        IEnumerable<SorterFileInfo> files,
        IReadOnlyList<CompiledPattern> patterns,
        ICollection<string>? warnings = null)
    {
        var map = new Dictionary<string, SorterGroup>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var baseName = Path.GetFileNameWithoutExtension(file.Name);
            SorterGroup? target = null;

            foreach (var cp in patterns)
            {
                string? key;
                try
                {
                    var match = cp.Regex.Match(baseName);
                    if (!match.Success)
                    {
                        continue;
                    }

                    key = ExtractKey(match, cp.GroupCapture);
                }
                catch (RegexMatchTimeoutException)
                {
                    warnings?.Add($"Pattern „{cp.Name}“: Zeitüberschreitung bei „{file.Name}“ — Datei bleibt für dieses Pattern unberücksichtigt.");
                    continue;
                }

                if (key is null)
                {
                    continue;
                }

                if (!map.TryGetValue(key, out target))
                {
                    target = new SorterGroup(key, cp.Name);
                    map[key] = target;
                }

                break;
            }

            if (target is null)
            {
                if (!map.TryGetValue(SorterGroup.UnsortedKey, out target))
                {
                    target = new SorterGroup(SorterGroup.UnsortedKey, SorterGroup.UnsortedPatternName);
                    map[SorterGroup.UnsortedKey] = target;
                }
            }

            target.Files.Add(file);
        }

        var groups = map.Values.ToList();
        foreach (var group in groups)
        {
            SortFiles(group.Files);
        }

        SortGroups(groups);
        return groups;
    }

    /// <summary>
    /// Stufe 2..N: teilt die Dateien der Gruppe nach dem Sub-Level <paramref name="levelIndex"/> und
    /// rekursiv weiter. Vorhandene Sub-Gruppen werden ersetzt.
    /// </summary>
    public static void SplitIntoSubGroups(SorterGroup group, IReadOnlyList<CompiledPattern> levels, int levelIndex = 0)
    {
        group.SubGroups.Clear();
        if (levelIndex >= levels.Count || group.Files.Count == 0)
        {
            return;
        }

        var level = levels[levelIndex];
        var map = new Dictionary<string, SorterGroup>(StringComparer.Ordinal);

        foreach (var file in group.Files)
        {
            string key;
            try
            {
                var match = level.Regex.Match(file.Name);
                key = match.Success ? ExtractKey(match, level.GroupCapture) ?? SorterGroup.OtherKey : SorterGroup.OtherKey;
            }
            catch (RegexMatchTimeoutException)
            {
                key = SorterGroup.OtherKey;
            }

            if (!map.TryGetValue(key, out var sub))
            {
                sub = new SorterGroup(key, level.Name, group.Level + 1);
                map[key] = sub;
            }

            sub.Files.Add(file);
        }

        var subs = map.Values.ToList();
        SortGroups(subs);
        foreach (var sub in subs)
        {
            SortFiles(sub.Files);
            SplitIntoSubGroups(sub, levels, levelIndex + 1);
            group.SubGroups.Add(sub);
        }
    }

    /// <summary>
    /// Gruppenschlüssel aus einem Treffer: GroupCapture 0 = ganzer Treffer, n = Gruppe n; existiert die
    /// Gruppe nicht oder ist sie leer, zählt der ganze Treffer. Leerer Treffer → null (kein Schlüssel).
    /// </summary>
    public static string? ExtractKey(Match match, int groupCapture)
    {
        var value = groupCapture > 0 && groupCapture < match.Groups.Count && match.Groups[groupCapture].Success
            ? match.Groups[groupCapture].Value
            : match.Value;

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    /// <summary>Alphabetisch nach Schlüssel; „_unsorted“ und „_other“ ans Ende.</summary>
    public static void SortGroups(List<SorterGroup> groups)
    {
        groups.Sort((a, b) =>
        {
            var ra = IsRestKey(a.Key) ? 1 : 0;
            var rb = IsRestKey(b.Key) ? 1 : 0;
            return ra != rb ? ra.CompareTo(rb) : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
        });
    }

    public static void SortFiles(List<SorterFileInfo> files)
        => files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

    private static bool IsRestKey(string key)
        => string.Equals(key, SorterGroup.UnsortedKey, StringComparison.Ordinal)
           || string.Equals(key, SorterGroup.OtherKey, StringComparison.Ordinal);
}

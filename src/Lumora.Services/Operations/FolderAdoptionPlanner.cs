namespace Lumora.Services.Operations;

/// <summary>
/// Reine Plan-Logik von „Ordner in die Bibliothek übernehmen“ (ADR-0034) — ohne Dateisystem-Zugriff außer über die
/// <see cref="IAdoptionTargetProbe"/>, daher vollständig unit-testbar.
/// <list type="bullet">
/// <item>Der übernommene Ordner landet als Unterordner im gewählten Zielordner; die Unterordner-Struktur bleibt erhalten,
/// Dateien bleiben immer in „ihrem“ Ordner.</item>
/// <item>Leere Teilbäume (keine Datei darunter) werden nicht angelegt.</item>
/// <item>Zusammenziehen (Option, Standard an): ein Ordner ohne Dateien mit genau einem (nicht leeren) Unterordner ist ein
/// Durchgangsordner und entfällt; der nächste rückt an seine Stelle, bis zum ersten Ordner mit Dateien oder mehr als einem
/// Unterordner — dessen Name bleibt. Gilt auch für den übernommenen Ordner selbst (<c>Import/leer/inhalt</c> →
/// <c>Ziel/inhalt</c>).</item>
/// <item>Würden zwei Geschwister nach dem Zusammenziehen gleich heißen, werden sie an dieser Stelle nicht zusammengezogen
/// (die Struktur bleibt unterscheidbar). Bleibt danach ein gleicher Name (echte Namensgleichheit), werden sie
/// zusammengeführt wie mit einem vorhandenen Ordner.</item>
/// <item>Existiert ein Zielordner bereits (ohne Groß-/Kleinschreibung), wird hineingeführt — mit seinem vorhandenen
/// Namen. Liegt dort eine gleichnamige Datei, bekommt der Ordner „_1“, „_2“ ….</item>
/// <item>Dateinamenskonflikte (vorhanden oder im Plan schon vergeben) → „name_1.ext“, „name_2.ext“ … wie Flatten.</item>
/// </list>
/// </summary>
public static class FolderAdoptionPlanner
{
    private const int MaxCollisionPasses = 8;

    public static FolderAdoptionPlan Plan(
        AdoptionSourceSnapshot snapshot,
        string targetFolder,
        bool collapseEmptyChains,
        IAdoptionTargetProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        probe ??= FileSystemAdoptionTargetProbe.Instance;

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder));
        var source = snapshot.Root;
        if (IsSameOrInside(target, source.Path))
        {
            throw new ArgumentException("Der Zielordner darf nicht im übernommenen Ordner liegen.", nameof(targetFolder));
        }

        var state = new PlanState(target, collapseEmptyChains, probe);
        if (source.TotalFileCount > 0)
        {
            state.Place(target, new[] { source });
        }

        return new FolderAdoptionPlan
        {
            SourceFolder = source.Path,
            TargetFolder = target,
            CollapseEmptyChains = collapseEmptyChains,
            Folders = state.Folders.Select(f => f.Build()).ToList(),
            Files = state.Files,
            CollapsedFolders = state.Collapsed,
            IgnoredEntries = snapshot.IgnoredEntries,
            Warnings = snapshot.Warnings,
        };
    }

    /// <summary>„name_1.ext“, „name_2.ext“ … — der erste Name, der weder vorhanden noch im Plan vergeben ist.</summary>
    internal static string NextFreeFileName(string folder, string fileName, Func<string, bool> isTaken)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var counter = 1; ; counter++)
        {
            var candidate = $"{baseName}_{counter}{ext}";
            if (!isTaken(Path.Combine(folder, candidate)))
            {
                return candidate;
            }
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var f = Path.TrimEndingDirectorySeparator(folder);
        var prefix = f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar; // eine Laufwerkswurzel behält ihr Trennzeichen
        return string.Equals(path, f, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FolderBuilder(string targetPath, string relative, int depth, bool mergesExisting)
    {
        public string TargetPath { get; } = targetPath;
        public List<string> Sources { get; } = new();
        public List<string> Collapsed { get; } = new();
        public int FileCount { get; set; }

        public AdoptionFolderPlan Build() => new(TargetPath, relative, depth, Sources, Collapsed, FileCount, mergesExisting);
    }

    /// <summary>Ein Eintrag beim Platzieren: Startordner, Ordner nach dem Zusammenziehen und die entfallenen Durchgangsordner.</summary>
    private sealed record Entry(AdoptionSourceFolder Start, AdoptionSourceFolder End, IReadOnlyList<AdoptionSourceFolder> Chain);

    private sealed class PlanState(string root, bool collapse, IAdoptionTargetProbe probe)
    {
        private readonly Dictionary<string, FolderBuilder> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimedFiles = new(StringComparer.OrdinalIgnoreCase);

        public List<FolderBuilder> Folders { get; } = new();
        public List<AdoptionFilePlan> Files { get; } = new();
        public List<string> Collapsed { get; } = new();

        public void Place(string parentTarget, IReadOnlyList<AdoptionSourceFolder> nodes)
        {
            var entries = nodes.Select(n => collapse ? CollapseFrom(n) : new Entry(n, n, Array.Empty<AdoptionSourceFolder>())).ToList();
            ResolveSiblingCollisions(entries);

            foreach (var entry in entries.OrderBy(e => e.End.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var folder = GetOrCreateFolder(parentTarget, entry.End.Name);
                folder.Sources.Add(entry.End.Path);
                foreach (var passed in entry.Chain)
                {
                    folder.Collapsed.Add(passed.Path);
                    Collapsed.Add(passed.Path);
                }

                foreach (var file in entry.End.Files.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                {
                    AddFile(folder, Path.Combine(entry.End.Path, file), file);
                }

                Place(folder.TargetPath, entry.End.NonEmptyChildren.ToList());
            }
        }

        /// <summary>Durchgangsordner überspringen: keine Dateien und genau ein nicht leerer Unterordner.</summary>
        private static Entry CollapseFrom(AdoptionSourceFolder start)
        {
            var chain = new List<AdoptionSourceFolder>();
            var current = start;
            while (current.Files.Count == 0)
            {
                var children = current.NonEmptyChildren.Take(2).ToList();
                if (children.Count != 1)
                {
                    break;
                }

                chain.Add(current);
                current = children[0];
            }

            return new Entry(start, current, chain);
        }

        /// <summary>Gleichnamige Geschwister nach dem Zusammenziehen: an dieser Stelle nicht zusammenziehen (Startordner bleibt).</summary>
        private static void ResolveSiblingCollisions(List<Entry> entries)
        {
            for (var pass = 0; pass < MaxCollisionPasses; pass++)
            {
                var changed = false;
                foreach (var group in entries.GroupBy(e => e.End.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
                {
                    foreach (var entry in group.Where(e => !ReferenceEquals(e.Start, e.End)))
                    {
                        var index = entries.IndexOf(entry);
                        entries[index] = new Entry(entry.Start, entry.Start, Array.Empty<AdoptionSourceFolder>());
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return;
                }
            }
        }

        private FolderBuilder GetOrCreateFolder(string parentTarget, string name)
        {
            var existingName = probe.FindExistingDirectoryName(parentTarget, name);
            var folderName = existingName ?? name;
            var path = Path.Combine(parentTarget, folderName);
            if (_byPath.TryGetValue(path, out var known))
            {
                return known; // gleichnamige Geschwister: zusammenführen
            }

            if (existingName is null && probe.FileExists(path))
            {
                // Eine Datei trägt den Ordnernamen: freien Ordnernamen suchen („inhalt_1“)
                for (var counter = 1; ; counter++)
                {
                    var candidate = Path.Combine(parentTarget, $"{name}_{counter}");
                    if (!probe.FileExists(candidate) && probe.FindExistingDirectoryName(parentTarget, $"{name}_{counter}") is null
                        && !_byPath.ContainsKey(candidate))
                    {
                        path = candidate;
                        break;
                    }
                }
            }

            var relative = Path.GetRelativePath(root, path);
            var depth = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
            var builder = new FolderBuilder(path, relative, depth, existingName is not null);
            _byPath[path] = builder;
            Folders.Add(builder);
            return builder;
        }

        private void AddFile(FolderBuilder folder, string sourcePath, string name)
        {
            var dest = Path.Combine(folder.TargetPath, name);
            var renamed = false;
            if (IsTaken(dest))
            {
                dest = Path.Combine(folder.TargetPath, NextFreeFileName(folder.TargetPath, name, IsTaken));
                renamed = true;
            }

            _claimedFiles.Add(dest);
            folder.FileCount++;
            Files.Add(new AdoptionFilePlan(sourcePath, dest, renamed));
        }

        private bool IsTaken(string path) => _claimedFiles.Contains(path) || probe.FileExists(path);
    }
}

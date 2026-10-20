namespace PhotoViewerV3.Services.Operations;

/// <summary>
/// Ordner im Quellbaum einer Übernahme (ADR-0034): Dateien direkt darin (ohne Systemdateien) und Unterordner (ohne
/// Systemordner wie „.thumbs“). Reiner Schnappschuss — vom <see cref="FolderAdoptionSourceReader"/> aus dem Dateisystem
/// gelesen, in Tests von Hand gebaut.
/// </summary>
public sealed class AdoptionSourceFolder
{
    private int? _totalFileCount;

    public AdoptionSourceFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.TrimEndingDirectorySeparator(path);
        var name = System.IO.Path.GetFileName(Path);
        Name = string.IsNullOrEmpty(name) ? Path : name;
    }

    /// <summary>Absoluter Pfad ohne abschließendes Trennzeichen.</summary>
    public string Path { get; }

    public string Name { get; }

    /// <summary>Dateinamen direkt in diesem Ordner (Medien und Nicht-Medien, ohne Systemdateien).</summary>
    public List<string> Files { get; } = new();

    public List<AdoptionSourceFolder> Children { get; } = new();

    /// <summary>Dateien in diesem Ordner und allen Unterordnern (nach dem Aufbau unveränderlich, daher zwischengespeichert).</summary>
    public int TotalFileCount => _totalFileCount ??= Files.Count + Children.Sum(c => c.TotalFileCount);

    /// <summary>Unterordner mit mindestens einer Datei im Teilbaum — leere Teilbäume werden nicht angelegt.</summary>
    public IEnumerable<AdoptionSourceFolder> NonEmptyChildren => Children.Where(c => c.TotalFileCount > 0);

    /// <summary>Hilfe für Tests: Unterordner anlegen und zurückgeben.</summary>
    public AdoptionSourceFolder AddChild(string name)
    {
        var child = new AdoptionSourceFolder(System.IO.Path.Combine(Path, name));
        Children.Add(child);
        _totalFileCount = null;
        return child;
    }

    /// <summary>Hilfe für Tests: Dateien hinzufügen.</summary>
    public AdoptionSourceFolder WithFiles(params string[] names)
    {
        Files.AddRange(names);
        _totalFileCount = null;
        return this;
    }

    internal void ResetCount() => _totalFileCount = null;
}

/// <summary>Ergebnis des Einlesens: Quellbaum plus nicht übernommene Systemeinträge (Systemordner, Thumbs.db …).</summary>
public sealed record AdoptionSourceSnapshot(AdoptionSourceFolder Root, IReadOnlyList<string> IgnoredEntries, IReadOnlyList<string> Warnings);

/// <summary>Optionen des Dialogs „Ordner in die Bibliothek übernehmen“ (ADR-0034); Standard: alles an.</summary>
public sealed record FolderAdoptionOptions
{
    public static FolderAdoptionOptions Default { get; } = new();

    /// <summary>Leere Zwischenordner zusammenziehen (Ordner ohne Dateien mit genau einem Unterordner entfallen).</summary>
    public bool CollapseEmptyChains { get; init; } = true;

    /// <summary>Quellordner danach in den Papierkorb — nur, wenn er keine Dateien mehr enthält.</summary>
    public bool RecycleSource { get; init; } = true;

    /// <summary>Eingang danach austragen (nur Katalog) — nur, wenn die ganze Eingangs-Wurzel übernommen wird.</summary>
    public bool UnregisterInbox { get; init; } = true;
}

/// <summary>Ein Zielordner der Übernahme: woher er kommt, wie viele Dateien direkt hinein, ob er schon existiert.</summary>
public sealed record AdoptionFolderPlan(
    string TargetPath,
    string RelativeTargetPath,
    int Depth,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> CollapsedPaths,
    int FileCount,
    bool MergesExisting)
{
    /// <summary>Mehr als ein Quellordner landet hier (gleichnamige Geschwister nach dem Zusammenziehen).</summary>
    public bool MergesSources => SourcePaths.Count > 1;
}

/// <summary>Eine Datei der Übernahme: Quelle → Ziel; <see cref="Renamed"/> bei Namenskonflikt („_1“, „_2“ …).</summary>
public sealed record AdoptionFilePlan(string SourcePath, string DestPath, bool Renamed);

/// <summary>
/// Plan einer Übernahme (reine Logik, <see cref="FolderAdoptionPlanner"/>): Zielordner in Anzeigereihenfolge (Baum,
/// vorne nach), Dateien mit Zielpfad und die nicht übernommenen Systemeinträge.
/// </summary>
public sealed class FolderAdoptionPlan
{
    public required string SourceFolder { get; init; }

    /// <summary>Gewählter Zielordner unter dem Bibliotheksordner; der übernommene Ordner landet darin.</summary>
    public required string TargetFolder { get; init; }

    public required bool CollapseEmptyChains { get; init; }

    public required IReadOnlyList<AdoptionFolderPlan> Folders { get; init; }

    public required IReadOnlyList<AdoptionFilePlan> Files { get; init; }

    /// <summary>Durchgangsordner (keine Dateien, genau ein Unterordner), die entfallen.</summary>
    public required IReadOnlyList<string> CollapsedFolders { get; init; }

    public IReadOnlyList<string> IgnoredEntries { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int FileCount => Files.Count;

    public int RenamedCount => Files.Count(f => f.Renamed);

    public int MergedFolderCount => Folders.Count(f => f.MergesExisting || f.MergesSources);

    public bool IsEmpty => Files.Count == 0;
}

/// <summary>Zielzustand für den Planer: vorhandene Dateien/Ordner im Bibliotheksordner (in Tests ein Fake).</summary>
public interface IAdoptionTargetProbe
{
    bool FileExists(string path);

    /// <summary>Name eines vorhandenen Unterordners von <paramref name="parent"/>, der ohne Groß-/Kleinschreibung <paramref name="name"/> heißt; sonst null.</summary>
    string? FindExistingDirectoryName(string parent, string name);
}

/// <summary>Probe gegen das echte Dateisystem.</summary>
public sealed class FileSystemAdoptionTargetProbe : IAdoptionTargetProbe
{
    public static FileSystemAdoptionTargetProbe Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public string? FindExistingDirectoryName(string parent, string name)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return null;
            }

            foreach (var dir in Directory.EnumerateDirectories(parent))
            {
                var existing = Path.GetFileName(dir);
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}

/// <summary>Kontext einer Übernahme: Quellordner, seine Eingangs-Wurzel und der Bibliotheksordner (Archiv-Wurzel).</summary>
public sealed record FolderAdoptionContext(
    int LibraryId,
    string LibraryName,
    string SourceFolder,
    int InboxRootId,
    string InboxRootPath,
    string InboxRootName,
    int ArchiveRootId,
    string ArchiveRootPath,
    string ArchiveRootName,
    string? LastTargetPath)
{
    /// <summary>Die ganze Eingangs-Wurzel wird übernommen (dann ist „Eingang austragen“ möglich).</summary>
    public bool IsWholeInbox => string.Equals(
        Path.TrimEndingDirectorySeparator(SourceFolder),
        Path.TrimEndingDirectorySeparator(InboxRootPath),
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>Ergebnis der Ausführung im <see cref="IFileOperationService"/> (ein Batch).</summary>
public sealed record FolderAdoptionExecution(
    int BatchId,
    int Moved,
    int Failed,
    int Renamed,
    bool SourceRecycled,
    string? RecycleNote,
    IReadOnlyList<string> RemainingFiles,
    IReadOnlyList<FileOperationItemResult> Items)
{
    /// <summary>Papierkorb war gewünscht, der Quellordner blieb aber (Rest-Dateien oder Fehler der Shell).</summary>
    public bool RecycleFailed { get; init; }
}

/// <summary>Gesamtergebnis einer Übernahme (Batch + ggf. Eingang ausgetragen) mit Zusammenfassung für die Oberfläche.</summary>
public sealed record FolderAdoptionOutcome(
    FolderAdoptionExecution Execution,
    bool InboxUnregistered,
    string Summary)
{
    public bool HasProblems => Execution.Failed > 0 || Execution.RemainingFiles.Count > 0 || Execution.RecycleFailed;
}

namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// Ein Quellordner der Flatten-Vorschau (V2 <c>Get-FlattenPreview</c>): Dateien direkt in diesem Ordner,
/// seine Pfad-Ebenen relativ zur Quellwurzel und der deduplizierte Zielname (änderbar).
/// </summary>
public sealed class FlattenFolder
{
    public const string RootName = "_root";

    public FlattenFolder(string absolutePath, string relativePath, IReadOnlyList<string> levels, string suggestedName)
    {
        AbsolutePath = absolutePath;
        RelativePath = relativePath;
        Levels = levels;
        SuggestedName = suggestedName;
        TargetName = suggestedName;
        IsSelected = levels.Count > 0; // Wurzel-Dateien bleiben standardmäßig liegen
    }

    public string AbsolutePath { get; }

    /// <summary>Relativ zur Quellwurzel, „.“ für die Wurzel selbst.</summary>
    public string RelativePath { get; }

    public IReadOnlyList<string> Levels { get; }

    /// <summary>V2-Regel: Ebenen dedupliziert und mit dem Separator verbunden; Wurzel = „_root“.</summary>
    public string SuggestedName { get; }

    public string TargetName { get; set; }

    public bool IsSelected { get; set; }

    public List<SorterFileInfo> Files { get; } = new();

    public bool IsRoot => Levels.Count == 0;

    public int LevelCount => Levels.Count;

    public int FileCount => Files.Count;

    public int MovableCount => Files.Count(f => f.IsInCatalog);

    public int NotInCatalogCount => Files.Count(f => !f.IsInCatalog);

    public long TotalSize => Files.Sum(f => f.Size);

    public IReadOnlyList<string> PreviewFiles => Files.Take(5).Select(f => f.Name).ToList();
}

public sealed class FlattenAnalysis
{
    public required string SourceFolder { get; init; }
    public required int LibraryId { get; init; }

    /// <summary>Wurzel (Eingang oder Archiv), unter der der Quellordner liegt.</summary>
    public required SorterRoot SourceRoot { get; init; }

    /// <summary>Alle Wurzeln der Bibliothek zum Zeitpunkt der Analyse (Archiv zuerst) — erlaubte Ziele (ADR-0024).</summary>
    public required IReadOnlyList<SorterRoot> Roots { get; init; }

    public required string Separator { get; init; }

    public List<FlattenFolder> Folders { get; } = new();

    public List<SorterFileInfo> NotInCatalog { get; } = new();

    public List<string> Warnings { get; } = new();

    public int TotalFolders => Folders.Count;

    public int TotalFiles => Folders.Sum(f => f.FileCount);

    public long TotalSize => Folders.Sum(f => f.TotalSize);

    public int MaxDepth => Folders.Count == 0 ? 0 : Folders.Max(f => f.LevelCount);

    public IReadOnlyList<string> LevelNames => Folders
        .SelectMany(f => f.Levels)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public sealed record FlattenAnalyzeOptions
{
    public static FlattenAnalyzeOptions Default { get; } = new();

    /// <summary>Trennzeichen zwischen den Pfad-Ebenen im Zielnamen (V2-Standard „_“).</summary>
    public string Separator { get; init; } = "_";

    public bool IncludeNonMediaFiles { get; init; }
}

public sealed record FlattenPlanOptions
{
    public static FlattenPlanOptions Default { get; } = new();

    /// <summary>Zielwurzel; null = Quellordner. Muss unter einer Wurzel der Bibliothek liegen (ADR-0024).</summary>
    public string? TargetRoot { get; init; }

    /// <summary>Alle Dateien direkt in die Zielwurzel (ohne Ordner-Ebene).</summary>
    public bool DirectToTargetRoot { get; init; }

    /// <summary>Leere Quellordner (und leer gewordene Elternordner bis zur Quellwurzel) nach dem Lauf löschen.</summary>
    public bool DeleteEmptySourceFolders { get; init; }
}

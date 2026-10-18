namespace PhotoViewerV3.Services.Sorter;

// ---------------------------------------------------------------- Profile (V2-JSON-Format)

/// <summary>
/// Pattern der Stufe 1 (V2 <c>file-sorter-patterns.json</c>). Der Regex wird mit IgnoreCase auf den
/// Dateinamen ohne Endung angewendet; <see cref="GroupCapture"/> 0 = ganzer Treffer, n = Gruppe n.
/// Niedrigere <see cref="Priority"/> wird zuerst geprüft; der erste Treffer gewinnt.
/// </summary>
public sealed class SorterPattern
{
    public string Name { get; set; } = string.Empty;
    public string Regex { get; set; } = string.Empty;
    public int GroupCapture { get; set; } = 1;
    public int Priority { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public bool BuiltIn { get; set; }
    public string Description { get; set; } = string.Empty;

    public SorterPattern Clone() => (SorterPattern)MemberwiseClone();
}

/// <summary>
/// Sub-Level-Pattern (Stufe 2..N, V2 <c>file-sorter-sublevels.json</c>). Wird — wie in V2 — auf den
/// vollständigen Dateinamen inklusive Endung angewendet (das Standard-Pattern braucht den Punkt).
/// </summary>
public sealed class SorterSubLevel
{
    public string Name { get; set; } = string.Empty;
    public string Regex { get; set; } = string.Empty;
    public int GroupCapture { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    public SorterSubLevel Clone() => (SorterSubLevel)MemberwiseClone();
}

/// <summary>Validierungsfehler eines Profils (Name doppelt, Regex nicht kompilierbar, …).</summary>
public sealed class SorterProfileException : Exception
{
    public SorterProfileException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Der gewählte Ordner liegt in keiner Bibliothek — der Sortierer arbeitet nur im Katalog (ADR-0012).</summary>
public sealed class FolderNotInLibraryException : InvalidOperationException
{
    public FolderNotInLibraryException(string folder)
        : base($"Der Ordner „{folder}“ gehört zu keiner Bibliothek; bitte als Bibliothek anlegen und scannen.")
    {
        Folder = folder;
    }

    public string Folder { get; }
}

// ---------------------------------------------------------------- Analyse

/// <summary>Eine Datei im Quellordner; <see cref="FileId"/> null = nicht im Katalog (seit dem Scan hinzugekommen).</summary>
public sealed class SorterFileInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; init; }
    public int? FileId { get; init; }
    public bool IsMedia { get; init; } = true;

    public bool IsInCatalog => FileId.HasValue;
}

/// <summary>
/// Gruppe der Analyse (Stufe 1) bzw. Sub-Gruppe (Stufe 2..N). <see cref="TargetFolder"/> ist der vom
/// Benutzer änderbare Ordnername (Vorschlag = Schlüssel); <see cref="IsSelected"/> steuert, ob die
/// Gruppe in den Plan kommt.
/// </summary>
public sealed class SorterGroup
{
    public const string UnsortedKey = "_unsorted";
    public const string OtherKey = "_other";
    public const string UnsortedPatternName = "none";
    public const string ManualPatternName = "manual";
    public const string SplitPatternName = "manual_split";

    public SorterGroup(string key, string patternName, int level = 1)
    {
        Key = key;
        PatternName = patternName;
        Level = level;
        SuggestedFolder = SorterPaths.SanitizeFolderName(key);
        TargetFolder = SuggestedFolder;
    }

    /// <summary>Kleingeschriebener Treffer (bzw. „_unsorted“ / „_other“).</summary>
    public string Key { get; }

    public string PatternName { get; }

    /// <summary>1 = Stufe 1, 2..N = Sub-Level-Ebene.</summary>
    public int Level { get; }

    public string SuggestedFolder { get; }

    public string TargetFolder { get; set; }

    public bool IsSelected { get; set; } = true;

    public List<SorterFileInfo> Files { get; } = new();

    public List<SorterGroup> SubGroups { get; } = new();

    /// <summary>Zielordner existiert bereits im Quellordner und enthält Dateien.</summary>
    public bool FolderExists { get; set; }

    public int FolderFileCount { get; set; }

    public int FileCount => Files.Count;

    public int MovableCount => Files.Count(f => f.IsInCatalog);

    public int NotInCatalogCount => Files.Count(f => !f.IsInCatalog);

    public long TotalSize => Files.Sum(f => f.Size);

    public IReadOnlyList<string> PreviewFiles => Files.Take(5).Select(f => f.Name).ToList();

    public bool IsUnsorted => Level == 1 && string.Equals(Key, UnsortedKey, StringComparison.Ordinal);

    public bool HasSubGroups => SubGroups.Count > 0;
}

/// <summary>Ergebnis von <see cref="IFileSorterService.AnalyzeAsync"/> — wird von den In-Memory-Operationen verändert.</summary>
public sealed class SorterAnalysis
{
    public required string SourceFolder { get; init; }
    public required int LibraryId { get; init; }
    public required string LibraryRoot { get; init; }

    /// <summary>Verwendete (aktive, kompilierbare) Sub-Level — leer = einstufig.</summary>
    public IReadOnlyList<SorterSubLevel> SubLevels { get; init; } = Array.Empty<SorterSubLevel>();

    public List<SorterGroup> Groups { get; } = new();

    /// <summary>Dateien im Ordner ohne Katalogeintrag („nicht im Katalog, erst neu scannen“).</summary>
    public List<SorterFileInfo> NotInCatalog { get; } = new();

    /// <summary>Hinweise, z. B. übersprungene Patterns mit ungültigem Regex.</summary>
    public List<string> Warnings { get; } = new();

    public DateTime AnalyzedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsMultiLevel => SubLevels.Count > 0;

    public int TotalFiles => Groups.Sum(g => g.FileCount);

    public int MovableFiles => Groups.Sum(g => g.MovableCount);
}

public sealed record SorterAnalyzeOptions
{
    public static SorterAnalyzeOptions Default { get; } = new();

    /// <summary>Sub-Level-Patterns anwenden (Ergebnis als Baum).</summary>
    public bool MultiLevel { get; init; }

    /// <summary>Auch Dateien, deren Endung kein Medienformat ist (sie sind nie im Katalog und werden nur angezeigt).</summary>
    public bool IncludeNonMediaFiles { get; init; }

    /// <summary>null = Patterns aus dem Profil-Store.</summary>
    public IReadOnlyList<SorterPattern>? Patterns { get; init; }

    /// <summary>null = Sub-Level aus dem Profil-Store.</summary>
    public IReadOnlyList<SorterSubLevel>? SubLevels { get; init; }
}

// ---------------------------------------------------------------- Plan

public enum UnsortedHandling
{
    /// <summary>„_unsorted“ bleibt liegen (Standard).</summary>
    Skip = 0,

    /// <summary>„_unsorted“ wird in den Ordner „_unsorted“ (bzw. das Mapping) verschoben.</summary>
    MoveToUnsortedFolder = 1,
}

public enum SubLevelLayout
{
    /// <summary>Verschachtelte Ordner: Prefix\Variante\… (Standard).</summary>
    Nested = 0,

    /// <summary>Flache Ordner mit kombinierten Namen wie V2: Prefix-r1 (result_1 → r1, _other → other).</summary>
    Flat = 1,
}

public sealed record SorterPlanOptions
{
    public static SorterPlanOptions Default { get; } = new();

    /// <summary>Zielwurzel; null = Quellordner. Ein anderer Ordner = „Sort-to-Root“.</summary>
    public string? TargetRoot { get; init; }

    public UnsortedHandling Unsorted { get; init; } = UnsortedHandling.Skip;

    /// <summary>Quellordner nach dem Lauf löschen, wenn er wirklich leer ist (nur bei Zielwurzel außerhalb des Quellordners).</summary>
    public bool DeleteEmptySourceFolders { get; init; }

    public SubLevelLayout Layout { get; init; } = SubLevelLayout.Nested;

    public string FlatSeparator { get; init; } = "-";
}

public enum SorterPlanItemStatus
{
    /// <summary>Wird verschoben.</summary>
    Ready = 0,

    /// <summary>Zielpfad existiert bereits (auf Platte oder im Plan) — wird übersprungen.</summary>
    Conflict = 1,

    /// <summary>Datei ist nicht im Katalog — erst neu scannen.</summary>
    NotInCatalog = 2,

    /// <summary>Zielordnername ungültig (Path-Traversal, ungültige Zeichen).</summary>
    InvalidTarget = 3,

    /// <summary>Datei liegt bereits im Zielordner.</summary>
    AlreadyInTarget = 4,
}

/// <summary>Eine geplante Verschiebung. Beim Flatten kann <see cref="DestPath"/> einen Dedup-Namen tragen (→ <see cref="RenameTo"/>).</summary>
public sealed record SorterPlanItem(
    int? FileId,
    string FileName,
    string SourcePath,
    string TargetFolder,
    string DestPath,
    string GroupPath,
    SorterPlanItemStatus Status,
    string? Note)
{
    public bool IsReady => Status == SorterPlanItemStatus.Ready;

    /// <summary>Neuer Dateiname, falls die Datei vor dem Verschieben umbenannt werden muss (Flatten-Dedup).</summary>
    public string? RenameTo
    {
        get
        {
            var destName = Path.GetFileName(DestPath);
            return string.Equals(destName, FileName, StringComparison.Ordinal) ? null : destName;
        }
    }

    public static string StatusText(SorterPlanItemStatus status) => status switch
    {
        SorterPlanItemStatus.Ready => "bereit",
        SorterPlanItemStatus.Conflict => "Konflikt: Ziel existiert bereits",
        SorterPlanItemStatus.NotInCatalog => "nicht im Katalog, erst neu scannen",
        SorterPlanItemStatus.InvalidTarget => "ungültiger Zielordner",
        SorterPlanItemStatus.AlreadyInTarget => "liegt bereits im Zielordner",
        _ => status.ToString(),
    };
}

public sealed class SorterPlan
{
    public const string KindSort = "sort";
    public const string KindFlatten = "flatten";

    public required string Kind { get; init; }
    public required string SourceFolder { get; init; }
    public required string TargetRoot { get; init; }
    public required int LibraryId { get; init; }
    public required string LibraryRoot { get; init; }
    public required IReadOnlyList<SorterPlanItem> Items { get; init; }

    /// <summary>Zielwurzel liegt in der Library-Root — sonst verlassen die Dateien den Katalog.</summary>
    public bool TargetInsideLibrary { get; init; }

    public bool DeleteEmptySourceFolders { get; init; }

    /// <summary>Quellordner, die nach dem Lauf gelöscht werden sollen, wenn sie wirklich leer sind.</summary>
    public IReadOnlyList<string> SourceFoldersToClean { get; init; } = Array.Empty<string>();

    /// <summary>Beim Aufräumen werden leer gewordene Elternordner nur unterhalb dieses Ordners (exklusiv) gelöscht.</summary>
    public string? CleanupBoundary { get; init; }

    /// <summary>Dateien, die gar nicht im Plan sind (abgewählte Gruppen, „_unsorted“ übersprungen).</summary>
    public int SkippedCount { get; init; }

    public int ReadyCount => Items.Count(i => i.IsReady);

    public int ConflictCount => Items.Count(i => i.Status == SorterPlanItemStatus.Conflict);

    public int NotInCatalogCount => Items.Count(i => i.Status == SorterPlanItemStatus.NotInCatalog);

    public int InvalidCount => Items.Count(i => i.Status == SorterPlanItemStatus.InvalidTarget);

    public int AlreadyInTargetCount => Items.Count(i => i.Status == SorterPlanItemStatus.AlreadyInTarget);

    public IReadOnlyList<string> TargetFolders => Items
        .Where(i => i.IsReady)
        .Select(i => i.TargetFolder)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

// ---------------------------------------------------------------- Ausführung / Undo

public sealed record SorterProgress(int Completed, int Total, string Message);

public sealed record SorterExecutionDetail(string FileName, string SourcePath, string? DestPath, bool Success, string? Message);

public sealed class SorterExecutionResult
{
    public List<int> BatchIds { get; } = new();
    public int Moved { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public List<string> CreatedFolders { get; } = new();
    public List<string> DeletedFolders { get; } = new();
    public List<SorterExecutionDetail> Details { get; } = new();

    public bool UndoAvailable => BatchIds.Count > 0;

    public IReadOnlyList<SorterExecutionDetail> Failures => Details.Where(d => !d.Success).ToList();
}

/// <summary>Protokoll des letzten Laufs (Settings „sorter.lastRun“ / „sorter.lastFlattenRun“).</summary>
public sealed class SorterRunRecord
{
    public string Kind { get; set; } = SorterPlan.KindSort;
    public int LibraryId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string SourceFolder { get; set; } = string.Empty;
    public string TargetRoot { get; set; } = string.Empty;
    public List<int> BatchIds { get; set; } = new();
    public List<string> CreatedFolders { get; set; } = new();
    public List<string> DeletedFolders { get; set; } = new();
    public int Moved { get; set; }
    public bool Undone { get; set; }
}

public sealed record SorterUndoResult(
    int Restored,
    int Failed,
    IReadOnlyList<string> RemovedFolders,
    IReadOnlyList<SorterExecutionDetail> Details);

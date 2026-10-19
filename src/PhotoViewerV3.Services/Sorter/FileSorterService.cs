using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Media;

namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// Standard-Implementierung von <see cref="IFileSorterService"/>. Die Analyse liest den Ordner
/// (nur direkte Dateien, Medienformate aus dem Register), löst FileIds über den Katalog auf und
/// gruppiert mit der <see cref="SorterPatternEngine"/>; Multi-Level teilt jede Gruppe rekursiv weiter.
/// Die Ausführung läuft über <see cref="SorterMoveExecutor"/> (je Zielordner ein Move-Batch).
/// </summary>
public sealed class FileSorterService : IFileSorterService
{
    private readonly ISorterProfileStore _store;
    private readonly SorterCatalog _catalog;
    private readonly SorterMoveExecutor _executor;
    private readonly IMediaFormatRegistry _formats;
    private readonly ILogger<FileSorterService> _logger;

    public FileSorterService(
        ISorterProfileStore store,
        SorterCatalog catalog,
        SorterMoveExecutor executor,
        IMediaFormatRegistry formats,
        ILogger<FileSorterService> logger)
    {
        _store = store;
        _catalog = catalog;
        _executor = executor;
        _formats = formats;
        _logger = logger;
    }

    // ------------------------------------------------------------------ Analyse

    public async Task<SorterAnalysis> AnalyzeAsync(string folderPath, SorterAnalyzeOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        options ??= SorterAnalyzeOptions.Default;

        var folder = SorterPaths.NormalizeDirectory(folderPath);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Der Ordner „{folder}“ wurde nicht gefunden.");
        }

        var location = await _catalog.RequireLocationAsync(folder, ct).ConfigureAwait(false);
        var patterns = options.Patterns ?? await _store.GetPatternsAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SorterSubLevel> subLevels = options.MultiLevel
            ? options.SubLevels ?? await _store.GetSubLevelsAsync(ct).ConfigureAwait(false)
            : Array.Empty<SorterSubLevel>();

        var catalogFiles = await _catalog.GetFilesInFolderAsync(location.LibraryId, folder, ct).ConfigureAwait(false);
        var files = await Task.Run(() => EnumerateFiles(folder, catalogFiles, options.IncludeNonMediaFiles), ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var compiled = SorterPatternEngine.CompilePatterns(patterns, warnings);
        var compiledLevels = SorterPatternEngine.CompileSubLevels(subLevels, warnings);
        var usedLevels = subLevels
            .Where(l => l.Enabled && SorterPatternEngine.TryCompile(l.Regex, out _, out _))
            .Select(l => l.Clone())
            .ToList();

        var groups = SorterPatternEngine.GroupFiles(files, compiled, warnings);
        foreach (var group in groups)
        {
            SorterPatternEngine.SplitIntoSubGroups(group, compiledLevels);
        }

        var analysis = new SorterAnalysis
        {
            SourceFolder = folder,
            LibraryId = location.LibraryId,
            SourceRoot = location.Root,
            Roots = location.Roots,
            SubLevels = usedLevels,
            TargetRoot = NormalizeTargetRoot(options.TargetRoot),
        };
        analysis.Groups.AddRange(groups);
        analysis.NotInCatalog.AddRange(files.Where(f => !f.IsInCatalog));
        analysis.Warnings.AddRange(warnings);
        foreach (var group in analysis.Groups)
        {
            RefreshFolderInfo(analysis, group);
        }

        _logger.LogInformation(
            "Sortierer-Analyse {Folder} ({Role} „{Root}“): {Files} Dateien, {Groups} Gruppen, {Unknown} nicht im Katalog, MultiLevel={Multi}",
            folder, location.Root.RoleText, location.Root.Name, files.Count, groups.Count, analysis.NotInCatalog.Count, analysis.IsMultiLevel);
        return analysis;
    }

    private List<SorterFileInfo> EnumerateFiles(string folder, IReadOnlyDictionary<string, CatalogFileEntry> catalogFiles, bool includeNonMedia)
    {
        var list = new List<SorterFileInfo>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            var isMedia = _formats.IsMedia(Path.GetExtension(name)); // Archive (0.2.8) zählen nicht als Mediendatei
            if (!isMedia && !includeNonMedia)
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = 0;
            }

            int? fileId = isMedia && catalogFiles.TryGetValue(name, out var entry) ? entry.Id : null;
            list.Add(new SorterFileInfo
            {
                Name = name,
                FullPath = path,
                Size = size,
                FileId = fileId,
                IsMedia = isMedia,
            });
        }

        return list;
    }

    private static void RefreshFolderInfo(SorterAnalysis analysis, SorterGroup group)
    {
        group.FolderExists = false;
        group.FolderFileCount = 0;
        if (!SorterPaths.IsValidFolderName(group.TargetFolder, out _))
        {
            return;
        }

        var path = Path.Combine(analysis.TargetRoot ?? analysis.SourceFolder, group.TargetFolder);
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            group.FolderFileCount = Directory.EnumerateFiles(path).Count();
            group.FolderExists = group.FolderFileCount > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            group.FolderExists = true;
        }
    }

    private static string? NormalizeTargetRoot(string? targetRoot)
        => string.IsNullOrWhiteSpace(targetRoot) ? null : SorterPaths.NormalizeDirectory(targetRoot);

    // ------------------------------------------------------------------ In-Memory-Operationen

    public void SetTargetRoot(SorterAnalysis analysis, string? targetRoot)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        analysis.TargetRoot = NormalizeTargetRoot(targetRoot);
        foreach (var group in analysis.Groups)
        {
            RefreshFolderInfo(analysis, group);
        }
    }

    public void MoveFile(SorterAnalysis analysis, string fileName, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var key = NormalizeKey(targetKey);

        var source = FindGroupWithFile(analysis, fileName)
                     ?? throw new ArgumentException($"Datei „{fileName}“ ist in keiner Gruppe.", nameof(fileName));
        var file = source.Files.First(f => NameEquals(f.Name, fileName));
        var target = FindGroup(analysis, key) ?? CreateGroup(analysis, key, SorterGroup.ManualPatternName);
        if (ReferenceEquals(source, target))
        {
            return;
        }

        source.Files.Remove(file);
        target.Files.Add(file);
        AfterChange(analysis, source, target);
        _logger.LogDebug("Sortierer: „{File}“ {Source} → {Target}", fileName, source.Key, target.Key);
    }

    public void MergeGroups(SorterAnalysis analysis, string sourceKey, string targetKey)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var sKey = NormalizeKey(sourceKey);
        var tKey = NormalizeKey(targetKey);
        if (string.Equals(sKey, tKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Quelle und Ziel sind identisch.", nameof(targetKey));
        }

        var source = FindGroup(analysis, sKey) ?? throw new ArgumentException($"Quell-Gruppe „{sKey}“ nicht gefunden.", nameof(sourceKey));
        var target = FindGroup(analysis, tKey) ?? throw new ArgumentException($"Ziel-Gruppe „{tKey}“ nicht gefunden.", nameof(targetKey));

        target.Files.AddRange(source.Files);
        source.Files.Clear();
        AfterChange(analysis, source, target);
        _logger.LogDebug("Sortierer: Gruppe {Source} in {Target} zusammengeführt", sKey, tKey);
    }

    public void SplitGroup(SorterAnalysis analysis, string sourceKey, IReadOnlyCollection<string> fileNames, string newKey)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(fileNames);
        var sKey = NormalizeKey(sourceKey);
        var nKey = NormalizeKey(newKey);

        var source = FindGroup(analysis, sKey) ?? throw new ArgumentException($"Gruppe „{sKey}“ nicht gefunden.", nameof(sourceKey));
        if (FindGroup(analysis, nKey) is not null)
        {
            throw new ArgumentException($"Gruppe „{nKey}“ existiert bereits.", nameof(newKey));
        }

        var missing = fileNames.Where(n => !source.Files.Any(f => NameEquals(f.Name, n))).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Dateien nicht in „{sKey}“: {string.Join(", ", missing)}", nameof(fileNames));
        }

        var moving = source.Files.Where(f => fileNames.Any(n => NameEquals(f.Name, n))).ToList();
        if (moving.Count == 0)
        {
            return;
        }

        var target = CreateGroup(analysis, nKey, SorterGroup.SplitPatternName);
        foreach (var file in moving)
        {
            source.Files.Remove(file);
            target.Files.Add(file);
        }

        AfterChange(analysis, source, target);
        _logger.LogDebug("Sortierer: {Count} Datei(en) von {Source} nach {Target} abgespalten", moving.Count, sKey, nKey);
    }

    private void AfterChange(SorterAnalysis analysis, params SorterGroup[] affected)
    {
        var levels = SorterPatternEngine.CompileSubLevels(analysis.SubLevels);
        foreach (var group in affected)
        {
            if (group.Files.Count == 0)
            {
                analysis.Groups.Remove(group);
                continue;
            }

            SorterPatternEngine.SortFiles(group.Files);
            SorterPatternEngine.SplitIntoSubGroups(group, levels);
            RefreshFolderInfo(analysis, group);
        }

        SorterPatternEngine.SortGroups(analysis.Groups);
    }

    private static SorterGroup CreateGroup(SorterAnalysis analysis, string key, string patternName)
    {
        var group = new SorterGroup(key, patternName);
        analysis.Groups.Add(group);
        return group;
    }

    private static SorterGroup? FindGroup(SorterAnalysis analysis, string key)
        => analysis.Groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.Ordinal));

    private static SorterGroup? FindGroupWithFile(SorterAnalysis analysis, string fileName)
        => analysis.Groups.FirstOrDefault(g => g.Files.Any(f => NameEquals(f.Name, fileName)));

    private static string NormalizeKey(string? key)
    {
        var k = key?.Trim();
        if (string.IsNullOrEmpty(k))
        {
            throw new ArgumentException("Der Gruppenschlüssel darf nicht leer sein.", nameof(key));
        }

        return k.ToLowerInvariant();
    }

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Plan

    public SorterPlan BuildPlan(SorterAnalysis analysis, IReadOnlyDictionary<string, string> mappings, SorterPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(mappings);
        options ??= SorterPlanOptions.Default;

        var targetRoot = string.IsNullOrWhiteSpace(options.TargetRoot)
            ? analysis.SourceFolder
            : SorterPaths.NormalizeDirectory(options.TargetRoot);
        var targetError = SorterTargetCheck.Check(analysis.Roots, targetRoot, out var targetLibraryRoot);

        var ctx = new PlanContext(targetRoot, options, targetError);
        foreach (var group in analysis.Groups)
        {
            if (!group.IsSelected)
            {
                ctx.Skipped += group.FileCount;
                continue;
            }

            string? folderName;
            if (group.IsUnsorted)
            {
                if (options.Unsorted == UnsortedHandling.Skip)
                {
                    ctx.Skipped += group.FileCount;
                    continue;
                }

                folderName = mappings.TryGetValue(group.Key, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                    ? mapped
                    : SorterGroup.UnsortedKey;
            }
            else if (!mappings.TryGetValue(group.Key, out folderName) || string.IsNullOrWhiteSpace(folderName))
            {
                ctx.Skipped += group.FileCount;
                continue;
            }

            AddGroupItems(group, [folderName.Trim()], group.Key, ctx);
        }

        var targetInsideSource = SorterPaths.IsInsideRoot(targetRoot, analysis.SourceFolder);
        var cleanup = options.DeleteEmptySourceFolders
                      && targetError is null
                      && !targetInsideSource
                      && !SorterTargetCheck.IsRootFolder(analysis.Roots, analysis.SourceFolder);

        return new SorterPlan
        {
            Kind = SorterPlan.KindSort,
            SourceFolder = analysis.SourceFolder,
            TargetRoot = targetRoot,
            LibraryId = analysis.LibraryId,
            SourceRoot = analysis.SourceRoot,
            Roots = analysis.Roots,
            TargetLibraryRoot = targetLibraryRoot,
            TargetError = targetError,
            DeleteEmptySourceFolders = cleanup,
            SourceFoldersToClean = cleanup ? [analysis.SourceFolder] : [],
            CleanupBoundary = cleanup ? Path.GetDirectoryName(analysis.SourceFolder) : null,
            Items = ctx.Items,
            SkippedCount = ctx.Skipped,
        };
    }

    private sealed class PlanContext(string targetRoot, SorterPlanOptions options, string? targetError)
    {
        public string TargetRoot { get; } = targetRoot;
        public SorterPlanOptions Options { get; } = options;

        /// <summary>Ziel gesperrt (ADR-0024) — alle Einträge werden „ungültiges Ziel“.</summary>
        public string? TargetError { get; } = targetError;
        public List<SorterPlanItem> Items { get; } = new();
        public HashSet<string> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Skipped { get; set; }
    }

    private static void AddGroupItems(SorterGroup group, List<string> chain, string groupPath, PlanContext ctx)
    {
        if (group.HasSubGroups)
        {
            foreach (var sub in group.SubGroups)
            {
                if (!sub.IsSelected)
                {
                    ctx.Skipped += sub.FileCount;
                    continue;
                }

                var subName = sub.TargetFolder.Trim();
                var subChain = ctx.Options.Layout == SubLevelLayout.Nested
                    ? [.. chain, subName]
                    : new List<string>(chain[..^1]) { chain[^1] + ctx.Options.FlatSeparator + SorterPaths.ToShortSubKey(subName) };
                AddGroupItems(sub, subChain, groupPath + "/" + sub.Key, ctx);
            }

            return;
        }

        string? error = null;
        foreach (var segment in chain)
        {
            if (!SorterPaths.IsValidFolderName(segment, out error))
            {
                break;
            }
        }

        var targetFolder = error is null ? Path.Combine([ctx.TargetRoot, .. chain]) : ctx.TargetRoot;
        foreach (var file in group.Files)
        {
            var dest = Path.Combine(targetFolder, file.Name);
            SorterPlanItemStatus status;
            string? note = null;

            if (ctx.TargetError is not null)
            {
                status = SorterPlanItemStatus.InvalidTarget;
                note = ctx.TargetError;
            }
            else if (error is not null)
            {
                status = SorterPlanItemStatus.InvalidTarget;
                note = error;
            }
            else if (!file.IsInCatalog)
            {
                status = SorterPlanItemStatus.NotInCatalog;
            }
            else if (SorterPaths.PathEquals(SorterPaths.NormalizeDirectory(Path.GetDirectoryName(file.FullPath)!), targetFolder))
            {
                status = SorterPlanItemStatus.AlreadyInTarget;
            }
            else if (File.Exists(dest) || ctx.Claimed.Contains(dest))
            {
                status = SorterPlanItemStatus.Conflict;
                note = "Ziel existiert bereits";
            }
            else
            {
                status = SorterPlanItemStatus.Ready;
                ctx.Claimed.Add(dest);
            }

            ctx.Items.Add(new SorterPlanItem(file.FileId, file.Name, file.FullPath, targetFolder, dest, groupPath, status, note));
        }
    }

    // ------------------------------------------------------------------ Ausführung / Undo

    public Task<SorterExecutionResult> ExecuteAsync(SorterPlan plan, IProgress<SorterProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind != SorterPlan.KindSort)
        {
            throw new ArgumentException("Der Plan stammt nicht vom Sortierer.", nameof(plan));
        }

        return _executor.ExecuteAsync(plan, SorterRunLog.SortKey, progress, ct);
    }

    public Task<SorterRunRecord?> GetLastRunAsync(CancellationToken ct = default)
        => _executor.GetLastRunAsync(SorterRunLog.SortKey, ct);

    public Task<SorterUndoResult?> UndoLastRunAsync(IProgress<SorterProgress>? progress = null, CancellationToken ct = default)
        => _executor.UndoLastRunAsync(SorterRunLog.SortKey, progress, ct);
}

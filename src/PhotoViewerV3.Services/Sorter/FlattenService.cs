using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Media;

namespace PhotoViewerV3.Services.Sorter;

/// <summary>
/// Standard-Implementierung von <see cref="IFlattenService"/>. Die Vorschau folgt exakt V2
/// <c>Get-FlattenPreview</c>: jeder Ordner mit Mediendateien (Wurzel eingeschlossen) wird ein Eintrag,
/// Zielname = <see cref="BuildDeduplicatedName"/> der Pfad-Ebenen (Wurzel „_root“); ignoriert werden
/// „.thumbs“, „.cache“, „.temp“, „.converted“. Namenskonflikte im Ziel werden wie V2 mit „_1“, „_2“, …
/// aufgelöst — hier als protokolliertes Rename vor dem Move.
/// </summary>
public sealed class FlattenService : IFlattenService
{
    private static readonly HashSet<string> s_ignoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".thumbs", ".cache", ".temp", ".converted",
    };

    private readonly SorterCatalog _catalog;
    private readonly SorterMoveExecutor _executor;
    private readonly IMediaFormatRegistry _formats;
    private readonly ILogger<FlattenService> _logger;

    public FlattenService(
        SorterCatalog catalog,
        SorterMoveExecutor executor,
        IMediaFormatRegistry formats,
        ILogger<FlattenService> logger)
    {
        _catalog = catalog;
        _executor = executor;
        _formats = formats;
        _logger = logger;
    }

    /// <summary>
    /// V2 <c>Get-DeduplicatedName</c>: Ebenen in Reihenfolge, Duplikate (case-insensitiv) entfernt —
    /// das erste Vorkommen bleibt — und mit dem Separator verbunden. Leer → "".
    /// </summary>
    public static string BuildDeduplicatedName(IReadOnlyList<string> levels, string separator)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            return string.Empty;
        }

        if (levels.Count == 1)
        {
            return levels[0];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();
        foreach (var level in levels)
        {
            if (seen.Add(level))
            {
                unique.Add(level);
            }
        }

        return string.Join(separator, unique);
    }

    // ------------------------------------------------------------------ Analyse

    public async Task<FlattenAnalysis> AnalyzeAsync(string sourceFolder, FlattenAnalyzeOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        options ??= FlattenAnalyzeOptions.Default;

        var source = SorterPaths.NormalizeDirectory(sourceFolder);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Der Ordner „{source}“ wurde nicht gefunden.");
        }

        var library = await _catalog.RequireLibraryForFolderAsync(source, ct).ConfigureAwait(false);
        var separator = string.IsNullOrEmpty(options.Separator) ? "_" : options.Separator;
        var catalog = await _catalog.GetFilesUnderFolderAsync(library.Id, source, ct).ConfigureAwait(false);

        var analysis = new FlattenAnalysis
        {
            SourceFolder = source,
            LibraryId = library.Id,
            LibraryRoot = SorterPaths.NormalizeDirectory(library.RootPath),
            Separator = separator,
        };

        await Task.Run(() => ScanFolders(analysis, catalog, options.IncludeNonMediaFiles, ct), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Flatten-Analyse {Folder}: {Folders} Ordner, {Files} Dateien, {Unknown} nicht im Katalog",
            source, analysis.TotalFolders, analysis.TotalFiles, analysis.NotInCatalog.Count);
        return analysis;
    }

    private void ScanFolders(
        FlattenAnalysis analysis,
        IReadOnlyDictionary<string, Dictionary<string, CatalogFileEntry>> catalog,
        bool includeNonMedia,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(analysis.SourceFolder);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                analysis.Warnings.Add($"Ordner nicht lesbar: {dir} ({ex.Message})");
                subDirs = Array.Empty<string>();
            }

            foreach (var sub in subDirs)
            {
                if (!s_ignoredDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            var normalized = SorterPaths.NormalizeDirectory(dir);
            var files = EnumerateFiles(normalized, catalog.GetValueOrDefault(normalized), includeNonMedia, analysis.Warnings);
            if (files.Count == 0)
            {
                continue;
            }

            var relative = SorterPaths.RelativeTo(normalized, analysis.SourceFolder);
            var levels = relative == "."
                ? Array.Empty<string>()
                : relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var suggested = levels.Length == 0
                ? FlattenFolder.RootName
                : BuildDeduplicatedName(levels, analysis.Separator);

            var folder = new FlattenFolder(normalized, relative, levels, suggested);
            folder.Files.AddRange(files);
            analysis.Folders.Add(folder);
            analysis.NotInCatalog.AddRange(files.Where(f => !f.IsInCatalog));
        }

        analysis.Folders.Sort((a, b) =>
        {
            if (a.IsRoot != b.IsRoot)
            {
                return a.IsRoot ? -1 : 1;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        });
    }

    private List<SorterFileInfo> EnumerateFiles(
        string folder,
        IReadOnlyDictionary<string, CatalogFileEntry>? catalogFiles,
        bool includeNonMedia,
        List<string> warnings)
    {
        var list = new List<SorterFileInfo>();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Ordner nicht lesbar: {folder} ({ex.Message})");
            return list;
        }

        foreach (var path in paths)
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

            int? fileId = isMedia && catalogFiles is not null && catalogFiles.TryGetValue(name, out var entry) ? entry.Id : null;
            list.Add(new SorterFileInfo
            {
                Name = name,
                FullPath = path,
                Size = size,
                FileId = fileId,
                IsMedia = isMedia,
            });
        }

        SorterPatternEngine.SortFiles(list);
        return list;
    }

    // ------------------------------------------------------------------ Plan

    public SorterPlan BuildPlan(FlattenAnalysis analysis, FlattenPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        options ??= FlattenPlanOptions.Default;

        var targetRoot = string.IsNullOrWhiteSpace(options.TargetRoot)
            ? analysis.SourceFolder
            : SorterPaths.NormalizeDirectory(options.TargetRoot);

        var items = new List<SorterPlanItem>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var cleanup = new List<string>();

        foreach (var folder in analysis.Folders)
        {
            if (!folder.IsSelected)
            {
                skipped += folder.FileCount;
                continue;
            }

            string targetFolder;
            string? error = null;
            if (options.DirectToTargetRoot)
            {
                targetFolder = targetRoot;
            }
            else
            {
                var name = folder.TargetName.Trim();
                targetFolder = SorterPaths.IsValidFolderName(name, out error) ? Path.Combine(targetRoot, name) : targetRoot;
            }

            foreach (var file in folder.Files)
            {
                var dest = Path.Combine(targetFolder, file.Name);
                SorterPlanItemStatus status;
                string? note = null;

                if (error is not null)
                {
                    status = SorterPlanItemStatus.InvalidTarget;
                    note = error;
                }
                else if (!file.IsInCatalog)
                {
                    status = SorterPlanItemStatus.NotInCatalog;
                }
                else if (SorterPaths.PathEquals(folder.AbsolutePath, targetFolder))
                {
                    status = SorterPlanItemStatus.AlreadyInTarget;
                }
                else
                {
                    if (File.Exists(dest) || claimed.Contains(dest))
                    {
                        var candidate = NextFreeName(folder.AbsolutePath, targetFolder, file.Name, claimed, out dest);
                        note = $"Name bereits vergeben – wird als „{candidate}“ abgelegt";
                        claimed.Add(Path.Combine(folder.AbsolutePath, candidate));
                    }

                    status = SorterPlanItemStatus.Ready;
                    claimed.Add(dest);
                }

                items.Add(new SorterPlanItem(file.FileId, file.Name, file.FullPath, targetFolder, dest, folder.RelativePath, status, note));
            }

            if (options.DeleteEmptySourceFolders
                && !folder.IsRoot
                && !SorterPaths.PathEquals(folder.AbsolutePath, targetRoot)
                && !SorterPaths.PathEquals(folder.AbsolutePath, analysis.LibraryRoot))
            {
                cleanup.Add(folder.AbsolutePath);
            }
        }

        return new SorterPlan
        {
            Kind = SorterPlan.KindFlatten,
            SourceFolder = analysis.SourceFolder,
            TargetRoot = targetRoot,
            LibraryId = analysis.LibraryId,
            LibraryRoot = analysis.LibraryRoot,
            TargetInsideLibrary = SorterPaths.IsInsideRoot(targetRoot, analysis.LibraryRoot),
            DeleteEmptySourceFolders = options.DeleteEmptySourceFolders && cleanup.Count > 0,
            SourceFoldersToClean = cleanup,
            CleanupBoundary = analysis.SourceFolder,
            Items = items,
            SkippedCount = skipped,
        };
    }

    /// <summary>V2: „{base}_{n}{ext}“ mit n ab 1 — frei im Ziel, im Plan und (für das Rename) im Quellordner.</summary>
    private static string NextFreeName(string sourceFolder, string targetFolder, string fileName, HashSet<string> claimed, out string dest)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 1;
        string candidate;
        do
        {
            candidate = $"{baseName}_{counter}{ext}";
            counter++;
            dest = Path.Combine(targetFolder, candidate);
        }
        while (File.Exists(dest)
               || claimed.Contains(dest)
               || File.Exists(Path.Combine(sourceFolder, candidate))
               || claimed.Contains(Path.Combine(sourceFolder, candidate)));

        return candidate;
    }

    // ------------------------------------------------------------------ Ausführung / Undo

    public Task<SorterExecutionResult> ExecuteAsync(SorterPlan plan, IProgress<SorterProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind != SorterPlan.KindFlatten)
        {
            throw new ArgumentException("Der Plan stammt nicht von Flatten & Move.", nameof(plan));
        }

        return _executor.ExecuteAsync(plan, SorterRunLog.FlattenKey, progress, ct);
    }

    public Task<SorterRunRecord?> GetLastRunAsync(CancellationToken ct = default)
        => _executor.GetLastRunAsync(SorterRunLog.FlattenKey, ct);

    public Task<SorterUndoResult?> UndoLastRunAsync(IProgress<SorterProgress>? progress = null, CancellationToken ct = default)
        => _executor.UndoLastRunAsync(SorterRunLog.FlattenKey, progress, ct);
}

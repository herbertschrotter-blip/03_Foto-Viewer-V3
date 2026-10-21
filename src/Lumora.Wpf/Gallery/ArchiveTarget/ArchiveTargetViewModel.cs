using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Operations;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>
/// ViewModel des Dialogs „Ins Archiv…“ (ADR-0020): Ordnerbaum der Archiv-Wurzel aus Katalog-Ordnern (Folders mit
/// RootId der Archiv-Wurzel) plus Dateisystem-Ordnern ohne Katalogzeile, Suchfeld (flache Trefferliste über alle
/// bekannten Ordner), „Neuer Ordner“ und „Umbenennen“ über <see cref="IFileOperationService"/> (protokolliert),
/// zuletzt benutzter Zielordner vorgemerkt, Vorschlag „Jahr/Monat“ aus dem Aufnahme-/Dateidatum.
/// Der Zielordner darf noch nicht existieren (Vorschlag) — <c>MoveAsync</c> legt ihn an.
/// </summary>
public sealed partial class ArchiveTargetViewModel : ObservableObject
{
    private readonly ArchiveTargetRequest _request;
    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly ILogger _logger;

    private sealed record CatalogFolder(int Id, string Path, string Name);

    /// <summary>Katalog-Ordner der Archiv-Wurzel nach normalisiertem Pfad.</summary>
    private readonly Dictionary<string, CatalogFolder> _catalogByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Katalog-Kinder je normalisiertem Elternpfad (über den Pfad, nicht ParentId — Ketten können Lücken haben).</summary>
    private readonly Dictionary<string, List<CatalogFolder>> _catalogChildren = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alle bisher bekannten Ordnerpfade (Katalog + im Dateisystem entdeckte) für die Suche.</summary>
    private readonly Dictionary<string, string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);

    private ArchiveFolderNode? _rootNode;
    private bool _suppressSelection;

    public ArchiveTargetViewModel(
        ArchiveTargetRequest request,
        ILibraryContextFactory factory,
        IFileOperationService fileOps,
        ILogger? logger = null)
    {
        _request = request;
        _factory = factory;
        _fileOps = fileOps;
        _logger = logger ?? NullLogger.Instance;
        ArchiveRootPath = NormalizePath(request.ArchiveRootPath);
        SuggestedRelativePath = request.SuggestedRelativePath;
    }

    /// <summary>Name abfragen (Titel, Text, Vorbelegung) → Eingabe oder null; vom Dialogfenster gesetzt.</summary>
    public Func<string, string, string, Task<string?>>? PromptName { get; set; }

    public string ArchiveRootPath { get; }

    public int FileCount => _request.FileIds.Count;

    public string Title => FileCount == 1 ? "1 Datei in die Bibliothek verschieben" : $"{FileCount:N0} Dateien in die Bibliothek verschieben";

    public ObservableCollection<ArchiveFolderNode> Roots { get; } = new();

    public ObservableCollection<ArchiveFolderNode> SearchResults { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(ShowTree))]
    private string? _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(TargetDisplay), nameof(CanRename))]
    [NotifyCanExecuteChangedFor(nameof(CreateFolderCommand), nameof(RenameFolderCommand))]
    private ArchiveFolderNode? _selectedNode;

    /// <summary>Absoluter Zielordner — Auswahl im Baum oder übernommener Vorschlag (darf noch fehlen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(TargetDisplay))]
    private string? _targetPath;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public string? SuggestedRelativePath { get; }

    public bool HasSuggestion => !string.IsNullOrEmpty(SuggestedRelativePath);

    public string SuggestionText => HasSuggestion ? $"Vorschlag: {SuggestedRelativePath}" : "Kein Datumsvorschlag (Datei nicht lesbar)";

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public bool ShowTree => !IsSearching;

    public bool CanConfirm => !string.IsNullOrWhiteSpace(TargetPath) && IsUnderRoot(TargetPath);

    public bool CanRename => SelectedNode is { IsPlaceholder: false, IsRoot: false, FolderId: not null };

    public string TargetDisplay => string.IsNullOrWhiteSpace(TargetPath)
        ? "Kein Zielordner gewählt."
        : $"Ziel: {TargetPath}";

    // ---- Laden -------------------------------------------------------------------------------

    /// <summary>Katalog-Ordner der Archiv-Wurzel laden, Baum aufbauen, letzten Zielordner vorwählen.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            List<CatalogFolder> rows;
            await using (var ctx = _factory.Create())
            {
                var rootId = _request.ArchiveRootId;
                rows = await ctx.Folders.AsNoTracking()
                    .Where(f => f.RootId == rootId)
                    .Select(f => new CatalogFolder(f.Id, f.Path, f.Name))
                    .ToListAsync()
                    .ConfigureAwait(true);
            }

            _catalogByPath.Clear();
            _catalogChildren.Clear();
            _knownPaths.Clear();
            foreach (var row in rows)
            {
                var path = NormalizePath(row.Path);
                _catalogByPath[path] = row;
                _knownPaths[path] = path;
                var parent = Path.GetDirectoryName(path);
                if (parent is null || !IsUnderRoot(parent))
                {
                    continue;
                }

                if (!_catalogChildren.TryGetValue(parent, out var list))
                {
                    list = new List<CatalogFolder>();
                    _catalogChildren[parent] = list;
                }

                list.Add(row);
            }

            _catalogByPath.TryGetValue(ArchiveRootPath, out var rootRow);
            _rootNode = new ArchiveFolderNode(this, rootRow?.Id, _request.ArchiveRootName, ArchiveRootPath, hasChildren: true, isRoot: true);
            Roots.Clear();
            Roots.Add(_rootNode);
            _rootNode.IsExpanded = true;

            var preselect = _request.LastTargetPath is { } last && IsUnderRoot(NormalizePath(last)) ? NormalizePath(last) : null;
            if (preselect is not null && _rootNode.FindByPath(preselect) is { } node)
            {
                ExpandTo(node);
                node.IsSelected = true;
            }
            else
            {
                _rootNode.IsSelected = true;
            }

            _logger.LogInformation("Archiv-Dialog: {Folders} Katalog-Ordner unter {Root}, vorgewählt {Target}", rows.Count, ArchiveRootPath, TargetPath ?? "-");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnten nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Archiv-Dialog: Laden fehlgeschlagen");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Kinder eines Knotens: Katalog-Ordner ∪ Dateisystem-Ordner (nach Name dedupliziert, sortiert).</summary>
    internal IReadOnlyList<ArchiveFolderNode> CreateChildren(ArchiveFolderNode parent)
    {
        var parentPath = NormalizePath(parent.Path);
        var byName = new Dictionary<string, ArchiveFolderNode>(StringComparer.OrdinalIgnoreCase);

        if (_catalogChildren.TryGetValue(parentPath, out var catalogRows))
        {
            foreach (var row in catalogRows)
            {
                var path = NormalizePath(row.Path);
                byName[row.Name] = new ArchiveFolderNode(this, row.Id, row.Name, path, hasChildren: HasAnyChild(path));
            }
        }

        try
        {
            if (Directory.Exists(parentPath))
            {
                foreach (var dir in Directory.EnumerateDirectories(parentPath))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || byName.ContainsKey(name) || Lumora.Services.Infrastructure.CatalogFolders.IsCatalogFolder(dir))
                    {
                        continue;
                    }

                    var path = NormalizePath(dir);
                    _knownPaths[path] = path;
                    byName[name] = new ArchiveFolderNode(this, null, name, path, hasChildren: HasAnyChild(path));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Archiv-Dialog: Unterordner von {Path} nicht lesbar", parentPath);
        }

        return byName.Values.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private bool HasAnyChild(string path)
    {
        if (_catalogChildren.ContainsKey(path))
        {
            return true;
        }

        try
        {
            return Directory.Exists(path) && Directory.EnumerateDirectories(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal void OnNodeSelected(ArchiveFolderNode node)
    {
        if (node.IsPlaceholder || _suppressSelection)
        {
            return;
        }

        SelectedNode = node;
        TargetPath = node.Path;
    }

    // ---- Suche -------------------------------------------------------------------------------

    partial void OnSearchTextChanged(string? value)
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var term = value.Trim();
        var hits = _knownPaths.Keys
            .Where(p => !string.Equals(p, ArchiveRootPath, StringComparison.OrdinalIgnoreCase)
                        && p[Math.Min(p.Length, ArchiveRootPath.Length)..].Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .Take(200);
        foreach (var path in hits)
        {
            var id = _catalogByPath.TryGetValue(path, out var row) ? row.Id : (int?)null;
            SearchResults.Add(new ArchiveFolderNode(this, id, RelativeTo(path), path, hasChildren: false));
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    // ---- Vorschlag / Aktionen -----------------------------------------------------------------

    [RelayCommand]
    private void UseSuggestion()
    {
        if (!HasSuggestion)
        {
            return;
        }

        var target = Path.Combine(ArchiveRootPath, SuggestedRelativePath!);
        if (_rootNode?.FindByPath(target) is { } node)
        {
            ExpandTo(node);
            node.IsSelected = true;
            return;
        }

        // Ordner existiert noch nicht: Auswahl im Baum aufheben, Ziel direkt setzen (MoveAsync legt ihn an).
        ClearTreeSelection();
        TargetPath = target;
    }

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private async Task CreateFolderAsync()
    {
        var parent = SelectedNode;
        if (parent is null || PromptName is null)
        {
            return;
        }

        var name = await PromptName("Neuer Ordner", $"Neuer Ordner in „{parent.Path}“:", "Neuer Ordner").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _fileOps.CreateFolderAsync(_request.LibraryId, parent.Path, name.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ordner konnte nicht angelegt werden.";
                return;
            }

            RegisterCatalogFolder(result.FolderId, result.Path);
            parent.ReloadChildren();
            parent.IsExpanded = true;
            var created = parent.Children.FirstOrDefault(c => string.Equals(c.Path, NormalizePath(result.Path), StringComparison.OrdinalIgnoreCase));
            if (created is not null)
            {
                created.IsSelected = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht angelegt werden: {ex.Message}";
            _logger.LogWarning(ex, "Archiv-Dialog: Neuer Ordner in {Parent} fehlgeschlagen", parent.Path);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateFolder() => SelectedNode is { IsPlaceholder: false };

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameFolderAsync()
    {
        var node = SelectedNode;
        if (node?.FolderId is not { } folderId || PromptName is null)
        {
            return;
        }

        var name = await PromptName("Ordner umbenennen", $"Neuer Name für „{node.Name}“:", node.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _fileOps.RenameFolderAsync(folderId, name.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ordner konnte nicht umbenannt werden.";
                return;
            }

            // Katalog-Index und Baum unter dem Elternknoten neu aufbauen (Pfade der Unterordner haben sich geändert)
            await LoadCatalogIndexOnlyAsync().ConfigureAwait(true);
            var parentPath = Path.GetDirectoryName(NormalizePath(result.Path));
            var parentNode = parentPath is null ? null : _rootNode?.FindByPath(parentPath);
            if (parentNode is not null)
            {
                parentNode.ReloadChildren();
                parentNode.IsExpanded = true;
                var renamed = parentNode.Children.FirstOrDefault(c => c.FolderId == folderId);
                if (renamed is not null)
                {
                    renamed.IsSelected = true;
                }
            }
            else
            {
                TargetPath = result.Path;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogWarning(ex, "Archiv-Dialog: Umbenennen von FolderId {FolderId} fehlgeschlagen", folderId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Öffnet einen Suchtreffer im Baum (Auswahl + Aufklappen).</summary>
    [RelayCommand]
    private void OpenSearchHit(ArchiveFolderNode? hit)
    {
        if (hit is null || _rootNode is null)
        {
            return;
        }

        SearchText = null;
        var node = _rootNode.FindByPath(hit.Path);
        if (node is null)
        {
            TargetPath = hit.Path;
            return;
        }

        ExpandTo(node);
        node.IsSelected = true;
    }

    // ---- Helfer ------------------------------------------------------------------------------

    private async Task LoadCatalogIndexOnlyAsync()
    {
        List<CatalogFolder> rows;
        await using (var ctx = _factory.Create())
        {
            var rootId = _request.ArchiveRootId;
            rows = await ctx.Folders.AsNoTracking()
                .Where(f => f.RootId == rootId)
                .Select(f => new CatalogFolder(f.Id, f.Path, f.Name))
                .ToListAsync()
                .ConfigureAwait(true);
        }

        _catalogByPath.Clear();
        _catalogChildren.Clear();
        foreach (var row in rows)
        {
            RegisterCatalogFolder(row.Id, row.Path, row.Name);
        }
    }

    private void RegisterCatalogFolder(int? id, string path, string? name = null)
    {
        var normalized = NormalizePath(path);
        _knownPaths[normalized] = normalized;
        if (id is not { } folderId)
        {
            return;
        }

        var row = new CatalogFolder(folderId, normalized, name ?? Path.GetFileName(normalized));
        _catalogByPath[normalized] = row;
        var parent = Path.GetDirectoryName(normalized);
        if (parent is null || !IsUnderRoot(parent))
        {
            return;
        }

        if (!_catalogChildren.TryGetValue(parent, out var list))
        {
            list = new List<CatalogFolder>();
            _catalogChildren[parent] = list;
        }

        list.RemoveAll(c => c.Id == folderId);
        list.Add(row);
    }

    private void ExpandTo(ArchiveFolderNode node)
    {
        // Alle Vorfahren aufklappen: vom Root entlang des Pfades
        if (_rootNode is null)
        {
            return;
        }

        var current = _rootNode;
        current.IsExpanded = true;
        var segments = node.Path.Length > ArchiveRootPath.Length
            ? node.Path[ArchiveRootPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        var path = ArchiveRootPath;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            var next = current.Children.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return;
            }

            if (!ReferenceEquals(next, node))
            {
                next.IsExpanded = true;
            }

            current = next;
        }
    }

    private void ClearTreeSelection()
    {
        _suppressSelection = true;
        try
        {
            if (SelectedNode is not null)
            {
                SelectedNode.IsSelected = false;
            }

            SelectedNode = null;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private bool IsUnderRoot(string path)
    {
        var normalized = NormalizePath(path);
        if (string.Equals(normalized, ArchiveRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.StartsWith(ArchiveRootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string RelativeTo(string path) =>
        path.Length > ArchiveRootPath.Length ? path[ArchiveRootPath.Length..].TrimStart(Path.DirectorySeparatorChar) : path;

    /// <summary>Voller Pfad ohne abschließende Trennzeichen; Laufwerkswurzeln behalten ihres.</summary>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

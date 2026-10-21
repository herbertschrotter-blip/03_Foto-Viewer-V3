using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Services.Operations;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Folders;

/// <summary>
/// Ordnerbaum der aktiven Bibliothek. Lädt alle Folder-Zeilen (eine Query) und die Dateianzahl je Ordner
/// (eine GroupBy-Query), baut daraus einen In-Memory-Index und materialisiert Knoten lazy beim Aufklappen.
/// Oberste Knoten sind die Wurzeln der Bibliothek mit Rolle (ADR-0020: Archiv zuerst, dann Eingänge).
/// Auswahl → FolderSelectedMessage; „Gesamte Bibliothek“ → FolderId null.
/// Kontextmenü „Neuer Ordner“ / „Umbenennen“ läuft protokolliert über <see cref="IFileOperationService"/>.
/// Lädt neu bei ActiveLibraryChangedMessage, ScanCompletedMessage, LibraryRootsChangedMessage, FoldersChangedMessage
/// und (gebündelt) nach Datei-Operationen (FilesChangedMessage Moved/Removed/Added: Zähler und neue Ordner);
/// aufgeklappte Knoten und die Auswahl werden dabei über den Pfad wiederhergestellt.
/// </summary>
public sealed partial class FolderTreeViewModel : ObservableObject
{
    private const string WholeLibraryText = "Gesamte Bibliothek";
    private const int ReloadDebounceMs = 300;

    private sealed record FolderRow(int Id, int? ParentId, int? RootId, string Name, string Path);

    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryService _libraries;
    private readonly IFileOperationService _fileOps;
    private readonly IDialogService _dialogs;
    private readonly FolderActions _folderActions;
    private readonly ILogger<FolderTreeViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    private Dictionary<int, List<FolderRow>> _childrenByParent = new();
    private List<FolderRow> _rootRows = new();
    private Dictionary<int, int> _directCounts = new();
    private Dictionary<int, int> _totalCounts = new();
    private Dictionary<int, LibraryRootInfo> _rootsById = new();
    private FolderNode? _selectedNode;
    private int _libraryId;
    private int? _loadedLibraryId;
    private bool _reloadPending;
    private CancellationTokenSource? _reloadDebounce;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLibrary;

    [ObservableProperty]
    private string? _libraryName;

    [ObservableProperty]
    private string? _hintText;

    [ObservableProperty]
    private string _selectionText = WholeLibraryText;

    [ObservableProperty]
    private bool _includeSubfolders = true;

    public FolderTreeViewModel(
        ILibraryContextFactory factory,
        ILibraryService libraries,
        IFileOperationService fileOps,
        IDialogService dialogs,
        FolderActions folderActions,
        ILogger<FolderTreeViewModel> logger,
        Libraries.IInboxActions? inboxActions = null,
        Gallery.CaptureDates.CaptureDateActions? captureDateActions = null)
    {
        _inboxActions = inboxActions; // „Eingang hinzufügen…“ im Seitenkopf (ADR-0042, FolderTreeViewModel.AddInbox.cs)
        _captureDateActions = captureDateActions; // „Datum setzen…“ (ADR-0046, FolderTreeViewModel.CaptureDate.cs)
        _folderActions = folderActions;
        _factory = factory;
        _libraries = libraries;
        _fileOps = fileOps;
        _dialogs = dialogs;
        _logger = logger;

        _messenger.Register<FolderTreeViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, LibraryRootsChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, FoldersChangedMessage>(this, static (r, m) => UiThread.Post(() => r.ScheduleReload(m.LibraryId)));
        _messenger.Register<FolderTreeViewModel, FilesChangedMessage>(this, static (r, m) => UiThread.Post(() => r.OnFilesChanged(m)));
    }

    [ObservableProperty]
    private string? _errorMessage;

    // ---- Kontextmenü: Neuer Ordner / Umbenennen (ADR-0020, protokolliert über IFileOperationService) ----

    [RelayCommand]
    private async Task CreateSubfolderAsync(FolderNode? parent)
    {
        if (parent is null || parent.IsPlaceholder || !HasLibrary)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Neuer Ordner", $"Neuer Ordner in „{parent.Path}“:", "Neuer Ordner", "Anlegen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var result = await _fileOps.CreateFolderAsync(_libraryId, parent.Path, name.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ordner konnte nicht angelegt werden.";
                return;
            }

            parent.IsExpanded = true;
            _logger.LogInformation("Ordnerbaum: Ordner angelegt {Path}", result.Path);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht angelegt werden: {ex.Message}";
            _logger.LogWarning(ex, "Ordnerbaum: Neuer Ordner in {Parent} fehlgeschlagen", parent.Path);
        }
    }

    /// <summary>Ordner umbenennen — mit Dateien über eine protokollierte RenameFolder-Operation (rückgängig machbar); Wurzeln nicht.</summary>
    [RelayCommand]
    private async Task RenameFolderAsync(FolderNode? node)
    {
        if (node is null || !node.CanRename || !HasLibrary)
        {
            return;
        }

        var newName = await _dialogs.PromptTextAsync("Ordner umbenennen", $"Neuer Name für „{node.Name}“:", node.Name, "Umbenennen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == node.Name)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var result = await _fileOps.RenameFolderAsync(node.Id, newName.Trim()).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.Error ?? "Ordner konnte nicht umbenannt werden.";
                return;
            }

            if (_selectedNode is not null && ReferenceEquals(_selectedNode, node))
            {
                SelectionText = result.Path; // Pfad des gewählten Knotens hat sich geändert; Reload stellt die Auswahl darüber wieder her
            }

            _logger.LogInformation("Ordnerbaum: Ordner umbenannt → {Path}", result.Path);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Ordner konnte nicht umbenannt werden: {ex.Message}";
            _logger.LogWarning(ex, "Ordnerbaum: Umbenennen von {Path} fehlgeschlagen", node.Path);
        }
    }

    public ObservableCollection<FolderNode> Roots { get; } = new();

    /// <summary>Beim Anzeigen der Seite: nur laden, wenn noch nichts geladen ist (Nachrichten halten den Baum aktuell).</summary>
    public Task EnsureLoadedAsync() => _loadedLibraryId is null ? ReloadAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand]
    private void SelectWholeLibrary()
    {
        ResetSelection();
        if (HasLibrary)
        {
            _messenger.Send(new FolderSelectedMessage(_libraryId, null, IncludeSubfolders));
            _logger.LogInformation("Ordnerauswahl: gesamte Bibliothek (Id={LibraryId})", _libraryId);
        }
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        if (HasLibrary && _selectedNode is not null)
        {
            _messenger.Send(new FolderSelectedMessage(_libraryId, _selectedNode.Id, value));
        }
    }

    internal void OnNodeSelected(FolderNode node)
    {
        if (node.IsPlaceholder || !HasLibrary)
        {
            return;
        }

        _selectedNode = node;
        SelectionText = node.Path;
        _messenger.Send(new FolderSelectedMessage(_libraryId, node.Id, IncludeSubfolders));
        _logger.LogInformation("Ordnerauswahl: {Path} (FolderId={FolderId}, Unterordner={Sub})", node.Path, node.Id, IncludeSubfolders);
    }

    internal IReadOnlyList<FolderNode> CreateChildren(FolderNode parent) =>
        _childrenByParent.TryGetValue(parent.Id, out var rows)
            ? rows.Select(CreateNode).ToList()
            : Array.Empty<FolderNode>();

    /// <summary>Datei-Operationen (Papierkorb, Verschieben, Undo) ändern Zähler und legen Ordner an → gebündelt neu laden.</summary>
    private void OnFilesChanged(FilesChangedMessage message)
    {
        if (message.Kind == FilesChangeKind.Metadata)
        {
            return;
        }

        ScheduleReload(message.LibraryId);
    }

    private void ScheduleReload(int libraryId)
    {
        if (_loadedLibraryId is null || libraryId != _loadedLibraryId)
        {
            return;
        }

        _reloadDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadDebounce = cts;
        _ = ReloadDebouncedAsync(cts.Token);
    }

    private async Task ReloadDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(ReloadDebounceMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        if (IsLoading)
        {
            _reloadPending = true; // nach dem laufenden Ladevorgang noch einmal (kein Update darf verloren gehen)
            return;
        }

        IsLoading = true;
        try
        {
            var active = await _libraries.GetActiveAsync().ConfigureAwait(true);
            if (active is null)
            {
                HasLibrary = false;
                LibraryName = null;
                HintText = "Keine aktive Bibliothek — zuerst unter „Bibliothek“ anlegen und scannen.";
                Roots.Clear();
                ResetSelection();
                _loadedLibraryId = null;
                return;
            }

            _libraryId = active.Id;
            LibraryName = active.Name;
            HasLibrary = true;
            var offline = active.Roots.Where(r => !r.IsAvailable).Select(r => r.Path).ToList();
            HintText = offline.Count == 0
                ? null
                : offline.Count == 1
                    ? $"Wurzel offline: {offline[0]} ist nicht erreichbar."
                    : $"{offline.Count} Wurzeln nicht erreichbar: {string.Join(", ", offline)}";
            _rootsById = active.Roots.ToDictionary(r => r.Id);

            List<FolderRow> rows;
            Dictionary<int, int> counts;
            await using (var ctx = _factory.Create())
            {
                rows = await ctx.Folders.AsNoTracking()
                    .Where(f => f.LibraryId == active.Id)
                    .Select(f => new FolderRow(f.Id, f.ParentId, f.RootId, f.Name, f.Path))
                    .ToListAsync()
                    .ConfigureAwait(true);
                counts = await ctx.Files.AsNoTracking()
                    .Where(f => f.LibraryId == active.Id)
                    .GroupBy(f => f.FolderId)
                    .Select(g => new { FolderId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.FolderId, x => x.Count)
                    .ConfigureAwait(true);
            }

            BuildIndex(rows, counts);

            // Zustand über Pfade merken: aufgeklappte Knoten und Auswahl überleben den Neuaufbau
            var expandedPaths = CollectExpandedPaths();
            var selectedPath = _selectedNode?.Path;

            Roots.Clear();
            ResetSelection();
            foreach (var root in _rootRows)
            {
                Roots.Add(CreateNode(root));
            }

            if (expandedPaths.Count > 0)
            {
                RestoreExpansion(Roots, expandedPaths);
            }
            else if (Roots.Count == 1)
            {
                Roots[0].IsExpanded = true;
            }

            if (selectedPath is not null)
            {
                RestoreSelection(selectedPath);
            }

            _loadedLibraryId = active.Id;
            _logger.LogInformation(
                "Ordnerbaum geladen: Library={LibraryId} Ordner={Folders} Wurzeln={Roots} Dateien={Files}",
                active.Id, rows.Count, _rootRows.Count, counts.Values.Sum());
        }
        catch (Exception ex)
        {
            HintText = $"Ordnerbaum konnte nicht geladen werden: {ex.Message}";
            _logger.LogError(ex, "Ordnerbaum konnte nicht geladen werden");
        }
        finally
        {
            IsLoading = false;
        }

        if (_reloadPending)
        {
            _reloadPending = false;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    private HashSet<string> CollectExpandedPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (node.IsExpanded)
                {
                    result.Add(node.Path);
                    Visit(node.Children);
                }
            }
        }

        Visit(Roots);
        return result;
    }

    private static void RestoreExpansion(IEnumerable<FolderNode> nodes, HashSet<string> expandedPaths)
    {
        foreach (var node in nodes)
        {
            if (node.IsPlaceholder || !expandedPaths.Contains(node.Path))
            {
                continue;
            }

            node.IsExpanded = true; // lädt die Kinder
            RestoreExpansion(node.Children, expandedPaths);
        }
    }

    /// <summary>Auswahl per Pfad wiederherstellen; der Knoten sendet dabei erneut FolderSelectedMessage (Galerie ignoriert unveränderte Filter).</summary>
    private void RestoreSelection(string path)
    {
        var node = FindLoadedNode(Roots, path);
        if (node is null)
        {
            _logger.LogInformation("Ordnerbaum: zuvor gewählter Ordner existiert nicht mehr: {Path}", path);
            return;
        }

        node.IsSelected = true;
    }

    private static FolderNode? FindLoadedNode(IEnumerable<FolderNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.IsPlaceholder)
            {
                continue;
            }

            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.IsExpanded && FindLoadedNode(node.Children, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void BuildIndex(List<FolderRow> rows, Dictionary<int, int> counts)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        var comparer = StringComparer.CurrentCultureIgnoreCase;

        _childrenByParent = new Dictionary<int, List<FolderRow>>();
        _rootRows = new List<FolderRow>();
        foreach (var row in rows)
        {
            // Wurzel = ParentId NULL (oder verwaiste ParentId — defensiv ebenfalls als Wurzel zeigen)
            if (row.ParentId is { } parentId && ids.Contains(parentId))
            {
                if (!_childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<FolderRow>();
                    _childrenByParent[parentId] = list;
                }

                list.Add(row);
            }
            else
            {
                _rootRows.Add(row);
            }
        }

        foreach (var list in _childrenByParent.Values)
        {
            list.Sort((a, b) => comparer.Compare(a.Name, b.Name));
        }

        // Wurzeln: Archiv zuerst, dann Eingänge in ihrer SortOrder, verwaiste Zeilen zuletzt nach Pfad (ADR-0020)
        _rootRows.Sort((a, b) =>
        {
            var ka = RootSortKey(a);
            var kb = RootSortKey(b);
            var byKey = ka.CompareTo(kb);
            return byKey != 0 ? byKey : comparer.Compare(a.Path, b.Path);
        });

        _directCounts = counts;
        _totalCounts = new Dictionary<int, int>(rows.Count);
        foreach (var root in _rootRows)
        {
            ComputeTotal(root.Id);
        }
    }

    private int ComputeTotal(int folderId)
    {
        var total = _directCounts.GetValueOrDefault(folderId);
        if (_childrenByParent.TryGetValue(folderId, out var children))
        {
            foreach (var child in children)
            {
                total += ComputeTotal(child.Id);
            }
        }

        _totalCounts[folderId] = total;
        return total;
    }

    private int RootSortKey(FolderRow row)
    {
        if (row.RootId is { } rootId && _rootsById.TryGetValue(rootId, out var root) && IsRootRow(row, root))
        {
            return root.IsArchive ? 0 : 1 + root.SortOrder;
        }

        return int.MaxValue;
    }

    /// <summary>Zeile ist die Wurzel selbst (Pfad = Wurzelpfad), nicht ein verwaister Unterordner mit ParentId NULL.</summary>
    private static bool IsRootRow(FolderRow row, LibraryRootInfo root) =>
        string.Equals(
            row.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            root.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private string? RoleLabelOf(FolderRow row) =>
        row.ParentId is null && row.RootId is { } rootId && _rootsById.TryGetValue(rootId, out var root) && IsRootRow(row, root)
            ? root.RoleText
            : null;

    private FolderNode CreateNode(FolderRow row) => new(
        this,
        row.Id,
        row.Name,
        row.Path,
        _directCounts.GetValueOrDefault(row.Id),
        _totalCounts.GetValueOrDefault(row.Id),
        _childrenByParent.ContainsKey(row.Id),
        RoleLabelOf(row),
        row.RootId is { } rootId && _rootsById.TryGetValue(rootId, out var root) && root.IsInbox);

    private void ResetSelection()
    {
        if (_selectedNode is not null)
        {
            _selectedNode.IsSelected = false;
            _selectedNode = null;
        }

        SelectionText = WholeLibraryText;
    }
}

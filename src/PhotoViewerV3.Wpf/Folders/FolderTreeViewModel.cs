using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Database;
using PhotoViewerV3.Services.Library;
using PhotoViewerV3.Services.Messaging;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Folders;

/// <summary>
/// Ordnerbaum der aktiven Bibliothek. Lädt alle Folder-Zeilen (eine Query) und die Dateianzahl je Ordner
/// (eine GroupBy-Query), baut daraus einen In-Memory-Index und materialisiert Knoten lazy beim Aufklappen.
/// Auswahl → FolderSelectedMessage; „Gesamte Bibliothek“ → FolderId null.
/// Lädt neu bei ActiveLibraryChangedMessage und ScanCompletedMessage.
/// </summary>
public sealed partial class FolderTreeViewModel : ObservableObject
{
    private const string WholeLibraryText = "Gesamte Bibliothek";

    private sealed record FolderRow(int Id, int? ParentId, string Name, string Path);

    private readonly ILibraryContextFactory _factory;
    private readonly ILibraryService _libraries;
    private readonly ILogger<FolderTreeViewModel> _logger;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;

    private Dictionary<int, List<FolderRow>> _childrenByParent = new();
    private List<FolderRow> _rootRows = new();
    private Dictionary<int, int> _directCounts = new();
    private Dictionary<int, int> _totalCounts = new();
    private FolderNode? _selectedNode;
    private int _libraryId;
    private int? _loadedLibraryId;

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

    public FolderTreeViewModel(ILibraryContextFactory factory, ILibraryService libraries, ILogger<FolderTreeViewModel> logger)
    {
        _factory = factory;
        _libraries = libraries;
        _logger = logger;

        _messenger.Register<FolderTreeViewModel, ActiveLibraryChangedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
        _messenger.Register<FolderTreeViewModel, ScanCompletedMessage>(this, static (r, m) => UiThread.Post(() => { _ = r.ReloadAsync(); }));
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

    private async Task ReloadAsync()
    {
        if (IsLoading)
        {
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
            HintText = active.IsRootAvailable ? null : $"Bibliothek offline: {active.RootPath} ist nicht erreichbar.";

            List<FolderRow> rows;
            Dictionary<int, int> counts;
            await using (var ctx = _factory.Create())
            {
                rows = await ctx.Folders.AsNoTracking()
                    .Where(f => f.LibraryId == active.Id)
                    .Select(f => new FolderRow(f.Id, f.ParentId, f.Name, f.Path))
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

            Roots.Clear();
            ResetSelection();
            foreach (var root in _rootRows)
            {
                Roots.Add(CreateNode(root));
            }

            if (Roots.Count == 1)
            {
                Roots[0].IsExpanded = true;
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

        _rootRows.Sort((a, b) => comparer.Compare(a.Path, b.Path));

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

    private FolderNode CreateNode(FolderRow row) => new(
        this,
        row.Id,
        row.Name,
        row.Path,
        _directCounts.GetValueOrDefault(row.Id),
        _totalCounts.GetValueOrDefault(row.Id),
        _childrenByParent.ContainsKey(row.Id));

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

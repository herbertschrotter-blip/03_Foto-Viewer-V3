using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PhotoViewerV3.Folders;

/// <summary>
/// Knoten im Ordnerbaum. Kinder werden erst beim Aufklappen aus dem In-Memory-Index des ViewModels
/// erzeugt (lazy); bis dahin hält ein Platzhalter-Knoten den Expander sichtbar. Wurzelordner der Bibliothek
/// (ADR-0020) tragen ihre Rolle (<see cref="RoleLabel"/> „Archiv“ / „Eingang“).
/// </summary>
public sealed partial class FolderNode : ObservableObject
{
    private readonly FolderTreeViewModel? _owner;
    private bool _childrenLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Platzhalter-Knoten (ohne Owner), der beim ersten Aufklappen ersetzt wird.</summary>
    private FolderNode()
    {
        Id = -1;
        Name = "…";
        Path = string.Empty;
    }

    public FolderNode(FolderTreeViewModel owner, int id, string name, string path, int fileCount, int totalFileCount, bool hasChildren, string? roleLabel = null, bool isInInbox = false)
    {
        IsInInbox = isInInbox;
        _owner = owner;
        Id = id;
        Name = name;
        Path = path;
        FileCount = fileCount;
        TotalFileCount = totalFileCount;
        HasChildren = hasChildren;
        RoleLabel = roleLabel;

        if (hasChildren)
        {
            Children.Add(new FolderNode());
        }
    }

    public int Id { get; }

    public string Name { get; }

    public string Path { get; }

    /// <summary>Dateien direkt in diesem Ordner.</summary>
    public int FileCount { get; }

    /// <summary>Dateien inklusive aller Unterordner.</summary>
    public int TotalFileCount { get; }

    public bool HasChildren { get; }

    /// <summary>„Archiv“ / „Eingang“ bei Wurzelordnern der Bibliothek, sonst null.</summary>
    public string? RoleLabel { get; }

    public bool IsLibraryRoot => RoleLabel is not null;

    public bool IsPlaceholder => _owner is null;

    /// <summary>Ordner liegt unter einer Eingangs-Wurzel (inkl. der Wurzel selbst) — „In die Bibliothek übernehmen…“ möglich (ADR-0034).</summary>
    public bool IsInInbox { get; }

    /// <summary>Kontextmenü „Bewertung für alle Dateien“: Parameter „1“–„5“, „0“ = Bewertung entfernen.</summary>
    [RelayCommand]
    private Task RateAllAsync(string? stars)
        => _owner is null ? Task.CompletedTask : _owner.RateFolderAsync(this, byte.TryParse(stars, out var s) && s is >= 1 and <= 5 ? s : null);

    /// <summary>Kontextmenü „Favorit für alle“: Parameter „true“ / „false“.</summary>
    [RelayCommand]
    private Task FavoriteAllAsync(string? value)
        => _owner is null ? Task.CompletedTask : _owner.FavoriteFolderAsync(this, string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Umbenennen nur für normale Ordner (Wurzeln sind Bibliothekseinstellung, Platzhalter nichts).</summary>
    public bool CanRename => !IsPlaceholder && !IsLibraryRoot;

    public ObservableCollection<FolderNode> Children { get; } = new();

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildrenLoaded();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && _owner is not null)
        {
            _owner.OnNodeSelected(this);
        }
    }

    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || _owner is null)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in _owner.CreateChildren(this))
        {
            Children.Add(child);
        }
    }
}

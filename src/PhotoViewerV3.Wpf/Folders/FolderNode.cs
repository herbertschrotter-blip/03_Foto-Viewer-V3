using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Folders;

/// <summary>
/// Knoten im Ordnerbaum. Kinder werden erst beim Aufklappen aus dem In-Memory-Index des ViewModels
/// erzeugt (lazy); bis dahin hält ein Platzhalter-Knoten den Expander sichtbar.
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

    public FolderNode(FolderTreeViewModel owner, int id, string name, string path, int fileCount, int totalFileCount, bool hasChildren)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Path = path;
        FileCount = fileCount;
        TotalFileCount = totalFileCount;
        HasChildren = hasChildren;

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

    public bool IsPlaceholder => _owner is null;

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

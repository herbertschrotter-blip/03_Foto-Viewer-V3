using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumora.Gallery.ArchiveTarget;

/// <summary>
/// Knoten im Zielordner-Baum des Archiv-Dialogs: Katalog-Ordner (mit FolderId) oder Dateisystem-Ordner ohne
/// Katalogzeile (<see cref="FolderId"/> null). Kinder entstehen lazy beim Aufklappen über den Owner.
/// </summary>
public sealed partial class ArchiveFolderNode : ObservableObject
{
    private readonly ArchiveTargetViewModel? _owner;
    private bool _childrenLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private int? _folderId;

    /// <summary>Platzhalter (ohne Owner), der beim ersten Aufklappen ersetzt wird.</summary>
    private ArchiveFolderNode()
    {
        _name = "…";
        _path = string.Empty;
    }

    public ArchiveFolderNode(ArchiveTargetViewModel owner, int? folderId, string name, string path, bool hasChildren, bool isRoot = false)
    {
        _owner = owner;
        _folderId = folderId;
        _name = name;
        _path = path;
        IsRoot = isRoot;
        if (hasChildren)
        {
            Children.Add(new ArchiveFolderNode());
        }
    }

    public bool IsRoot { get; }

    public bool IsPlaceholder => _owner is null;

    /// <summary>Ordner nur auf der Platte, noch ohne Katalogzeile (wird beim Verschieben bzw. nächsten Scan angelegt).</summary>
    public bool IsFileSystemOnly => FolderId is null && !IsPlaceholder;

    public string DisplayName => IsRoot ? $"{Name}  (Bibliotheksordner)" : Name;

    public ObservableCollection<ArchiveFolderNode> Children { get; } = new();

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildrenLoaded();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _owner?.OnNodeSelected(this);
        }
    }

    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || _owner is null)
        {
            return;
        }

        _childrenLoaded = true;
        ReloadChildren();
    }

    /// <summary>Kinder aus Katalog + Dateisystem neu aufbauen (nach „Neuer Ordner“ / „Umbenennen“).</summary>
    public void ReloadChildren()
    {
        if (_owner is null)
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

    /// <summary>Sucht einen bereits erzeugten Nachfahren über den Pfad (lädt Kinder entlang des Weges nach).</summary>
    public ArchiveFolderNode? FindByPath(string path)
    {
        if (string.Equals(TrimPath(Path), TrimPath(path), System.StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        var prefix = TrimPath(Path) + System.IO.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        EnsureChildrenLoaded();
        foreach (var child in Children)
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            if (child.FindByPath(path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string TrimPath(string path) =>
        path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
}

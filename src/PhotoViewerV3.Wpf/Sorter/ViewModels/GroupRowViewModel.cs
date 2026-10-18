using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Sorter;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>
/// Zeile der Gruppentabelle. Schreibt Zielordner und Auswahl direkt in die <see cref="SorterGroup"/>
/// des Analyse-Ergebnisses, damit Plan und In-Memory-Operationen dieselben Daten sehen.
/// </summary>
public sealed partial class GroupRowViewModel : ObservableObject
{
    private const int PreviewCount = 5;

    [ObservableProperty]
    private string _targetFolder;

    [ObservableProperty]
    private bool _isIncluded;

    /// <summary>Markierung in der Liste (für „Zusammenführen“).</summary>
    [ObservableProperty]
    private bool _isMarked;

    public GroupRowViewModel(SorterGroup group)
    {
        Group = group;
        _targetFolder = group.TargetFolder;
        _isIncluded = group.IsSelected;
        Children = new ObservableCollection<GroupRowViewModel>(group.SubGroups.Select(sg => new GroupRowViewModel(sg)));
    }

    public SorterGroup Group { get; }

    public ObservableCollection<GroupRowViewModel> Children { get; }

    public string Key => Group.Key;

    public string DisplayKey => Group.Level > 1 ? "↳ " + Group.Key : Group.Key;

    public string PatternName => Group.PatternName;

    public int Level => Group.Level;

    public double Indent => (Group.Level - 1) * 18;

    public int FileCount => Group.FileCount;

    public string CountDisplay => Group.NotInCatalogCount > 0
        ? $"{Group.MovableCount:N0} (+{Group.NotInCatalogCount:N0})"
        : Group.FileCount.ToString("N0");

    public string SizeDisplay => SorterPaths.FormatSize(Group.TotalSize);

    public string PreviewText
    {
        get
        {
            var names = string.Join(", ", Group.PreviewFiles);
            return Group.FileCount > PreviewCount ? $"{names} … (+{Group.FileCount - PreviewCount:N0})" : names;
        }
    }

    public string? FolderHint => Group.FolderExists ? $"Ordner existiert bereits ({Group.FolderFileCount:N0} Dateien)" : null;

    public string? CatalogHint => Group.NotInCatalogCount > 0
        ? $"{Group.NotInCatalogCount:N0} Datei(en) nicht im Katalog – erst neu scannen"
        : null;

    public bool IsUnsorted => Group.IsUnsorted;

    public bool HasChildren => Children.Count > 0;

    partial void OnTargetFolderChanged(string value) => Group.TargetFolder = value ?? string.Empty;

    partial void OnIsIncludedChanged(bool value) => Group.IsSelected = value;
}

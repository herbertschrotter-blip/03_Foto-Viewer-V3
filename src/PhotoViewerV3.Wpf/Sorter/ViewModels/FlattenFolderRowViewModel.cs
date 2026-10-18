using CommunityToolkit.Mvvm.ComponentModel;
using PhotoViewerV3.Services.Sorter;

namespace PhotoViewerV3.Sorter.ViewModels;

/// <summary>Zeile der Flatten-Vorschau; schreibt Zielname und Auswahl direkt in den <see cref="FlattenFolder"/>.</summary>
public sealed partial class FlattenFolderRowViewModel : ObservableObject
{
    private const int PreviewCount = 5;

    [ObservableProperty]
    private string _targetName;

    [ObservableProperty]
    private bool _isIncluded;

    public FlattenFolderRowViewModel(FlattenFolder folder)
    {
        Folder = folder;
        _targetName = folder.TargetName;
        _isIncluded = folder.IsSelected;
    }

    public FlattenFolder Folder { get; }

    public bool IsRoot => Folder.IsRoot;

    public string PathDisplay => Folder.IsRoot ? "(Wurzel)" : Folder.RelativePath;

    public string CountDisplay => Folder.NotInCatalogCount > 0
        ? $"{Folder.MovableCount:N0} (+{Folder.NotInCatalogCount:N0})"
        : Folder.FileCount.ToString("N0");

    public string SizeDisplay => SorterPaths.FormatSize(Folder.TotalSize);

    public string PreviewText
    {
        get
        {
            var names = string.Join(", ", Folder.PreviewFiles);
            return Folder.FileCount > PreviewCount ? $"{names} … (+{Folder.FileCount - PreviewCount:N0})" : names;
        }
    }

    public string? CatalogHint => Folder.NotInCatalogCount > 0
        ? $"{Folder.NotInCatalogCount:N0} Datei(en) nicht im Katalog – erst neu scannen"
        : null;

    partial void OnTargetNameChanged(string value) => Folder.TargetName = value ?? string.Empty;

    partial void OnIsIncludedChanged(bool value) => Folder.IsSelected = value;
}

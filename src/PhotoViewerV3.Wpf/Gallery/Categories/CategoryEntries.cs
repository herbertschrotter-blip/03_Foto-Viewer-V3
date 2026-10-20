using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoViewerV3.Gallery.Categories;

/// <summary>Eintrag im Kategorien-Filter: ein Wert oder „ohne Wert“ (<see cref="ValueId"/> null); Häkchen = gehört zum Filter.</summary>
public sealed partial class CategoryFilterEntry : ObservableObject
{
    public CategoryFilterEntry(int categoryId, int? valueId, string name, int fileCount)
    {
        CategoryId = categoryId;
        ValueId = valueId;
        Name = name;
        FileCount = fileCount;
    }

    public int CategoryId { get; }

    /// <summary>null = „ohne Wert“.</summary>
    public int? ValueId { get; }

    public bool IsNone => ValueId is null;

    public string Name { get; }

    public int FileCount { get; }

    public string CountText => IsNone ? string.Empty : FileCount.ToString("N0");

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Eine Kategorie im Filter-Dropdown mit ihren Werten und „ohne Wert“ (ADR-0027).</summary>
public sealed class CategoryFilterGroup
{
    public CategoryFilterGroup(int categoryId, string name, IReadOnlyList<CategoryFilterEntry> entries)
    {
        CategoryId = categoryId;
        Name = name;
        Entries = entries;
    }

    public int CategoryId { get; }

    public string Name { get; }

    public IReadOnlyList<CategoryFilterEntry> Entries { get; }

    public bool IsActive => Entries.Any(e => e.IsSelected);

    /// <summary>„Projekt: Nord, ohne Wert“ für die Beschriftung des Filter-Knopfs; null ohne Auswahl.</summary>
    public string? SelectionText
    {
        get
        {
            var names = Entries.Where(e => e.IsSelected).Select(e => e.IsNone ? "ohne Wert" : e.Name).ToList();
            return names.Count == 0 ? null : $"{Name}: {string.Join(", ", names)}";
        }
    }
}

/// <summary>
/// Eintrag im Kontextmenü „Kategorie → Wert“: Zustand bezogen auf die Auswahl (true = alle ausgewählten Dateien haben
/// diesen Wert, null = einige, false = keine). <see cref="ValueId"/> null = „Kein Wert“ (Wert entfernen).
/// </summary>
public sealed partial class CategoryMenuEntry : ObservableObject
{
    public CategoryMenuEntry(int categoryId, string categoryName, int? valueId, string name)
    {
        CategoryId = categoryId;
        CategoryName = categoryName;
        ValueId = valueId;
        Name = name;
    }

    public int CategoryId { get; }

    public string CategoryName { get; }

    public int? ValueId { get; }

    public bool IsNone => ValueId is null;

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnAll))]
    private bool? _state;

    public bool IsOnAll => State == true;
}

/// <summary>Untermenü einer Kategorie im Kontextmenü.</summary>
public sealed class CategoryMenuGroup
{
    public CategoryMenuGroup(int categoryId, string name, IReadOnlyList<CategoryMenuEntry> entries)
    {
        CategoryId = categoryId;
        Name = name;
        Entries = entries;
    }

    public int CategoryId { get; }

    public string Name { get; }

    public IReadOnlyList<CategoryMenuEntry> Entries { get; }
}

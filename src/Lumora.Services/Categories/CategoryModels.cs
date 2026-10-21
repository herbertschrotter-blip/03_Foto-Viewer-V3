namespace Lumora.Services.Categories;

/// <summary>Anzeige-DTO eines Kategorie-Werts.</summary>
public sealed record CategoryValueInfo(int Id, int CategoryId, string Name, int SortOrder);

/// <summary>Anzeige-DTO einer Kategorie mit ihren Werten in Anzeigereihenfolge (ADR-0027).</summary>
public sealed record CategoryInfo(int Id, string Name, int SortOrder, IReadOnlyList<CategoryValueInfo> Values)
{
    public CategoryValueInfo? FindValue(int valueId)
    {
        foreach (var value in Values)
        {
            if (value.Id == valueId)
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>Art einer Änderung an Kategorien oder ihren Werten (nicht an Zuweisungen — dafür <c>FilesChangedMessage</c>).</summary>
public enum CategoriesChangeKind
{
    Created = 0,
    Renamed = 1,
    Reordered = 2,
    Deleted = 3,
    ValuesChanged = 4,
}

/// <summary>
/// Kategorien oder ihre Werte haben sich geändert. Empfänger (Galerie: Filter, Spalten, Kontextmenü) laden die Liste
/// neu; Zuweisungen an Dateien werden getrennt als <c>FilesChangedMessage</c> (Kind = Metadata) gemeldet.
/// Transport: WeakReferenceMessenger, ggf. von Worker-Threads.
/// </summary>
public sealed record CategoriesChangedMessage(CategoriesChangeKind Kind, int? CategoryId);

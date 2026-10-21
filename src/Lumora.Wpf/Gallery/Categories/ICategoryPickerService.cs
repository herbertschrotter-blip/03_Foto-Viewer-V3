using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumora.Services.Categories;

namespace Lumora.Gallery.Categories;

/// <summary>
/// Anfrage an die Kategorie-Auswahl (Strg+K, ADR-0027): alle Kategorien mit Werten und die aktuellen Werte der
/// betroffenen Dateien (FileId → CategoryId → ValueId; Dateien ohne Wert fehlen).
/// </summary>
public sealed record CategoryPickRequest(
    IReadOnlyList<CategoryInfo> Categories,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> ValuesByFile,
    IReadOnlyList<int> FileIds)
{
    public int SelectionCount => FileIds.Count;
}

/// <summary>Eine Änderung: Kategorie bekommt bei allen Dateien den Wert (<see cref="ValueId"/> null = „Kein Wert“).</summary>
public sealed record CategoryValueChange(int CategoryId, int? ValueId);

/// <summary>Ergebnis der Kategorie-Auswahl: nur die geänderten Kategorien.</summary>
public sealed record CategoryPickResult(IReadOnlyList<CategoryValueChange> Changes)
{
    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>Kategorie-Auswahl-Dialog hinter einem Interface, damit ViewModels und Rauchtest ohne Fenster auskommen.</summary>
public interface ICategoryPickerService
{
    /// <summary>Zeigt den Dialog; null bei Abbruch.</summary>
    Task<CategoryPickResult?> PickAsync(CategoryPickRequest request, Window? owner);
}

/// <summary>Öffnet <see cref="CategoryPickerDialog"/> modal.</summary>
public sealed class CategoryPickerService : ICategoryPickerService
{
    public Task<CategoryPickResult?> PickAsync(CategoryPickRequest request, Window? owner)
        => Task.FromResult(CategoryPickerDialog.Show(owner, request));
}

/// <summary>Auswahlmöglichkeit einer Kategorie im Dialog: ein Wert, „Kein Wert“ oder „gemischt — nicht ändern“.</summary>
public sealed record CategoryPickOption(int? ValueId, string Label, bool KeepsMixed = false)
{
    public bool IsNone => ValueId is null && !KeepsMixed;

    public override string ToString() => Label;
}

/// <summary>
/// Zeile im Kategorie-Auswahl-Dialog: Kategorie mit ihren Optionen. Ausgangszustand = gemeinsamer Wert aller Dateien,
/// „Kein Wert“ (keine hat einen) oder „gemischt“ (dann bleibt die Kategorie unverändert, solange nichts gewählt wird).
/// </summary>
public sealed partial class CategoryPickItem : ObservableObject
{
    public CategoryPickItem(CategoryInfo category, IReadOnlyList<CategoryPickOption> options, CategoryPickOption initial, string hint)
    {
        Category = category;
        Options = options;
        Initial = initial;
        _selected = initial;
        Hint = hint;
    }

    public CategoryInfo Category { get; }

    public string Name => Category.Name;

    public IReadOnlyList<CategoryPickOption> Options { get; }

    public CategoryPickOption Initial { get; }

    /// <summary>„3 von 5 mit Wert“ bei gemischtem Ausgangszustand, sonst leer.</summary>
    public string Hint { get; }

    [ObservableProperty] private CategoryPickOption _selected;

    /// <summary>Optionen der Kategorie in Anzeigereihenfolge (für Tests: Wert nach Name wählen).</summary>
    public CategoryPickOption? Find(string label) => Options.FirstOrDefault(o => o.Label == label);
}

/// <summary>Reine Logik der Kategorie-Auswahl (ohne Fenster testbar).</summary>
public static class CategoryPickLogic
{
    public const string NoneLabel = "Kein Wert";
    public const string MixedLabel = "(gemischt — nicht ändern)";

    /// <summary>Je Kategorie eine Zeile mit Ausgangszustand aus den Werten der Dateien.</summary>
    public static IReadOnlyList<CategoryPickItem> BuildItems(CategoryPickRequest request)
    {
        var items = new List<CategoryPickItem>(request.Categories.Count);
        foreach (var category in request.Categories)
        {
            var counts = new Dictionary<int, int>();
            foreach (var fileId in request.FileIds)
            {
                if (request.ValuesByFile.TryGetValue(fileId, out var values) && values.TryGetValue(category.Id, out var valueId)
                    && category.FindValue(valueId) is not null)
                {
                    counts[valueId] = counts.GetValueOrDefault(valueId) + 1;
                }
            }

            var withValue = counts.Values.Sum();
            var mixed = request.FileIds.Count > 0 && (counts.Count > 1 || (withValue > 0 && withValue < request.FileIds.Count));
            var options = new List<CategoryPickOption>(category.Values.Count + 2);
            CategoryPickOption? mixedOption = null;
            if (mixed)
            {
                mixedOption = new CategoryPickOption(null, MixedLabel, KeepsMixed: true);
                options.Add(mixedOption);
            }

            options.AddRange(category.Values.Select(v => new CategoryPickOption(v.Id, v.Name)));
            var none = new CategoryPickOption(null, NoneLabel);
            options.Add(none);

            var initial = mixedOption
                          ?? (counts.Count == 1 ? options.First(o => o.ValueId == counts.Keys.First()) : none);
            var hint = mixed ? $"{withValue} von {request.FileIds.Count} mit Wert" : string.Empty;
            items.Add(new CategoryPickItem(category, options, initial, hint));
        }

        return items;
    }

    /// <summary>Änderungen gegenüber dem Ausgangszustand; „gemischt — nicht ändern“ ändert nichts.</summary>
    public static CategoryPickResult ComputeResult(IEnumerable<CategoryPickItem> items)
    {
        var changes = new List<CategoryValueChange>();
        foreach (var item in items)
        {
            if (item.Selected.KeepsMixed || Equals(item.Selected, item.Initial))
            {
                continue;
            }

            changes.Add(new CategoryValueChange(item.Category.Id, item.Selected.ValueId));
        }

        return new CategoryPickResult(changes);
    }
}

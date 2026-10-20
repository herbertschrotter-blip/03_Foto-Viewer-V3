using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Services.Categories;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Settings.CategoryManagement;

/// <summary>Ein Wert in der Verwaltung: Name, Zahl der Dateien, Position innerhalb der Kategorie.</summary>
public sealed class CategoryValueRow
{
    public CategoryValueRow(CategoryRow owner, CategoryValueInfo value, int fileCount, bool canMoveUp, bool canMoveDown)
    {
        Owner = owner;
        Value = value;
        FileCount = fileCount;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
    }

    public CategoryRow Owner { get; }

    public CategoryValueInfo Value { get; }

    public int Id => Value.Id;

    public string Name => Value.Name;

    public int FileCount { get; }

    public string FileCountText => FileCount == 1 ? "1 Datei" : $"{FileCount:N0} Dateien";

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }
}

/// <summary>Eine Kategorie in der Verwaltung mit ihren Werten und dem Eingabefeld „Neuer Wert“.</summary>
public sealed partial class CategoryRow : ObservableObject
{
    public CategoryRow(CategoryInfo category, IReadOnlyDictionary<int, int> counts, bool canMoveUp, bool canMoveDown)
    {
        Category = category;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        for (var i = 0; i < category.Values.Count; i++)
        {
            var value = category.Values[i];
            Values.Add(new CategoryValueRow(this, value, counts.GetValueOrDefault(value.Id), i > 0, i < category.Values.Count - 1));
        }
    }

    public CategoryInfo Category { get; }

    public int Id => Category.Id;

    public string Name => Category.Name;

    public ObservableCollection<CategoryValueRow> Values { get; } = new();

    public bool HasNoValues => Values.Count == 0;

    public int FileCount => Values.Sum(v => v.FileCount);

    public string SummaryText => $"{Values.Count:N0} Wert(e) · {FileCount:N0} Datei(en) zugeordnet";

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    [ObservableProperty] private string _newValueName = string.Empty;
}

/// <summary>
/// Seite „Kategorien“ (ADR-0027): Kategorien mit ihren Werten anlegen, umbenennen, sortieren und löschen (mit
/// Bestätigung und Zähler). Schreibzugriffe über <see cref="ICategoryService"/>; dessen <c>CategoriesChangedMessage</c>
/// aktualisiert Galerie-Filter, Spalten und Kontextmenü. Die Liste wird beim Anzeigen und nach jeder Aktion neu geladen.
/// </summary>
public sealed partial class CategoryManagementViewModel : ObservableObject
{
    private readonly ICategoryService _categories;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CategoryManagementViewModel> _logger;

    public CategoryManagementViewModel(ICategoryService categories, IDialogService dialogs, ILogger<CategoryManagementViewModel> logger)
    {
        _categories = categories;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<CategoryRow> Categories { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _newCategoryName = string.Empty;

    /// <summary>Optionale Werte der neuen Kategorie, durch Komma oder Semikolon getrennt („N, NN“).</summary>
    [ObservableProperty] private string _newCategoryValues = string.Empty;

    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasNoCategories = true;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isErrorOpen;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _isInfoOpen;

    partial void OnErrorMessageChanged(string? value) => IsErrorOpen = !string.IsNullOrEmpty(value);

    partial void OnInfoMessageChanged(string? value) => IsInfoOpen = !string.IsNullOrEmpty(value);

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var categories = await _categories.GetAllAsync().ConfigureAwait(true);
            var counts = await _categories.GetFileCountsAsync().ConfigureAwait(true);
            Categories.Clear();
            for (var i = 0; i < categories.Count; i++)
            {
                Categories.Add(new CategoryRow(categories[i], counts, i > 0, i < categories.Count - 1));
            }

            HasNoCategories = Categories.Count == 0;
            SummaryText = Categories.Count == 0
                ? "Noch keine Kategorien angelegt."
                : $"{Categories.Count:N0} Kategorie(n) · {Categories.Sum(c => c.Values.Count):N0} Wert(e)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kategorien konnten nicht geladen werden");
            ErrorMessage = $"Kategorien konnten nicht geladen werden: {ex.Message}";
        }
    }

    /// <summary>„N, NN; NNN“ → einzelne Werte (leere fallen weg).</summary>
    public static IReadOnlyList<string> SplitValues(string? text)
        => (text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewCategoryName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => RunAsync(async () =>
    {
        var category = await _categories.CreateAsync(NewCategoryName, SplitValues(NewCategoryValues)).ConfigureAwait(true);
        NewCategoryName = string.Empty;
        NewCategoryValues = string.Empty;
        InfoMessage = category.Values.Count == 0
            ? $"Kategorie „{category.Name}“ angelegt — jetzt Werte hinzufügen."
            : $"Kategorie „{category.Name}“ mit {category.Values.Count:N0} Wert(en) angelegt.";
    });

    [RelayCommand]
    private async Task RenameAsync(CategoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Kategorie umbenennen", $"Neuer Name für die Kategorie „{row.Name}“:", row.Name, "Umbenennen").ConfigureAwait(true);
        if (name is null || string.Equals(name.Trim(), row.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(() => _categories.RenameAsync(row.Id, name)).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task MoveUpAsync(CategoryRow? row) => MoveCategoryAsync(row, -1);

    [RelayCommand]
    private Task MoveDownAsync(CategoryRow? row) => MoveCategoryAsync(row, +1);

    private Task MoveCategoryAsync(CategoryRow? row, int delta)
    {
        var order = Categories.Select(c => c.Id).ToList();
        return Swap(order, row?.Id, delta) ? RunAsync(() => _categories.ReorderAsync(order)) : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteAsync(CategoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        var message = row.FileCount > 0
            ? $"Die Kategorie „{row.Name}“ mit {row.Values.Count:N0} Wert(en) ist {row.FileCount:N0} Datei(en) zugeordnet. " +
              "Die Zuordnungen werden entfernt, die Dateien selbst bleiben unverändert."
            : $"Die Kategorie „{row.Name}“ mit {row.Values.Count:N0} Wert(en) ist keiner Datei zugeordnet.";
        if (!await _dialogs.ConfirmAsync("Kategorie löschen", message, "Löschen", isDestructive: true).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var removed = await _categories.DeleteAsync(row.Id).ConfigureAwait(true);
            InfoMessage = $"Kategorie „{row.Name}“ gelöscht ({removed:N0} Zuordnung(en) entfernt).";
        }).ConfigureAwait(true);
    }

    // ---- Werte ---------------------------------------------------------------------------------

    [RelayCommand]
    private Task AddValueAsync(CategoryRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.NewValueName))
        {
            return Task.CompletedTask;
        }

        var names = SplitValues(row.NewValueName);
        return RunAsync(async () =>
        {
            // Jeden Wert einzeln: ein Duplikat bricht die übrigen nicht ab, sondern wird gemeldet.
            var skipped = new List<string>();
            foreach (var name in names)
            {
                try
                {
                    await _categories.AddValueAsync(row.Id, name).ConfigureAwait(true);
                }
                catch (InvalidOperationException)
                {
                    skipped.Add(name);
                }
            }

            if (skipped.Count > 0)
            {
                ErrorMessage = $"Schon vorhanden und übersprungen: {string.Join(", ", skipped)}.";
            }
        });
    }

    [RelayCommand]
    private async Task RenameValueAsync(CategoryValueRow? value)
    {
        if (value is null)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Wert umbenennen", $"Neuer Name für den Wert „{value.Name}“ der Kategorie „{value.Owner.Name}“:", value.Name, "Umbenennen").ConfigureAwait(true);
        if (name is null || string.Equals(name.Trim(), value.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(() => _categories.RenameValueAsync(value.Id, name)).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task MoveValueUpAsync(CategoryValueRow? value) => MoveValueAsync(value, -1);

    [RelayCommand]
    private Task MoveValueDownAsync(CategoryValueRow? value) => MoveValueAsync(value, +1);

    private Task MoveValueAsync(CategoryValueRow? value, int delta)
    {
        if (value is null)
        {
            return Task.CompletedTask;
        }

        var order = value.Owner.Values.Select(v => v.Id).ToList();
        return Swap(order, value.Id, delta)
            ? RunAsync(() => _categories.ReorderValuesAsync(value.Owner.Id, order))
            : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteValueAsync(CategoryValueRow? value)
    {
        if (value is null)
        {
            return;
        }

        var message = value.FileCount > 0
            ? $"Der Wert „{value.Name}“ ist {value.FileCountText} zugeordnet; sie haben danach keinen Wert in „{value.Owner.Name}“."
            : $"Der Wert „{value.Name}“ ist keiner Datei zugeordnet.";
        if (!await _dialogs.ConfirmAsync("Wert löschen", message, "Löschen", isDestructive: true).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(() => _categories.DeleteValueAsync(value.Id)).ConfigureAwait(true);
    }

    private static bool Swap(List<int> order, int? id, int delta)
    {
        var index = id is null ? -1 : order.IndexOf(id.Value);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return false;
        }

        (order[index], order[target]) = (order[target], order[index]);
        return true;
    }

    /// <summary>Aktion ausführen, Fachfehler (Duplikat, leerer Name) als InfoBar zeigen, danach neu laden.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = null;
        InfoMessage = null;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kategorie-Aktion fehlgeschlagen");
            ErrorMessage = $"Die Aktion ist fehlgeschlagen: {ex.Message}";
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Tags;
using PhotoViewerV3.Services.Tags;
using PhotoViewerV3.Shell;

namespace PhotoViewerV3.Settings.TagManagement;

/// <summary>Zeile der Tag-Verwaltung: Tag, Zahl der zugewiesenen Dateien, Farbwahl und Position.</summary>
public sealed class TagManagementRow
{
    public TagManagementRow(TagInfo tag, int fileCount, bool canMoveUp, bool canMoveDown)
    {
        Tag = tag;
        FileCount = fileCount;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        ColorChoices = TagPalette.Colors
            .Select(c => new TagColorChoice(this, c, TagBrushes.ForHex(c), string.Equals(c, tag.Color, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public TagInfo Tag { get; }

    public int Id => Tag.Id;

    public string Name => Tag.Name;

    public Brush Brush => TagBrushes.ForHex(Tag.Color);

    public int FileCount { get; }

    public string FileCountText => FileCount == 1 ? "1 Datei" : $"{FileCount:N0} Dateien";

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    /// <summary>Palette (ADR-0021) mit Markierung der aktuellen Farbe.</summary>
    public IReadOnlyList<TagColorChoice> ColorChoices { get; }
}

/// <summary>Ein Farbfeld der Palette in einer Zeile (Klick = Farbe setzen).</summary>
public sealed record TagColorChoice(TagManagementRow Row, string Color, Brush Brush, bool IsCurrent);

/// <summary>
/// Seite „Tags“: Tags anlegen, umbenennen, färben (Palette), sortieren und löschen (mit Bestätigung und Zähler).
/// Alle Schreibzugriffe laufen über <see cref="ITagService"/>; der sendet <c>TagsChangedMessage</c>, auf die Galerie
/// und Lightbox reagieren. Die Liste wird bei jedem Anzeigen der Seite und nach jeder Aktion neu geladen.
/// </summary>
public sealed partial class TagManagementViewModel : ObservableObject
{
    private readonly ITagService _tags;
    private readonly IDialogService _dialogs;
    private readonly ILogger<TagManagementViewModel> _logger;

    public TagManagementViewModel(ITagService tags, IDialogService dialogs, ILogger<TagManagementViewModel> logger)
    {
        _tags = tags;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<TagManagementRow> Tags { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _newTagName = string.Empty;

    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _hasNoTags = true;
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
            var tags = await _tags.GetAllAsync().ConfigureAwait(true);
            var counts = await _tags.GetFileCountsAsync().ConfigureAwait(true);
            Tags.Clear();
            for (var i = 0; i < tags.Count; i++)
            {
                Tags.Add(new TagManagementRow(tags[i], counts.GetValueOrDefault(tags[i].Id), i > 0, i < tags.Count - 1));
            }

            HasNoTags = Tags.Count == 0;
            var assignments = Tags.Sum(t => t.FileCount);
            SummaryText = Tags.Count == 0
                ? "Noch keine Tags angelegt."
                : $"{Tags.Count:N0} Tag(s) · {assignments:N0} Zuweisung(en)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tags konnten nicht geladen werden");
            ErrorMessage = $"Tags konnten nicht geladen werden: {ex.Message}";
        }
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewTagName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => RunAsync(async () =>
    {
        var tag = await _tags.CreateAsync(NewTagName).ConfigureAwait(true);
        NewTagName = string.Empty;
        InfoMessage = $"Tag „{tag.Name}“ angelegt.";
    });

    [RelayCommand]
    private async Task RenameAsync(TagManagementRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = await _dialogs.PromptTextAsync("Tag umbenennen", $"Neuer Name für das Tag „{row.Name}“:", row.Name, "Umbenennen").ConfigureAwait(true);
        if (name is null || string.Equals(name.Trim(), row.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _tags.RenameAsync(row.Id, name).ConfigureAwait(true);
            InfoMessage = $"Tag „{row.Name}“ heißt jetzt „{name.Trim()}“.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task SetColorAsync(TagColorChoice? choice)
        => choice is null || choice.IsCurrent
            ? Task.CompletedTask
            : RunAsync(() => _tags.SetColorAsync(choice.Row.Id, choice.Color));

    [RelayCommand]
    private Task MoveUpAsync(TagManagementRow? row) => MoveAsync(row, -1);

    [RelayCommand]
    private Task MoveDownAsync(TagManagementRow? row) => MoveAsync(row, +1);

    private Task MoveAsync(TagManagementRow? row, int delta)
    {
        var order = Tags.Select(t => t.Id).ToList();
        var index = row is null ? -1 : order.IndexOf(row.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return Task.CompletedTask;
        }

        (order[index], order[target]) = (order[target], order[index]);
        return RunAsync(() => _tags.ReorderAsync(order));
    }

    [RelayCommand]
    private async Task DeleteAsync(TagManagementRow? row)
    {
        if (row is null)
        {
            return;
        }

        var message = row.FileCount > 0
            ? $"Das Tag „{row.Name}“ ist {row.FileCountText} zugewiesen. Die Zuweisungen werden entfernt, die Dateien selbst bleiben unverändert."
            : $"Das Tag „{row.Name}“ ist keiner Datei zugewiesen.";
        var confirmed = await _dialogs.ConfirmAsync("Tag löschen", message, "Löschen", isDestructive: true).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var removed = await _tags.DeleteAsync(row.Id).ConfigureAwait(true);
            InfoMessage = $"Tag „{row.Name}“ gelöscht ({removed:N0} Zuweisung(en) entfernt).";
        }).ConfigureAwait(true);
    }

    /// <summary>Aktion ausführen, Fachfehler (Duplikat, leerer Name, ungültige Farbe) als InfoBar zeigen, danach neu laden.</summary>
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
            _logger.LogError(ex, "Tag-Aktion fehlgeschlagen");
            ErrorMessage = $"Die Aktion ist fehlgeschlagen: {ex.Message}";
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

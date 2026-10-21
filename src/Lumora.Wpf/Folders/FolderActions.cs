using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Lumora.Folders.Adoption;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Services;
using Lumora.Gallery.Tags;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Operations;
using Lumora.Services.Tags;

namespace Lumora.Folders;

/// <summary>Ergebnis einer Ordner-Aktion für die Anzeige (InfoBar im Ordnerbaum bzw. Ergebnis-Panel der Bibliotheksseite).</summary>
public sealed record FolderActionResult(string Message, bool IsWarning);

/// <summary>
/// Ordner-Aktionen aus dem Ordnerbaum und der Bibliotheksseite (ADR-0034): „In die Bibliothek übernehmen…“ (Dialog,
/// Ausführung, Bericht mit Rest-Dateien) und „… für alle Dateien“ (Bewertung, Favorit, Kategorie, Tags — rekursiv, mit
/// Bestätigung und Anzahl). Dialoge über austauschbare Dienste (Rauchtest-Fakes): <see cref="IFolderAdoptionPicker"/>,
/// <see cref="IGalleryDialogService"/>, <see cref="ITagPickerService"/>, <see cref="ICategoryPickerService"/>.
/// </summary>
public sealed class FolderActions
{
    private const int MaxDetailLines = 50;

    private readonly IFolderAdoptionService _adoption;
    private readonly IFolderAdoptionPicker _picker;
    private readonly IFolderMetadataService _metadata;
    private readonly ITagService _tags;
    private readonly ICategoryService _categories;
    private readonly ITagPickerService _tagPicker;
    private readonly ICategoryPickerService _categoryPicker;
    private readonly IGalleryDialogService _dialogs;
    private readonly ILogger<FolderActions> _logger;

    public FolderActions(
        IFolderAdoptionService adoption,
        IFolderAdoptionPicker picker,
        IFolderMetadataService metadata,
        ITagService tags,
        ICategoryService categories,
        ITagPickerService tagPicker,
        ICategoryPickerService categoryPicker,
        IGalleryDialogService dialogs,
        ILogger<FolderActions> logger)
    {
        _adoption = adoption;
        _picker = picker;
        _metadata = metadata;
        _tags = tags;
        _categories = categories;
        _tagPicker = tagPicker;
        _categoryPicker = categoryPicker;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Zuletzt ausgeführte Übernahme (Rauchtest, Diagnose).</summary>
    public FolderAdoptionOutcome? LastAdoption { get; private set; }

    /// <summary>true, solange eine Übernahme läuft (Ordnerbaum und Bibliotheksseite teilen sich diese Instanz).</summary>
    public bool IsAdopting { get; private set; }

    /// <summary>„Ordner in die Bibliothek übernehmen…“; null bei Abbruch.</summary>
    public async Task<FolderActionResult?> AdoptAsync(string folderPath)
    {
        if (IsAdopting)
        {
            return new FolderActionResult("Es läuft bereits eine Übernahme — bitte warten, bis sie fertig ist.", true);
        }

        IsAdopting = true;
        try
        {
            return await AdoptCoreAsync(folderPath).ConfigureAwait(true);
        }
        finally
        {
            IsAdopting = false;
        }
    }

    private async Task<FolderActionResult?> AdoptCoreAsync(string folderPath)
    {
        var context = await _adoption.GetContextAsync(folderPath).ConfigureAwait(true);
        if (context is null)
        {
            return new FolderActionResult($"„{folderPath}“ liegt in keinem Eingang — übernehmen lässt sich nur ein Ordner aus einem Eingang.", true);
        }

        Lumora.Services.Operations.AdoptionSourceSnapshot snapshot;
        try
        {
            snapshot = await _adoption.ReadSourceAsync(context.SourceFolder).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            return new FolderActionResult(ex.Message, true); // z. B. versteckte Bibliothek im Quellordner (ADR-0049)
        }

        if (snapshot.Root.TotalFileCount == 0)
        {
            return new FolderActionResult($"„{Path.GetFileName(context.SourceFolder)}“ enthält keine Dateien — nichts zu übernehmen.", true);
        }

        var choice = await _picker.PickAsync(context, snapshot).ConfigureAwait(true);
        if (choice is null)
        {
            _logger.LogInformation("Übernahme abgebrochen: {Source}", context.SourceFolder);
            return null;
        }

        var outcome = await _adoption.ExecuteAsync(context, choice.Plan, choice.Options).ConfigureAwait(true);
        LastAdoption = outcome;
        if (outcome.HasProblems)
        {
            var details = outcome.Execution.Items
                .Where(i => !i.Success)
                .Select(i => $"{Path.GetFileName(i.SourcePath)}: {i.Error ?? "unbekannter Fehler"}")
                .Concat(outcome.Execution.RemainingFiles.Select(f => $"bleibt im Quellordner: {f}"))
                .Concat(outcome.Execution.RecycleFailed && outcome.Execution.RecycleNote is { } note ? new[] { note } : Array.Empty<string>())
                .Take(MaxDetailLines)
                .ToList();
            await _dialogs.ShowErrorsAsync("Ordner übernommen — nicht alles", outcome.Summary, details).ConfigureAwait(true);
        }

        return new FolderActionResult(outcome.Summary, outcome.HasProblems);
    }

    /// <summary>„Bewertung für alle Dateien“: 1–5 Sterne, null = Bewertung entfernen.</summary>
    public async Task<FolderActionResult?> RateAsync(int folderId, string folderName, byte? rating)
    {
        var ids = await _metadata.GetFileIdsUnderAsync(folderId).ConfigureAwait(true);
        if (ids.Count == 0)
        {
            return Empty(folderName);
        }

        var what = rating is null ? "keine Bewertung mehr" : rating == 1 ? "1 Stern" : $"{rating} Sterne";
        if (!await ConfirmAsync("Bewertung für alle Dateien", folderName, ids.Count, $"bekommen {what}", "Bewertung setzen").ConfigureAwait(true))
        {
            return null;
        }

        await _metadata.SetRatingAsync(ids, rating).ConfigureAwait(true);
        return new FolderActionResult($"„{folderName}“: {Count(ids.Count)} {(rating is null ? "ohne Bewertung" : $"mit {what}")}.", false);
    }

    /// <summary>„Favorit für alle“ setzen oder entfernen.</summary>
    public async Task<FolderActionResult?> FavoriteAsync(int folderId, string folderName, bool isFavorite)
    {
        var ids = await _metadata.GetFileIdsUnderAsync(folderId).ConfigureAwait(true);
        if (ids.Count == 0)
        {
            return Empty(folderName);
        }

        var what = isFavorite ? "werden Favoriten" : "sind keine Favoriten mehr";
        if (!await ConfirmAsync("Favorit für alle Dateien", folderName, ids.Count, what, isFavorite ? "Als Favorit markieren" : "Favorit entfernen").ConfigureAwait(true))
        {
            return null;
        }

        await _metadata.SetFavoriteAsync(ids, isFavorite).ConfigureAwait(true);
        return new FolderActionResult($"„{folderName}“: {Count(ids.Count)} {what}.", false);
    }

    /// <summary>„Tags für alle…“ — vorhandene Tag-Auswahl mit den Zählern der Dateien im Ordner.</summary>
    public async Task<FolderActionResult?> EditTagsAsync(int folderId, string folderName)
    {
        var ids = await _metadata.GetFileIdsUnderAsync(folderId).ConfigureAwait(true);
        if (ids.Count == 0)
        {
            return Empty(folderName);
        }

        var all = await _tags.GetAllAsync().ConfigureAwait(true);
        var byFile = await _tags.GetTagIdsForFilesAsync(ids).ConfigureAwait(true);
        var counts = new Dictionary<int, int>();
        foreach (var tagIds in byFile.Values)
        {
            foreach (var tagId in tagIds)
            {
                counts[tagId] = counts.GetValueOrDefault(tagId) + 1;
            }
        }

        var request = new TagPickRequest(all, counts, ids.Count, name => _tags.CreateAsync(name));
        var result = await _tagPicker.PickAsync(request, Owner()).ConfigureAwait(true);
        if (result is null || result.IsEmpty)
        {
            return null;
        }

        if (!await ConfirmAsync("Tags für alle Dateien", folderName, ids.Count, "bekommen die geänderten Tags", "Tags ändern").ConfigureAwait(true))
        {
            return null;
        }

        var (assigned, removed) = await _metadata.ChangeTagsAsync(ids, result.AssignTagIds, result.RemoveTagIds).ConfigureAwait(true);
        return new FolderActionResult($"„{folderName}“: Tags bei {Count(ids.Count)} geändert — {assigned:N0} zugewiesen, {removed:N0} entfernt.", false);
    }

    /// <summary>„Kategorie für alle…“ — vorhandene Kategorie-Auswahl (Wert je Kategorie, „gemischt — nicht ändern“).</summary>
    public async Task<FolderActionResult?> EditCategoriesAsync(int folderId, string folderName)
    {
        var categories = await _categories.GetAllAsync().ConfigureAwait(true);
        if (categories.Count == 0)
        {
            return new FolderActionResult("Noch keine Kategorien angelegt — unter „Kategorien“ anlegen.", true);
        }

        var ids = await _metadata.GetFileIdsUnderAsync(folderId).ConfigureAwait(true);
        if (ids.Count == 0)
        {
            return Empty(folderName);
        }

        var values = await _categories.GetValuesForFilesAsync(ids).ConfigureAwait(true);
        var result = await _categoryPicker.PickAsync(new CategoryPickRequest(categories, values, ids), Owner()).ConfigureAwait(true);
        if (result is null || result.IsEmpty)
        {
            return null;
        }

        if (!await ConfirmAsync("Kategorie für alle Dateien", folderName, ids.Count, $"bekommen {result.Changes.Count} geänderte Kategorie(n)", "Kategorie setzen").ConfigureAwait(true))
        {
            return null;
        }

        var changed = 0;
        foreach (var change in result.Changes)
        {
            changed += await _metadata.SetCategoryValueAsync(ids, change.CategoryId, change.ValueId).ConfigureAwait(true);
        }

        return new FolderActionResult($"„{folderName}“: Kategorien bei {Count(ids.Count)} gesetzt ({changed:N0} Zuordnung(en) geändert).", false);
    }

    private Task<bool> ConfirmAsync(string title, string folderName, int count, string what, string primary)
        => _dialogs.ConfirmAsync(title, $"Alle {Count(count)} in „{folderName}“ (mit Unterordnern) {what}.", primary);

    private static FolderActionResult Empty(string folderName) => new($"„{folderName}“ enthält keine Dateien.", true);

    private static string Count(int n) => n == 1 ? "1 Datei" : $"{n:N0} Dateien";

    private static Window? Owner() => Application.Current?.MainWindow;
}

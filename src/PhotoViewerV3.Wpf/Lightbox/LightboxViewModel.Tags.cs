using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PhotoViewerV3.Gallery.Tags;
using PhotoViewerV3.Services.Tags;

namespace PhotoViewerV3.Lightbox;

/// <summary>
/// Tags in der Lightbox (ADR-0021): Anzeige der Tags der aktuellen Datei in der Titelleiste und Bearbeiten per Strg+T
/// über denselben Tag-Auswahl-Dialog wie in der Galerie. Bewusst in eigener Datei, damit der Kern des ViewModels
/// unverändert bleibt. Ohne <see cref="ITagService"/> (Tests) bleibt die Anzeige leer.
/// </summary>
public sealed partial class LightboxViewModel
{
    private ITagService? _tagService;
    private ITagPickerService? _tagPicker;
    private int _tagsVersion;

    /// <summary>Tags der aktuellen Datei in Anzeigereihenfolge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentTags), nameof(CurrentTagsText))]
    private IReadOnlyList<TagInfo> _currentTags = Array.Empty<TagInfo>();

    public bool HasCurrentTags => CurrentTags.Count > 0;

    /// <summary>Tooltip der Tag-Chips.</summary>
    public string CurrentTagsText => CurrentTags.Count == 0
        ? "Keine Tags — Strg+T zum Zuweisen"
        : $"Tags: {string.Join(", ", CurrentTags.Select(t => t.Name))} — Strg+T zum Bearbeiten";

    private void InitTags(ITagService? tags, ITagPickerService? tagPicker)
    {
        _tagService = tags;
        _tagPicker = tagPicker;
        if (tags is null)
        {
            return;
        }

        // Umbenennen/Umfärben/Löschen in der Tag-Verwaltung → Chips der aktuellen Datei neu abbilden.
        WeakReferenceMessenger.Default.Register<TagsChangedMessage>(
            this,
            static (recipient, message) =>
            {
                var vm = (LightboxViewModel)recipient;
                vm.RunOnUi(() => _ = vm.LoadCurrentTagsAsync());
            });
    }

    /// <summary>Tags der aktuellen Datei laden; ältere Ladevorgänge (schnelles Blättern) werden verworfen.</summary>
    private async Task LoadCurrentTagsAsync()
    {
        var item = CurrentItem;
        var version = ++_tagsVersion;
        if (item is null || _tagService is null)
        {
            CurrentTags = Array.Empty<TagInfo>();
            return;
        }

        try
        {
            var all = await _tagService.GetAllAsync().ConfigureAwait(true);
            var map = await _tagService.GetTagIdsForFilesAsync(new[] { item.FileId }).ConfigureAwait(true);
            if (_disposed || version != _tagsVersion)
            {
                return;
            }

            var byId = all.ToDictionary(t => t.Id);
            CurrentTags = map.TryGetValue(item.FileId, out var ids)
                ? ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList()
                : Array.Empty<TagInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tags für File {FileId} konnten nicht geladen werden", item.FileId);
        }
    }

    /// <summary>Strg+T: Tag-Auswahl für die aktuelle Datei; Änderungen über <see cref="ITagService"/> (meldet Metadata).</summary>
    [RelayCommand]
    private async Task EditTagsAsync()
    {
        var item = CurrentItem;
        if (item is null)
        {
            return;
        }

        if (_tagService is null || _tagPicker is null)
        {
            ShowStatus("Tag-Dienst nicht verfügbar — Tags können nicht bearbeitet werden.");
            return;
        }

        var service = _tagService;
        try
        {
            var all = await service.GetAllAsync().ConfigureAwait(true);
            var counts = CurrentTags.ToDictionary(t => t.Id, _ => 1);
            var request = new TagPickRequest(all, counts, 1, name => service.CreateAsync(name));
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            var result = await _tagPicker.PickAsync(request, owner).ConfigureAwait(true);
            if (result is null || result.IsEmpty)
            {
                return;
            }

            var ids = new[] { item.FileId };
            if (result.AssignTagIds.Count > 0)
            {
                await service.AssignAsync(ids, result.AssignTagIds).ConfigureAwait(true);
            }

            if (result.RemoveTagIds.Count > 0)
            {
                await service.RemoveAsync(ids, result.RemoveTagIds).ConfigureAwait(true);
            }

            ShowStatus("Tags gespeichert.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Tags konnten nicht gespeichert werden: {ex.Message}");
            _logger.LogWarning(ex, "Tags bearbeiten fehlgeschlagen für File {FileId}", item.FileId);
        }

        await LoadCurrentTagsAsync().ConfigureAwait(true);
    }
}

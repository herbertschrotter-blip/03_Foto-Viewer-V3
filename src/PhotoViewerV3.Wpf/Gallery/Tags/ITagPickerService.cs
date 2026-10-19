using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using PhotoViewerV3.Services.Tags;

namespace PhotoViewerV3.Gallery.Tags;

/// <summary>
/// Anfrage an die Tag-Auswahl: alle Tags, je Tag die Zahl der ausgewählten Dateien, die es bereits haben
/// (0 = keine, = <paramref name="SelectionCount"/> = alle, dazwischen = gemischt), optional ein Anlegen-Callback.
/// </summary>
public sealed record TagPickRequest(
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyDictionary<int, int> CountsInSelection,
    int SelectionCount,
    Func<string, Task<TagInfo>>? CreateTag);

/// <summary>Ergebnis der Tag-Auswahl: zuzuweisende und zu entfernende TagIds (nur die geänderten).</summary>
public sealed record TagPickResult(IReadOnlyList<int> AssignTagIds, IReadOnlyList<int> RemoveTagIds)
{
    public bool IsEmpty => AssignTagIds.Count == 0 && RemoveTagIds.Count == 0;
}

/// <summary>Tag-Auswahl-Dialog (Strg+T) hinter einem Interface, damit ViewModels und Rauchtest ohne Fenster auskommen.</summary>
public interface ITagPickerService
{
    /// <summary>Zeigt den Dialog; null bei Abbruch.</summary>
    Task<TagPickResult?> PickAsync(TagPickRequest request, Window? owner);
}

/// <summary>Öffnet <see cref="TagPickerDialog"/> modal.</summary>
public sealed class TagPickerService : ITagPickerService
{
    public Task<TagPickResult?> PickAsync(TagPickRequest request, Window? owner)
        => Task.FromResult(TagPickerDialog.Show(owner, request));
}
